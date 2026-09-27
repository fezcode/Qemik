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
    private readonly CancellationTokenSource closing = new();
    private readonly TextBlock status = Muted("Connecting to guest display…");
    private readonly Button pause;
    private readonly DispatcherTimer resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly CheckBox autoResize;
    private readonly TextBlock integration = Muted("Resize the guest manually in Ubuntu Settings → Displays, or enable Fit resolution.");
    private (int Width, int Height) lastRequested;
    private bool resizeAvailable;
    private readonly DispatcherTimer clipboardTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool syncingClipboard;
    private string? lastClipboard;
    private readonly CheckBox shareClipboard;
    private GuestDisplayClient? display;
    private bool connecting;
    public GuestWindow(VmConfig machine, VmManager vmManager)
    {
        vm = machine; manager = vmManager;
        Title = vm.Name + " — Qemik"; Width = 1152; Height = 840; MinWidth = 820; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "qemik.ico"); if (File.Exists(iconPath)) Icon = new WindowIcon(iconPath);
        pause = AsyncButton("Pause", async () => await manager.ControlAsync(vm.Id, manager.State(vm.Id) == "Paused" ? "cont" : "stop"), Error);
        var toolbar = Row(pause, AsyncButton("Shut down", () => manager.ControlAsync(vm.Id, "system_powerdown"), Error),
            AsyncButton("Reset…", async () => { if (await Confirm("Reset this guest?", "Unsaved guest work can be lost.", "Reset")) await manager.ControlAsync(vm.Id, "system_reset"); }, Error),
            Button("Fullscreen", () => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen),
            AsyncButton("Ctrl+Alt+Del", async () => { if (display is not { } d) return; await d.KeyAsync(0xffe3, true); await d.KeyAsync(0xffe9, true); await d.KeyAsync(0xffff, true); await d.KeyAsync(0xffff, false); await d.KeyAsync(0xffe9, false); await d.KeyAsync(0xffe3, false); }, Error),
            AsyncButton("Reconnect", ConnectAsync, Error),
            AsyncButton("Force stop…", async () => { if (await Confirm("Force stop this guest?", "This cuts power immediately. Unsaved guest work can be lost.", "Force stop")) await manager.ForceStopAsync(vm.Id); }, Error, "danger"));
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        var icons = new[] { Icons.Pause, Icons.Power, Icons.Reset, Icons.Fullscreen, Icons.Keyboard, Icons.Reconnect, Icons.Stop };
        var buttons = toolbar.Children.OfType<Button>().ToArray(); toolbar.Children.Clear();
        for (var i = 0; i < buttons.Length; i++)
        {
            var button = buttons[i]; WithIcon(button, (string)button.Content!, icons[i]); button.Margin = new Thickness(0, 0, 8, 6); actions.Children.Add(button);
        }
        var mediaButton = WithIcon(AsyncButton("Mounted disks", ShowMediaAsync, Error), "Mounted disks", Icons.Disk);
        mediaButton.Margin = new Thickness(0, 0, 8, 6); actions.Children.Add(mediaButton);
        autoResize = Check("Fit resolution", false, _ => ScheduleResize());
        autoResize.Margin = new Thickness(0, 0, 14, 6);
        ToolTip.SetTip(autoResize, "Request a guest resolution matching this window, up to 1920×1080. Requires a compatible guest graphics driver.");
        actions.Children.Add(autoResize);
        shareClipboard = Check("Share text clipboard", vm.SharedClipboard, enabled =>
        {
            if (display is { } connected) connected.ClipboardEnabled = enabled && vm.SharedClipboard;
            lastClipboard = null;
        });
        shareClipboard.IsEnabled = vm.SharedClipboard;
        shareClipboard.Margin = new Thickness(0, 0, 8, 6);
        ToolTip.SetTip(shareClipboard, "Enable the guest clipboard channel in machine Settings → Sharing first, and install spice-vdagent inside Ubuntu. Text only; sync runs while this window is active.");
        actions.Children.Add(shareClipboard);
        clipboardTimer.Tick += async (_, _) =>
        {
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
        resizeTimer.Tick += async (_, _) =>
        {
            resizeTimer.Stop();
            if (autoResize.IsChecked != true || display is not { SupportsResize: true } connected) return;
            var target = ((int)Math.Clamp(surface.Bounds.Width, 640, 1920), (int)Math.Clamp(surface.Bounds.Height, 480, 1080));
            if (target == lastRequested) return;
            lastRequested = target;
            try { await connected.ResizeAsync(target.Item1, target.Item2, closing.Token); integration.Text = "Resolution requested; the guest decides when to apply it."; }
            catch (Exception ex) when (!closing.IsCancellationRequested) { integration.Text = ex.Message; }
            catch (Exception) when (closing.IsCancellationRequested) { }
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        layout.Children.Add(new Border { Padding = new Thickness(16, 12, 8, 6), Child = actions });
        Grid.SetRow(surface, 1); layout.Children.Add(surface);
        var footer = Stack(status, integration, Muted("Click the display to type · Ctrl+Alt+G releases input · Closing this window keeps the guest running")); footer.Spacing = 4;
        var bottom = new Border { Background = Brush.Parse("#191E17"), Padding = new Thickness(16, 9), Child = footer }; Grid.SetRow(bottom, 2); layout.Children.Add(bottom);
        Content = layout; Chrome.Frame(this);
        surface.ReleaseRequested += () => pause.Focus(); surface.InputError += Error;
        manager.Changed += OnStateChanged;
        Opened += async (_, _) => await ConnectAsync();
        Deactivated += (_, _) => surface.ReleaseInput();
        Closed += (_, _) => { manager.Changed -= OnStateChanged; resizeTimer.Stop(); clipboardTimer.Stop(); closing.Cancel(); display?.Dispose(); surface.Dispose(); };
    }
    private void ScheduleResize() { resizeTimer.Stop(); if (autoResize.IsChecked == true) resizeTimer.Start(); }
    private void Error(Exception ex) => status.Text = ex.Message;
    public async Task ShowSessionAsync(VmConfig machine) { vm = machine; Title = vm.Name + " — Qemik"; await ConnectAsync(); }
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
        if (manager.Session(vm.Id)?.GuestPort is not { } port || !manager.IsRunning(vm.Id)) { status.Text = "Start this machine with the Qemik guest window display selected."; return; }
        connecting = true;
        try
        {
            surface.ReleaseInput(); display?.Dispose(); display = await GuestDisplayClient.ConnectAsync(port, closing.Token); surface.Connect(display);
            lastRequested = default; resizeAvailable = false;
            lastClipboard = null; display.ClipboardEnabled = vm.SharedClipboard && shareClipboard.IsChecked == true;
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
                if (autoResize.IsChecked == true) integration.Text = connection.ResizeStatus;
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
