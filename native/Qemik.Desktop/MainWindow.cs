using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Qemik.Core;
using System.Diagnostics;
using System.Text.Json;
using static Qemik.Desktop.Ui;

namespace Qemik.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly LibraryStore store;
    private readonly VmManager manager;
    private Preferences prefs;
    private readonly ContentControl page = new();
    private readonly StackPanel nav = new() { Spacing = 7 };
    private readonly TextBlock notice = Muted("Ready. Your machines stay on your computer.");
    private readonly TextBlock engineStatus = Muted("Engine not checked");
    private readonly TextBlock breadcrumb = Muted("Workspace  /  Virtual machines");
    private readonly Button preferencesNavigation;
    private string location = "library";
    private string search = "";
    private InstallationInfo? installation;
    private bool busy;
    private bool closed;
    public MainWindow(LibraryStore store, VmManager manager)
    {
        this.store = store; this.manager = manager; prefs = store.LoadPreferences();
        Title = "Qemik — virtual machines, with backbone"; Width = 1280; Height = 840; MinWidth = 1020; MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "qemik.ico");
        if (File.Exists(iconPath)) Icon = new WindowIcon(iconPath);
        Chrome.Extend(this);
        var wordmark = Stack(Heading("qemik", 29), Text("B Y  F E Z C O D E", 8, "#92988D")); wordmark.Spacing = 3;
        var identity = Row(new Brand { Width = 44, Height = 44 }, wordmark);
        var caption = new Border { Name = "SidebarDrag", Background = Brushes.Transparent, Padding = new Thickness(20, 25, 16, 24), Child = identity }; Chrome.Draggable(caption);
        var preferences = preferencesNavigation = NavigationButton("Preferences", Icons.Settings, () => { if (!busy) ShowPreferences(); });
        engineStatus.FontSize = 11; engineStatus.TextTrimming = TextTrimming.CharacterEllipsis; engineStatus.TextWrapping = TextWrapping.NoWrap;
        var engineFooter = Button("", () => { if (!busy) ShowEngine(); }); engineFooter.HorizontalAlignment = HorizontalAlignment.Stretch; engineFooter.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var footerContent = Stack(Columns(Text("Qemik", 12), Text("0.1.0", 10, "#92988D"), "*,Auto"), engineStatus); footerContent.Spacing = 6; engineFooter.Content = footerContent;
        var bottom = Stack(new Border { Height = 1, Background = Brush.Parse("#30362C") }, preferences, engineFooter); bottom.Spacing = 10; bottom.Margin = new Thickness(16, 0, 16, 16);
        var side = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        side.Children.Add(caption); nav.Margin = new Thickness(16, 0, 4, 18); var scrollNav = Scroll(nav); scrollNav.Name = "SidebarNavigation"; Grid.SetRow(scrollNav, 1); side.Children.Add(scrollNav); Grid.SetRow(bottom, 2); side.Children.Add(bottom);
        var shell = new Grid { ColumnDefinitions = new ColumnDefinitions("228,*") };
        shell.Children.Add(new Border { Background = Brush.Parse("#151715"), BorderBrush = Brush.Parse("#30362C"), BorderThickness = new Thickness(0, 0, 1, 0), Child = side });
        var main = new Grid { RowDefinitions = new RowDefinitions("38,52,*,38") }; Grid.SetColumn(main, 1); shell.Children.Add(main);
        main.Children.Add(Chrome.TitleBar(this, null, true));
        var header = new Border { Name = "HeaderBar", Padding = new Thickness(32, 0), BorderBrush = Brush.Parse("#30362C"), BorderThickness = new Thickness(0, 0, 0, 1), Child = breadcrumb }; breadcrumb.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(header, 1); main.Children.Add(header);
        Grid.SetRow(page, 2); main.Children.Add(page);
        notice.FontSize = 11; notice.VerticalAlignment = VerticalAlignment.Center;
        var statusbar = new Border { Padding = new Thickness(32, 0), BorderBrush = Brush.Parse("#30362C"), BorderThickness = new Thickness(0, 1, 0, 0), Child = notice };
        Grid.SetRow(statusbar, 3); main.Children.Add(statusbar); Content = shell; Chrome.KeepInsideScreen(this, shell);
        manager.Changed += OnManagerChanged;
        Opened += async (_, _) => { try { await DetectAsync(); } catch (Exception ex) { Error(ex); } };
        Closing += (_, e) => { if (manager.HasRunning || busy) { e.Cancel = true; Notify("Stop your running machines and finish the current operation before closing Qemik."); } };
        Closed += (_, _) => { closed = true; manager.Changed -= OnManagerChanged; foreach (var guest in guestWindows.Values.ToArray()) guest.Close(); };
        ShowLibrary();
    }
    private void OnManagerChanged() => Dispatcher.UIThread.Post(() => { if (closed) return; if (location == "library") ShowLibrary(); else if (location.StartsWith("vm:")) ShowVm(location[3..]); });
    private void Notify(string text) { notice.Text = text; notice.Foreground = Brush.Parse("#D7F59A"); }
    private void Error(Exception ex) { notice.Text = ex.Message; notice.Foreground = Brush.Parse("#F7B4A9"); }
    internal void ShowSharingError(Exception ex) => Error(ex);
    private void EnsureIdle() { if (busy) throw new InvalidOperationException("Finish or pause the active operation first."); }
    private async Task Run(Func<Task> action) { try { await action(); } catch (Exception ex) { Error(ex); } }
    private void Navigation()
    {
        nav.Children.Clear();
        preferencesNavigation.Classes.Set("active", location == "preferences");
        nav.Children.Add(Eyebrow("WORKSPACE"));
        Add("Virtual machines", Icons.Library, "library", ShowLibrary);
        Add("Download an OS", Icons.Disk, "images", () => ShowImages());
        Add("QEMU engine", Icons.Cpu, "engine", ShowEngine);
        nav.Children.Add(new Border { Height = 16 });
        nav.Children.Add(Eyebrow("YOUR MACHINES"));
        var machines = store.List();
        if (machines.Count == 0) { var hint = Text("Your machines will appear here.", 11, "#778172"); hint.Margin = new Thickness(12, 8); nav.Children.Add(hint); }
        foreach (var vm in machines) Add(vm.Name, Icons.Monitor, "vm:" + vm.Id, () => ShowVm(vm.Id));
        void Add(string title, string icon, string key, Action action)
        {
            var b = NavigationButton(title, icon, () => { if (busy) { Notify("Finish or cancel the current engine operation first."); return; } action(); }); if (location == key) b.Classes.Add("active"); nav.Children.Add(b);
        }
    }
    private void SetPage(string key, Control content)
    {
        // Keep the page heading and its primary action clear of the scrolling viewport.
        if (content is ScrollViewer { Content: Border { Child: StackPanel stack } } scroll && stack.Children.Count > 0)
        {
            var heading = stack.Children[0]; stack.Children.RemoveAt(0);
            var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 24 }; layout.Children.Add(heading); Grid.SetRow(scroll, 1); layout.Children.Add(scroll); content = layout;
        }
        location = key; breadcrumb.Text = "Workspace  /  " + (key == "images" ? "Download an OS" : key == "engine" ? "QEMU engine" : key == "preferences" ? "Preferences" : key.StartsWith("vm:") ? "Virtual machines  /  " + store.List().FirstOrDefault(v => "vm:" + v.Id == key)?.Name : "Virtual machines");
        Navigation(); page.Content = new Border { Padding = new Thickness(32, 26, 24, 24), Child = content };
    }
    private Control PageHeader(string eyebrow, string title, string subtitle, Control? action = null)
    {
        var words = Stack(Eyebrow(eyebrow), Heading(title), Muted(subtitle)); words.Spacing = 9;
        if (action is null) return words;
        action.HorizontalAlignment = HorizontalAlignment.Right; action.VerticalAlignment = VerticalAlignment.Center;
        return Columns(words, action, "*,Auto");
    }
    public void ShowLibrary()
    {
        var all = store.List();
        var searchBox = new TextBox { PlaceholderText = "Search your machines…", Text = search, Width = 260 };
        var cards = new StackPanel { Spacing = 14 };
        void RefreshCards()
        {
            cards.Children.Clear();
            var filtered = all.Where(v => v.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || v.Guest.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (filtered.Length == 0 && all.Count > 0) cards.Children.Add(Card(Muted("No machines match your search.")));
            foreach (var vm in filtered)
            {
                var icon = new Border { Width = 58, Height = 58, CornerRadius = new CornerRadius(14), Background = Brush.Parse(vm.Guest == "Windows" ? "#283D4F" : "#39422C"), Child = Text(vm.Guest == "Windows" ? "⊞" : "◇", 36, "#C5DCC8"), Padding = new Thickness(13, 3) };
                var summary = Stack(Heading(vm.Name, 19), Muted($"{vm.Guest}  ·  {vm.Architecture}  ·  {vm.Cores * vm.Threads} CPUs  ·  {vm.MemoryMiB / 1024.0:0.#} GB")); summary.Spacing = 6;
                var details = Columns(icon, summary, "Auto,*"); summary.VerticalAlignment = VerticalAlignment.Center;
                var primary = vm.IsBlueprint ? AsyncButton("Create VM", () => CreateFromBlueprint(vm), Error, "primary") : AsyncButton(manager.IsRunning(vm.Id) ? "Shut down" : "▶  Start", () => manager.IsRunning(vm.Id) ? manager.ControlAsync(vm.Id, "system_powerdown") : StartVm(vm), Error, "primary");
                var actions = Row(Text("● " + (vm.IsBlueprint ? "Blueprint" : manager.State(vm.Id)), 12, vm.IsBlueprint || manager.IsRunning(vm.Id) ? "#D7F59A" : "#92988D"), Button("Open", () => { if (manager.IsRunning(vm.Id) && manager.Session(vm.Id)?.DisplayBackend is "qemik" or "sdl" or "gtk") OpenGuest(vm); else ShowVm(vm.Id); }), primary);
                actions.VerticalAlignment = VerticalAlignment.Center;
                actions.Children.Insert(2, WithIcon(Button("Options", () => ShowVm(vm.Id)), "Options", Icons.Settings));
                if (manager.IsRunning(vm.Id)) actions.Children.Add(ForceShutdownButton(vm.Id));
                cards.Children.Add(Card(Columns(details, actions, "*,Auto")));
            }
        }
        searchBox.TextChanged += (_, _) => { search = searchBox.Text ?? ""; RefreshCards(); }; RefreshCards();
        var body = Stack(PageHeader("YOUR WORKSPACE", "Virtual machines", "A little space for a whole other system.", Button("＋  New machine", () => _ = Run(NewMachine), "primary")));
        body.Spacing = 26;
        var stats = Row(Text($"{all.Count} machines", 13, "#C2D6C8"), Muted($"/   {all.Count(v => manager.IsRunning(v.Id))} running"));
        body.Children.Add(Columns(stats, searchBox, "*,Auto"));
        if (all.Count == 0)
        {
            var empty = Stack(new Brand { Width = 92, Height = 92, HorizontalAlignment = HorizontalAlignment.Center }, Heading("Make room for another world", 27), Muted("Run a Linux workspace, test Windows, or bring an older system back to life.\nCreate a machine, attach an installer image, and make it yours."), Row(Button("Create your first machine", () => _ = Run(NewMachine), "primary"), Button("Set up QEMU", ShowEngine)));
            foreach (var c in empty.Children) { c.HorizontalAlignment = HorizontalAlignment.Center; if (c is TextBlock t) t.TextAlignment = TextAlignment.Center; }
            empty.Spacing = 20; empty.Margin = new Thickness(20, 28);
            body.Children.Add(Card(empty));
            body.Children.Add(Columns(Card(Stack(Eyebrow("01 / VIRTUALIZE"), Heading("Closer to the metal", 18), Muted("Use Windows Hypervisor Platform for x86 guests when your host and QEMU build support it."))), Card(Stack(Eyebrow("02 / EMULATE"), Heading("Go beyond your hardware", 18), Muted("Explore ARM, RISC-V, PowerPC and x86 with QEMU's software emulation.")))));
        }
        else body.Children.Add(cards);
        body.Children.Add(Muted("Your images. Your settings. All stored locally."));
        SetPage("library", Scroll(body));
    }
    private async Task NewMachine()
    {
        EnsureIdle();
        var dialog = new Window { Title = "Create a virtual machine", Width = 590, Height = 430, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var guest = "Linux"; var name = "Linux";
        var nameBox = Input(name, s => name = s); nameBox.Name = "MachineName";
        var type = Select(guest, ["Linux", "Windows", "Other"], s => { guest = s; nameBox.Text = s == "Other" ? "New virtual machine" : s; });
        dialog.Content = new Border { Padding = new Thickness(30), Child = Stack(Eyebrow("A NEW BEGINNING"), Heading("What will you run?"), Muted("Start with a template. Configure hardware and attach your OS installer in the next step."), Field("Guest system", type), Field("Machine name", nameBox), Row(Button("Cancel", () => dialog.Close()), Button("Continue  →", () => { if (string.IsNullOrWhiteSpace(name)) return; var vm = VmConfig.Template(guest); vm.Name = name.Trim(); dialog.Close(vm); }, "primary"))) };
        ((StackPanel)((Border)dialog.Content).Child!).Children.Insert(3, Button("Download an OS installer instead…", () => { dialog.Close(); ShowImages(); }));
        dialog.Height += 48;
        Chrome.Frame(dialog);
        var result = await dialog.ShowDialog<VmConfig?>(this);
        if (result is not null) { if (await new SettingsWindow(result, prefs, manager).ShowDialog<bool>(this)) { store.Save(result); ShowVm(result.Id); Notify("Machine created. Attach a bootable image before starting."); } }
    }
    private async Task EditVm(VmConfig vm, Window? owner = null)
    {
        if (manager.IsRunning(vm.Id))
        {
            var current = await manager.RunningConfigurationAsync(vm.Id);
            await new SettingsWindow(current, prefs, manager, readOnly: true, launchCommand: manager.Session(vm.Id)?.LaunchCommand).ShowDialog<bool>(owner ?? this); return;
        }
        var copy = vm.Clone();
        if (await new SettingsWindow(copy, prefs, manager).ShowDialog<bool>(this)) { store.Save(copy); ShowVm(copy.Id); Notify("Settings saved."); }
    }
    public void ShowVm(string id)
    {
        var vm = store.List().FirstOrDefault(v => v.Id == id); if (vm is null) { ShowLibrary(); return; }
        var running = manager.IsRunning(id);
        var controls = Row();
        if (vm.IsBlueprint) controls.Children.Add(AsyncButton("Create VM from blueprint", () => CreateFromBlueprint(vm), Error, "primary"));
        else if (!running) controls.Children.Add(AsyncButton("▶  Start machine", () => StartVm(vm), Error, "primary"));
        else
        {
            if (manager.Session(vm.Id)?.DisplayBackend is "qemik" or "sdl" or "gtk") controls.Children.Add(Button("Open guest window", () => OpenGuest(vm), "primary"));
            var paused = manager.State(id) == "Paused";
            controls.Children.Add(AsyncButton(paused ? "▶  Resume" : "Ⅱ  Pause", () => manager.ControlAsync(id, paused ? "cont" : "stop"), Error));
            controls.Children.Add(AsyncButton("Shut down", () => manager.ControlAsync(id, "system_powerdown"), Error));
            controls.Children.Add(AsyncButton("Reset", async () => { if (await Confirm("Reset machine?", "This immediately restarts the guest. Unsaved work can be lost.", "Reset")) await manager.ControlAsync(id, "system_reset"); }, Error));
            controls.Children.Insert(controls.Children.Count - 1, ForceShutdownButton(id));
        }
        var settings = WithIcon(AsyncButton(running ? "View settings" : "Edit settings", () => EditVm(vm), Error), running ? "View settings" : "Edit settings", Icons.Settings);
        var overview = Stack(Card(Stack(Row(Text("● " + manager.State(id), 14, "#D7F59A"), Muted(vm.Architecture + " / " + vm.Accelerator.ToUpperInvariant())), Heading(vm.Name, 32), Muted(vm.Description.Length > 0 ? vm.Description : "Your own machine. Ready when you are."), controls)),
            Columns(Card(Stack(Eyebrow("SYSTEM"), Heading($"{vm.Cores * vm.Threads} virtual CPUs", 20), Muted($"{vm.MemoryMiB / 1024.0:0.#} GB memory · {vm.Cpu}\n{vm.Machine} · {(vm.Uefi ? "UEFI" : "Default firmware")}"))), Card(Stack(Eyebrow("DEVICES"), Heading($"{vm.Drives.Count} attached drives", 20), Muted($"{vm.Video} · {vm.Display.ToUpperInvariant()} display\n{vm.Network} networking · {vm.Audio} sound")))),
            Card(Stack(Heading("Guest display", 18), Muted(vm.Display == "qemik" ? "A dedicated Qemik window shows this guest with keyboard, mouse, fullscreen and power controls. Closing the display keeps the machine running. Use Ctrl+Alt+G to release input." : vm.Display == "vnc" ? $"Connect your VNC viewer to 127.0.0.1:{5900 + vm.VncDisplay}. The endpoint is local to this computer." : vm.Display == "none" ? "This machine has no graphical display. Configure serial output for console access." : "SDL on Windows opens a custom Qemik guest window with a native GPU surface and toolbar. Closing it keeps the guest running; Open reattaches it. GTK uses an external QEMU window."))),
            Card(Stack(Heading("Storage", 18), vm.Drives.Count == 0 ? Muted("No drives attached. Add a disk and an installation ISO in settings.") : Stack(vm.Drives.Select(d => (Control)Stack(Muted($"{(d.CdRom ? "CD/DVD" : d.Interface.ToUpperInvariant())}  ·  {d.Path}"), ExplorerButton(d.Path, "Show in Explorer"))).ToArray()))));
        var tabs = new TabControl();
        var log = Code("Select Refresh to read the latest QEMU output.");
        var logs = Stack(AsyncButton("Refresh output", async () => log.Text = await VmManager.ReadLogAsync(vm, prefs), Error), log);
        tabs.ItemsSource = new[]
        {
            new TabItem { Header = "Overview", Content = Scroll(overview) },
            new TabItem { Header = "Command", Content = Scroll(Stack(Muted("PowerShell preview. The live launch also adds a private loopback QMP control endpoint."), Code(QemuCommand.Build(vm, prefs).Preview), AsyncButton("Copy command", async () => { if (Clipboard is not null) await Clipboard.SetTextAsync(QemuCommand.Build(vm, prefs).Preview); Notify("Command copied."); }, Error))) },
            new TabItem { Header = "Snapshots", Content = Scroll(Snapshots(vm)) },
            new TabItem { Header = "Logs", Content = Scroll(logs) }
        };
        var footer = new WrapPanel();
        footer.Children.Add(Button("Download installer", () => { if (!busy) ShowImages(vm.Id); }));
        footer.Children.Add(AsyncButton("Export configuration", () => ExportVm(vm), Error));
        footer.Children.Add(AsyncButton("Remove from library", () => RemoveVm(vm), Error, "danger"));
        footer.Children.Insert(1, WithIcon(AsyncButton("Shared folders", () => new HostFoldersWindow(vm).ShowDialog(this), Error), "Shared folders", Icons.Folder));
        var blueprintButton = AsyncButton(vm.IsBlueprint ? "Use as regular VM" : "Set as blueprint", () => ChangeBlueprint(vm), Error);
        blueprintButton.IsEnabled = !running; ToolTip.SetTip(blueprintButton, "Fully shut down the machine before changing its blueprint status."); footer.Children.Insert(0, blueprintButton);
        foreach (var action in footer.Children) action.Margin = new Thickness(0, 0, 8, 6);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 24 };
        layout.Children.Add(PageHeader("VIRTUAL MACHINE", vm.Name, vm.Guest + " guest", settings)); Grid.SetRow(tabs, 1); layout.Children.Add(tabs); Grid.SetRow(footer, 2); layout.Children.Add(footer);
        SetPage("vm:" + id, layout);
    }
    private Control Snapshots(VmConfig vm)
    {
        var drives = vm.Drives.Where(d => !d.CdRom && !d.ReadOnly && d.Format == "qcow2").ToArray();
        var panel = Stack(Heading("Disk snapshots", 20), Muted("Offline snapshots preserve one QCOW2 disk. Stop the machine first. Memory, other disks and UEFI variables are not included."));
        if (drives.Length == 0) { panel.Children.Add(Muted("Add a writable QCOW2 disk to use snapshots.")); return panel; }
        var selected = drives[0]; var name = "checkpoint-" + DateTime.Now.ToString("yyyyMMdd-HHmm"); var output = Code("");
        var pick = new ComboBox { ItemsSource = drives.Select(d => d.Path).ToArray(), SelectedIndex = 0 }; pick.SelectionChanged += (_, _) => selected = drives[pick.SelectedIndex];
        panel.Children.Add(Field("Disk", pick)); panel.Children.Add(Field("Snapshot name / ID", Input(name, s => name = s)));
        panel.Children.Add(Row(AsyncButton("List", async () => output.Text = await manager.SnapshotAsync(vm, prefs, selected, "-l"), Error), AsyncButton("Create", async () => { await manager.SnapshotAsync(vm, prefs, selected, "-c", name); output.Text = await manager.SnapshotAsync(vm, prefs, selected, "-l"); }, Error, "primary"), AsyncButton("Restore", async () => { if (await Confirm("Restore disk snapshot?", "Changes since this snapshot will be discarded on the selected disk.", "Restore")) { await manager.SnapshotAsync(vm, prefs, selected, "-a", name); Notify("Disk snapshot restored."); } }, Error), AsyncButton("Delete", async () => { if (await Confirm("Delete snapshot?", "This removes the named snapshot from the selected disk.", "Delete")) { await manager.SnapshotAsync(vm, prefs, selected, "-d", name); output.Text = await manager.SnapshotAsync(vm, prefs, selected, "-l"); } }, Error, "danger")));
        panel.Children.Add(output); return panel;
    }
    private async Task ExportVm(VmConfig vm)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export VM configuration", SuggestedFileName = vm.Name + ".qemik.json", DefaultExtension = "json" });
        if (file is null) return; await using var stream = await file.OpenWriteAsync(); stream.SetLength(0); await JsonSerializer.SerializeAsync(stream, vm, new JsonSerializerOptions { WriteIndented = true }); Notify("Configuration exported. Disk images are referenced, not bundled.");
    }
    private async Task<bool> Confirm(string title, string message, string action)
    {
        var window = new Window { Title = title, Width = 490, Height = 240, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Content = new Border { Padding = new Thickness(28), Child = Stack(Heading(title, 22), Muted(message), Row(Button("Cancel", () => window.Close(false)), Button(action, () => window.Close(true), "danger"))) };
        Chrome.Frame(window);
        return await window.ShowDialog<bool>(this);
    }
    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
