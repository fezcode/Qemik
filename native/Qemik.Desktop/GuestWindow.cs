using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Qemik.Core;
using static Qemik.Desktop.Ui;

namespace Qemik.Desktop;

public sealed partial class GuestWindow : Window
{
    private VmConfig vm;
    private readonly VmManager manager;
    private readonly GuestSurface surface = new();
    private readonly NativeGpuSurface? nativeSurface;
    public bool UsesNativeGpu => nativeSurface is not null;
    private readonly CancellationTokenSource closing = new();
    private readonly TextBlock status = Muted("Connecting to guest display…");
    private readonly Button pause;
    private readonly DispatcherTimer resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly CheckBox autoResize;
    private readonly TextBlock integration = Muted("Waiting for automatic resolution fitting…");
    private readonly GuestAutoResize autoFit = new();
    private bool sendingResize;
    private (int Width, int Height) resolutionLimit = (3840, 2160);
    private bool resizeAvailable;
    private readonly DispatcherTimer clipboardTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool syncingClipboard;
    private string? lastClipboard;
    private readonly CheckBox shareClipboard;
    private readonly Button clipboardSetup;
    private readonly Func<Task>? enableClipboardForNextStart;
    private bool ClipboardChannelAvailable => manager.Session(vm.Id)?.ClipboardChannel ?? vm.SharedClipboard;
    private GuestDisplayClient? display;
    private GuestAgentClipboard? agentClipboard;
    private int agentGeneration;
    private bool ClipboardWindowActive => IsActive || nativeSurface?.IsForeground == true;
    private bool connecting;
    private VmSession? shownSession;
    private bool confirmingClose;
    public GuestWindow(VmConfig machine, VmManager vmManager, Func<Task>? enableClipboardForNextStart = null, Func<Window, Task>? viewSettings = null)
    {
        vm = machine; manager = vmManager; this.enableClipboardForNextStart = enableClipboardForNextStart;
        shownSession = manager.Session(vm.Id);
        if (OperatingSystem.IsWindows() && (manager.Session(vm.Id)?.DisplayBackend ?? vm.Display) == "sdl") nativeSurface = new NativeGpuSurface();
        Title = vm.Name + " — Qemik"; Width = 1152; Height = 840; MinWidth = 820; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "qemik.ico"); if (File.Exists(iconPath)) Icon = new WindowIcon(iconPath);
        pause = AsyncButton("Pause", async () => await manager.ControlAsync(vm.Id, manager.State(vm.Id) == "Paused" ? "cont" : "stop"), Error);
        var toolbar = Row(pause, AsyncButton("Shut down", () => manager.ControlAsync(vm.Id, "system_powerdown"), Error),
            AsyncButton("Force shutdown", async () => { await manager.ForceStopAsync(vm.Id); Close(); }, Error, "danger"),
            AsyncButton("Reset…", async () => { if (await Confirm("Reset this guest?", "Unsaved guest work can be lost.", "Reset")) await manager.ControlAsync(vm.Id, "system_reset"); }, Error),
            Button("Fullscreen", () => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen),
            AsyncButton("Ctrl+Alt+Del", async () =>
            {
                if (UsesNativeGpu && manager.Session(vm.Id)?.Qmp is { } qmp)
                {
                    await qmp.ExecuteAsync("send-key", new { keys = new[] { new { type = "qcode", data = "ctrl" }, new { type = "qcode", data = "alt" }, new { type = "qcode", data = "delete" } } }); return;
                }
                if (display is not { } d) return; await d.KeyAsync(0xffe3, true); await d.KeyAsync(0xffe9, true); await d.KeyAsync(0xffff, true); await d.KeyAsync(0xffff, false); await d.KeyAsync(0xffe9, false); await d.KeyAsync(0xffe3, false);
            }, Error),
            AsyncButton("Reconnect", ConnectAsync, Error));
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        var icons = new[] { Icons.Pause, Icons.Power, Icons.Stop, Icons.Reset, Icons.Fullscreen, Icons.Keyboard, Icons.Reconnect };
        var buttons = toolbar.Children.OfType<Button>().ToArray(); toolbar.Children.Clear();
        ToolTip.SetTip(buttons[2], "Immediately stops QEMU and closes this window. Unsaved guest work is lost.");
        for (var i = 0; i < buttons.Length; i++)
        {
            var button = buttons[i]; WithIcon(button, (string)button.Content!, icons[i]);
        }
        var mediaButton = WithIcon(AsyncButton("Mounted disks", ShowMediaAsync, Error), "Mounted disks", Icons.Disk);
        var foldersButton = WithIcon(AsyncButton("Shared folders", () => new HostFoldersWindow(vm).ShowDialog(this), Error), "Shared folders", Icons.Folder);
        Button? settings = null;
        if (viewSettings is not null)
        {
            settings = WithIcon(AsyncButton("View settings", () => viewSettings(this), Error), "View settings", Icons.Settings);
        }
        autoResize = Check("Fit resolution", true, _ => ScheduleResize());
        ToolTip.SetTip(autoResize, "Request a guest resolution matching this window's pixel size, up to 3840×2160 (4K). Requires a compatible guest graphics driver.");
        var resolutionCap = Select("4K", ["1080p", "1440p", "4K"], value => { resolutionLimit = value switch { "1080p" => (1920, 1080), "1440p" => (2560, 1440), _ => (3840, 2160) }; ScheduleResize(); });
        resolutionCap.MinWidth = 110;
        Avalonia.Automation.AutomationProperties.SetName(resolutionCap, "Resolution cap"); ToolTip.SetTip(resolutionCap, "Resolution cap: lower to 1080p for less rendering and display-copy work. Used while Fit resolution is enabled.");
        var resizeSetup = WithIcon(AsyncButton("Auto-fit setup", ShowResizeSetupAsync, Error), "Auto-fit setup", Icons.Settings);
        shareClipboard = Check("Share text clipboard", vm.SharedClipboard, enabled =>
        {
            if (display is { } connected) connected.ClipboardEnabled = enabled && ClipboardChannelAvailable;
            if (agentClipboard is { } agent) agent.Enabled = enabled && ClipboardWindowActive;
            lastClipboard = null;
        });
        ToolTip.SetTip(shareClipboard, "Enable the guest clipboard channel in machine Settings → Sharing first, and install spice-vdagent inside Ubuntu. Text only; sync runs while this window is active.");
        clipboardSetup = WithIcon(AsyncButton("Set up clipboard", ShowClipboardSetupAsync, Error), "Set up clipboard", Icons.Settings);
        RefreshClipboardControls();
        Button? keyboard = null;
        if (UsesNativeGpu)
        {
            nativeSurface!.KeyboardError += message => status.Text = "Keyboard disconnected: " + message + " Use Reconnect.";
            autoResize.IsVisible = resolutionCap.IsVisible = resizeSetup.IsVisible = false;
            integration.Text = "GPU rendering · Window size is forwarded to Ubuntu automatically";
            keyboard = WithIcon(Button("Capture keyboard", () => nativeSurface!.FocusGuest()), "Capture keyboard", Icons.Keyboard);
        }
        // Wrap whole groups so related controls stay together at smaller widths.
        void AddGroup(string title, params Control?[] controls)
        {
            var row = Row(controls.OfType<Control>().ToArray()); row.Spacing = 8;
            var label = Muted(title); label.FontSize = 11; label.LetterSpacing = .6;
            var group = Stack(label, row); group.Spacing = 6; group.Margin = new Thickness(0, 0, 24, 10);
            actions.Children.Add(group);
        }
        AddGroup("DISPLAY", buttons[4], buttons[6], autoResize, resolutionCap, resizeSetup);
        AddGroup("KEYBOARD", keyboard, buttons[5]);
        AddGroup("DEVICES", mediaButton, foldersButton, settings);
        AddGroup("CLIPBOARD", shareClipboard, clipboardSetup);
        AddGroup("POWER", pause, buttons[1], buttons[3], buttons[2]);
        clipboardTimer.Tick += async (_, _) =>
        {
            if (UsesNativeGpu) { await SyncAgentClipboardAsync(); return; }
            if (syncingClipboard || !IsActive || Clipboard is null || display is not { ClipboardEnabled: true } connected) return;
            syncingClipboard = true;
            try
            {
                var before = lastClipboard; var text = await Clipboard.TryGetTextAsync();
                if (before != lastClipboard || !connected.ClipboardEnabled || !IsActive) return;
                if (text is not null && text != lastClipboard) { await connected.SendClipboardAsync(text, closing.Token); lastClipboard = text; }
            }
            catch (Exception ex) when (!closing.IsCancellationRequested) { integration.Text = ex.Message; }
            catch (Exception) when (closing.IsCancellationRequested) { }
            finally { syncingClipboard = false; }
        };
        clipboardTimer.Start();
        surface.SizeChanged += (_, _) => ScheduleResize();
        ScalingChanged += (_, _) => ScheduleResize();
        resizeTimer.Tick += async (_, _) =>
        {
            if (autoResize.IsChecked != true) { resizeTimer.Stop(); return; }
            if (sendingResize) return;
            if (display is not { SupportsResize: true } connected) { integration.Text = "Waiting for guest resize support. Use Auto-fit setup if this persists."; return; }
            var target = GuestSurface.DesiredResolution(surface.Bounds.Size, RenderScaling, resolutionLimit.Width, resolutionLimit.Height);
            var request = autoFit.ShouldRequest(target, (connected.Width, connected.Height), Environment.TickCount64);
            integration.Text = autoFit.Status;
            if (!request) return;
            sendingResize = true;
            try { await connected.ResizeAsync(target.Item1, target.Item2, closing.Token); }
            catch (Exception ex) when (!closing.IsCancellationRequested) { integration.Text = ex.Message; }
            catch (Exception) when (closing.IsCancellationRequested) { }
            finally { sendingResize = false; }
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var controls = new Border { Name = "GuestControls", Padding = new Thickness(16, 12, 8, 6), Child = actions };
        layout.Children.Add(controls);
        Control viewport = nativeSurface is { } gpu ? gpu : surface;
        Grid.SetRow(viewport, 1); layout.Children.Add(viewport);
        var footer = Stack(status, integration, Muted("Click the display to type · Ctrl+Alt+G releases input · Close asks before forcing the guest to shut down")); footer.Spacing = 4;
        var bottom = new Border { Name = "GuestInfo", Background = Brush.Parse("#191E17"), Padding = new Thickness(16, 9), Child = footer }; Grid.SetRow(bottom, 2); layout.Children.Add(bottom);
        var toggleControls = new Button { Name = "ToggleGuestControls" };
        toggleControls.Classes.Add("chrome");
        var toggleGlyph = Glyph(Icons.Controls, 16);
        toggleGlyph.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, new Avalonia.Data.Binding("Foreground") { Source = toggleControls });
        toggleControls.Content = toggleGlyph;
        void UpdateToggleLabel()
        {
            var label = controls.IsVisible ? "Hide controls and status" : "Show controls and status";
            ToolTip.SetTip(toggleControls, label); Avalonia.Automation.AutomationProperties.SetName(toggleControls, label);
        }
        toggleControls.Click += (_, _) =>
        {
            controls.IsVisible = bottom.IsVisible = !controls.IsVisible;
            UpdateToggleLabel();
            if (!controls.IsVisible)
            {
                if (nativeSurface is { } gpuSurface) gpuSurface.FocusGuest(); else surface.Focus();
            }
        };
        UpdateToggleLabel();
        Content = layout; Chrome.Frame(this, toggleControls);
        surface.ReleaseRequested += () => { if (controls.IsVisible) pause.Focus(); else toggleControls.Focus(); }; surface.InputError += Error;
        manager.Changed += OnStateChanged;
        Opened += async (_, _) => await ConnectAsync();
        Deactivated += (_, _) => surface.ReleaseInput();
        Closing += OnClosing;
        Closed += (_, _) => { manager.Changed -= OnStateChanged; resizeTimer.Stop(); clipboardTimer.Stop(); closing.Cancel(); display?.Dispose(); agentClipboard?.Dispose(); surface.Dispose(); };
    }
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (confirmingClose) { e.Cancel = true; return; }
        if (shownSession is { } session && ReferenceEquals(session, manager.Session(vm.Id)) && manager.IsRunning(vm.Id))
        {
            e.Cancel = true;
            confirmingClose = true;
            _ = ConfirmCloseAsync(session);
            return;
        }
        // Detaching a live SDL window restores QEMU's standalone display. Only detach
        // after exit, or when this window belongs to a replaced, stopped session.
        nativeSurface?.Detach();
    }
    private async Task ConfirmCloseAsync(VmSession session)
    {
        var closeWindow = false;
        try
        {
            if (!await Confirm("Do you want to force close?", "This immediately shuts down the virtual machine. Unsaved guest work will be lost.", "Force close")) return;
            // An approval for the previous guest must never stop a newly started one.
            if (!ReferenceEquals(session, manager.Session(vm.Id))) return;
            await manager.ForceStopAsync(vm.Id);
            closeWindow = true;
        }
        catch (Exception ex) { Error(ex); }
        finally { confirmingClose = false; }
        if (closeWindow) Close();
    }
    private void ScheduleResize() { if (UsesNativeGpu) return; resizeTimer.Stop(); autoFit.Reset(); if (autoResize.IsChecked == true) resizeTimer.Start(); else integration.Text = "Auto-fit off. The current guest resolution is scaled to the window."; }
    private void Error(Exception ex) => status.Text = ex.Message;
    public async Task ShowSessionAsync(VmConfig machine) { var hadChannel = shareClipboard.IsVisible; vm = machine; Title = vm.Name + " — Qemik"; RefreshClipboardControls(); if (!hadChannel && ClipboardChannelAvailable) shareClipboard.IsChecked = true; await ConnectAsync(); }
    private void OnStateChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (closing.IsCancellationRequested) return;
        var paused = manager.State(vm.Id) == "Paused"; WithIcon(pause, paused ? "Resume" : "Pause", paused ? Icons.Play : Icons.Pause);
        if (!manager.IsRunning(vm.Id)) { status.Text = "Guest stopped. Start it again from the library."; display?.Dispose(); display = null; }
        else status.Text = manager.State(vm.Id) + " · " + vm.Accelerator.ToUpperInvariant();
    });
    private async Task ConnectAsync()
    {
        if (connecting || closing.IsCancellationRequested) return;
        shownSession = manager.Session(vm.Id);
        if (nativeSurface is not null)
        {
            connecting = true;
            try
            {
                var session = manager.Session(vm.Id);
                if (session?.Active != true) { status.Text = "Start this machine from the library."; return; }
                await nativeSurface.AttachAsync(session.Process, closing.Token, session.Qmp);
                await ConnectAgentClipboardAsync();
                status.Text = manager.State(vm.Id) + " · " + vm.Accelerator.ToUpperInvariant() + " · Native GPU surface";
            }
            catch (Exception ex) when (!closing.IsCancellationRequested) { Error(ex); }
            catch (Exception) when (closing.IsCancellationRequested) { }
            finally { connecting = false; }
            return;
        }
        if (manager.Session(vm.Id)?.GuestPort is not { } port || !manager.IsRunning(vm.Id)) { status.Text = "Start this machine with the Qemik guest window display selected."; return; }
        connecting = true;
        try
        {
            surface.ReleaseInput(); display?.Dispose(); display = await GuestDisplayClient.ConnectAsync(port, closing.Token); surface.Connect(display);
            autoFit.Reset(); resizeAvailable = false; ScheduleResize();
            lastClipboard = null; display.ClipboardEnabled = ClipboardChannelAvailable && shareClipboard.IsChecked == true;
            var clipboardConnection = display;
            display.ClipboardReceived = async text =>
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (closing.IsCancellationRequested || display != clipboardConnection || !clipboardConnection.ClipboardEnabled || !IsActive || Clipboard is null) return;
                    lastClipboard = text; await Clipboard.SetTextAsync(text);
                });
            };
            status.Text = manager.State(vm.Id) + " · " + vm.Accelerator.ToUpperInvariant() + " · " + display.Width + " × " + display.Height;
            var connected = display; _ = Task.Run(() => ReceiveAsync(connected));
        }
        catch (Exception ex) when (!closing.IsCancellationRequested) { Error(ex); }
        catch (Exception) when (closing.IsCancellationRequested) { }
        finally { connecting = false; }
    }
    private async Task ReceiveAsync(GuestDisplayClient connection)
    {
        try
        {
            await connection.ReadFramesAsync(async frame => await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (closing.IsCancellationRequested || display != connection) return;
                surface.Present(frame); status.Text = manager.State(vm.Id) + " · " + vm.Accelerator.ToUpperInvariant() + $" · {frame.Width} × {frame.Height}";
                if (!resizeAvailable && connection.SupportsResize) { resizeAvailable = true; ScheduleResize(); }
            }), closing.Token);
        }
        catch (Exception ex)
        {
            if (!closing.IsCancellationRequested && display == connection) await Dispatcher.UIThread.InvokeAsync(() => status.Text = manager.IsRunning(vm.Id) ? "Display disconnected: " + ex.Message + " Use Reconnect." : "Guest stopped. Start it again from the library.");
        }
    }
    private async Task<bool> Confirm(string title, string message, string action, Window? owner = null)
    {
        var dialog = new Window { Title = title, Width = 470, Height = 230, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Content = new Border { Padding = new Thickness(24), Child = Stack(Heading(title, 22), Muted(message), Row(Button("Cancel", () => dialog.Close(false)), Button(action, () => dialog.Close(true), "danger"))) };
        Chrome.Frame(dialog); return await dialog.ShowDialog<bool>(owner ?? this);
    }
}
