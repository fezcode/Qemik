using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Qemik.Core;
using Qemik.Desktop;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Qemik.Tests.TestAppBuilder))]
namespace Qemik.Tests;
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
public sealed class DesktopTests
{
    [AvaloniaFact]
    public void GpuGuestWindowKeepsCustomControlsAndOffersAgentClipboard()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows native GPU hosting.");
        using var manager = new VmManager();
        var vm = new VmConfig { Display = "sdl", Video = "virtio-vga-gl", SharedClipboard = true };
        var window = new GuestWindow(vm, manager, viewSettings: _ => Task.CompletedTask); window.Show(); Layout(window);
        Assert.True(window.UsesNativeGpu); Assert.Single(window.GetVisualDescendants().OfType<NativeGpuSurface>());
        var names = window.GetVisualDescendants().OfType<Button>().Select(b => Avalonia.Automation.AutomationProperties.GetName(b)).ToArray();
        foreach (var name in new[] { "Pause", "Shut down", "Force shutdown", "Fullscreen", "Mounted disks", "Shared folders", "View settings" }) Assert.Contains(name, names);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<CheckBox>(), b => b.IsVisible && b.Content as string == "Fit resolution");
        Assert.Contains(window.GetVisualDescendants().OfType<CheckBox>(), b => b.IsVisible && b.Content as string == "Share text clipboard");
        Assert.Contains("Capture keyboard", names);
        Screenshot(window, "custom-gpu-window-controls"); window.Close();
    }
    [AvaloniaFact]
    public void NativeGpuPresetSelectsCompatibleDisplayAndOffersRecovery()
    {
        var vm = new VmConfig { Name = "GPU fixture" }; using var manager = new VmManager();
        var window = new SettingsWindow(vm, new Preferences(), manager); window.Show(); window.ShowSection("Display"); Layout(window);
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Use host GPU (custom window)").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(window); Assert.Equal("sdl", vm.Display); Assert.Equal("virtio-vga-gl", vm.Video);
        Screenshot(window, "native-gpu-settings");
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Use integrated 2D display").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("qemik", vm.Display); Assert.Equal("virtio-vga", vm.Video); window.Close();
    }
    [AvaloniaFact]
    public async Task RunningSettingsShowStartedHardwareAndLiveMediaWithoutEditing()
    {
        if (Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR") is null) Assert.Skip("Opt in with QEMIK_QEMU_DIR for running settings validation.");
        using var store = new LibraryStore(CoreTests.TestDirectory()); using var manager = new VmManager();
        var prefs = store.LoadPreferences(); prefs.QemuDirectory = Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR")!; store.SavePreferences(prefs);
        var iso = Path.Combine(store.Root, "fixture.iso"); await File.WriteAllBytesAsync(iso, new byte[4096]);
        var vm = new VmConfig { Name = "Read-only settings fixture", MemoryMiB = 256, Cores = 1, Network = "none", ExtraArguments = "-S", Drives = [new() { Path = iso, CdRom = true, Format = "raw", Interface = "ide", ReadOnly = true }] }; store.Save(vm);
        MainWindow? main = null; SettingsWindow? settings = null;
        try
        {
            await manager.StartAsync(vm, prefs);
            vm.MemoryMiB = 8192; vm.SharedClipboard = true; store.Save(vm); // Only future-start settings change.
            await manager.ChangeMediumAsync(vm.Id, "drive0", null);
            var current = await manager.RunningConfigurationAsync(vm.Id); Assert.Equal(256, current.MemoryMiB); Assert.False(current.SharedClipboard); Assert.Empty(current.Drives.Single().Path);
            main = new MainWindow(store, manager); main.Show(); main.ShowVm(vm.Id); Layout(main);
            var open = main.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "View settings"); Assert.True(open.IsEnabled); open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Pump(); settings = Assert.IsType<SettingsWindow>(Assert.Single(main.OwnedWindows)); Assert.True(settings.IsReadOnly);
            foreach (var section in SettingsWindow.Sections)
            {
                settings.ShowSection(section); Layout(settings);
                Assert.All(settings.GetVisualDescendants().OfType<TextBox>(), b => Assert.True(b.IsReadOnly));
                Assert.Empty(settings.GetVisualDescendants().OfType<ComboBox>()); Assert.Empty(settings.GetVisualDescendants().OfType<NumericUpDown>()); Assert.Empty(settings.GetVisualDescendants().OfType<CheckBox>());
                Assert.DoesNotContain(settings.GetVisualDescendants().OfType<Button>(), b => b.Content as string is "Save settings" or "Detach" or "Browse…");
                if (section == "System") { Assert.Contains(settings.GetVisualDescendants().OfType<TextBox>(), b => b.Text == "256"); Screenshot(settings, "running-settings-system"); }
                if (section == "Drives") Assert.Contains(settings.GetVisualDescendants().OfType<TextBox>(), b => b.Text == "Empty drive");
                if (section == "Display") Screenshot(settings, "running-settings-display");
            }
            settings.Close(); await Pump(); Assert.True(manager.IsRunning(vm.Id)); Assert.Equal(8192, store.List().Single().MemoryMiB); Assert.Equal(iso, store.List().Single().Drives.Single().Path);
        }
        finally { settings?.Close(); if (manager.IsRunning(vm.Id)) await manager.ForceStopAsync(vm.Id); main?.Close(); }
    }
    [AvaloniaFact]
    public async Task PasswordFreeFolderUiCreatesLocalShareWithoutWindowsPrompt()
    {
        var root = CoreTests.TestDirectory(); var selected = Path.Combine(root, "Files"); Directory.CreateDirectory(selected);
        await using var service = new LocalFolderSharing(Path.Combine(root, "config"));
        var vm = new VmConfig { Name = "Ubuntu Desktop" }; var window = new HostFoldersWindow(vm, service); window.Show(); await Pump(); Layout(window);
        window.GetVisualDescendants().OfType<TextBox>().Single(b => b.IsReadOnly).Text = selected;
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Share folder…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Pump(); Layout(window);
        var share = Assert.Single(await service.ListAsync(vm.Id)); Assert.StartsWith("dav://10.0.2.2:", share.Address); Assert.Contains("Read-only", share.Access);
        Assert.Empty(window.OwnedWindows);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == share.Address);
        Screenshot(window, "shared-folders-no-password"); window.Close();
    }
    [AvaloniaFact]
    public async Task BlueprintUiCreatesBatchAndPreventsStartingSource()
    {
        using var store = new LibraryStore(CoreTests.TestDirectory()); using var manager = new VmManager();
        var vm = new VmConfig { Name = "Ubuntu blueprint" }; store.Save(vm);
        var window = new MainWindow(store, manager); window.Show(); window.ShowVm(vm.Id); Layout(window);
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Set as blueprint").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Pump(); Assert.True(Assert.Single(store.List()).IsBlueprint); Layout(window);
        Screenshot(window, "blueprint-details");
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Create VM from blueprint").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Pump(); var dialog = Assert.Single(window.OwnedWindows); Layout(dialog);
        dialog.GetVisualDescendants().OfType<NumericUpDown>().Single().Value = 2;
        Screenshot(dialog, "blueprint-copy");
        dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Create machines").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Pump(); Assert.Equal(3, store.List().Count); Assert.Equal(2, store.List().Count(v => !v.IsBlueprint)); Assert.Empty(window.OwnedWindows);
        window.Close();
    }
    [AvaloniaFact]
    public async Task SharedFoldersUiListsAddressesAndConfirmsRemoval()
    {
        var sharing = new FixtureSharing(); var window = new HostFoldersWindow(new VmConfig { Name = "Ubuntu Desktop" }, sharing);
        window.Show(); await Pump(); Layout(window); Screenshot(window, "shared-folders");
        Assert.True(window.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "smb://10.0.2.2/Qemik_fixture_Files");
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Stop sharing…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Pump(); var dialog = Assert.Single(window.OwnedWindows); Layout(dialog);
        dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Stop sharing").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Pump(); Assert.True(sharing.Removed); window.Close();
    }
    private sealed class FixtureSharing : IHostFolderSharing
    {
        public string Account => @"HOST\User";
        public bool Removed { get; private set; }
        public Task<IReadOnlyList<HostFolderShare>> ListAsync(string id) => Task.FromResult<IReadOnlyList<HostFolderShare>>(Removed ? [] : [new("Qemik_fixture_Files", @"D:\Documents", @"HOST\User: Read")]);
        public Task CreateAsync(string id, string name, string path, bool readOnly) => throw new NotSupportedException();
        public Task RemoveAsync(string id, HostFolderShare share) { Removed = true; return Task.CompletedTask; }
    }
    [AvaloniaFact]
    public async Task ClipboardSetupSavesForNextStartWithoutPretendingRunningChannelExists()
    {
        if (Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR") is null) Assert.Skip("Opt in with QEMIK_QEMU_DIR for clipboard setup validation.");
        using var store = new LibraryStore(CoreTests.TestDirectory()); using var manager = new VmManager();
        var prefs = store.LoadPreferences(); prefs.QemuDirectory = Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR")!;
        var vm = new VmConfig { Name = "Clipboard setup fixture", MemoryMiB = 256, Cores = 1, Network = "none", ExtraArguments = "-S" }; store.Save(vm);
        GuestWindow? window = null;
        try
        {
            await manager.StartAsync(vm, prefs);
            window = new GuestWindow(vm, manager, () => { var saved = store.List().Single(); saved.SharedClipboard = true; store.Save(saved); return Task.CompletedTask; });
            window.Show(); Layout(window);
            var toggle = window.GetVisualDescendants().OfType<CheckBox>().Single(b => b.Content as string == "Share text clipboard"); Assert.False(toggle.IsVisible);
            window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Set up clipboard").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Pump(); var dialog = Assert.Single(window.OwnedWindows); Layout(dialog); Screenshot(dialog, "clipboard-setup");
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Enable for next start").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Pump(); Assert.True(store.List().Single().SharedClipboard); Assert.True(manager.IsRunning(vm.Id));
            Assert.False(manager.Session(vm.Id)!.ClipboardChannel); Assert.False(toggle.IsVisible);
            await window.ShowSessionAsync(store.List().Single()); Assert.False(toggle.IsVisible);
            await manager.ForceStopAsync(vm.Id); await manager.StartAsync(store.List().Single(), prefs);
            await window.ShowSessionAsync(store.List().Single()); Layout(window);
            Assert.True(toggle.IsVisible); Assert.True(toggle.IsEnabled); Assert.True(toggle.IsChecked);
            Assert.True(manager.Session(vm.Id)!.ClipboardChannel);
        }
        finally { if (manager.IsRunning(vm.Id)) await manager.ForceStopAsync(vm.Id); window?.Close(); }
    }
    [AvaloniaTheory]
    [InlineData("library")]
    [InlineData("details")]
    [InlineData("guest")]
    public async Task ForceShutdownImmediatelyStopsDisposableGuestWithoutConfirmation(string location)
    {
        if (Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR") is null) Assert.Skip("Opt in with QEMIK_QEMU_DIR for force shutdown UI validation.");
        using var store = new LibraryStore(CoreTests.TestDirectory()); using var manager = new VmManager();
        var prefs = store.LoadPreferences(); prefs.QemuDirectory = Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR")!; store.SavePreferences(prefs);
        var vm = new VmConfig { Name = "Ubuntu Desktop shutdown fixture", MemoryMiB = 256, Cores = 1, Network = "none", ExtraArguments = "-S" }; store.Save(vm);
        Window? window = null;
        try
        {
            await manager.StartAsync(vm, prefs);
            window = location == "guest" ? new GuestWindow(vm, manager) : new MainWindow(store, manager);
            window.Show(); if (location == "details") ((MainWindow)window).ShowVm(vm.Id); Layout(window);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Shut down" || Avalonia.Automation.AutomationProperties.GetName(b) == "Shut down");
            if (location == "library") Screenshot(window, "library-force-shutdown");
            window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Force shutdown").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (manager.IsRunning(vm.Id) && DateTime.UtcNow < deadline) await Pump();
            await Pump(); Assert.False(manager.IsRunning(vm.Id)); Assert.Empty(window.OwnedWindows);
            if (location == "guest") Assert.False(window.IsVisible);
            else
            {
                ((MainWindow)window).ShowLibrary(); Layout(window);
                Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => Avalonia.Automation.AutomationProperties.GetName(b) == "Force shutdown");
            }
        }
        finally { if (manager.IsRunning(vm.Id)) await manager.ForceStopAsync(vm.Id); window?.Close(); }
    }
    [AvaloniaFact]
    public async Task GuestCloseConfirmsBeforeStoppingAndWaitsForProcessExit()
    {
        if (Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR") is null) Assert.Skip("Opt in with QEMIK_QEMU_DIR for guest close validation.");
        using var store = new LibraryStore(CoreTests.TestDirectory()); using var manager = new VmManager();
        var prefs = store.LoadPreferences(); prefs.QemuDirectory = Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR")!;
        var vm = new VmConfig { Name = "Guest close fixture", MemoryMiB = 256, Cores = 1, Network = "none", ExtraArguments = "-S" };
        GuestWindow? window = null;
        try
        {
            await manager.StartAsync(vm, prefs);
            var process = manager.Session(vm.Id)!.Process;
            window = new GuestWindow(vm, manager); window.Show(); Layout(window);
            foreach (var dismissWithCancel in new[] { true, false })
            {
                window.Close(); await Pump();
                var dialog = Assert.Single(window.OwnedWindows); Layout(dialog);
                Assert.Equal("Do you want to force close?", dialog.Title);
                Assert.True(window.IsVisible); Assert.False(process.HasExited);
                window.Close(); await Pump(); Assert.Same(dialog, Assert.Single(window.OwnedWindows));
                if (dismissWithCancel)
                {
                    Screenshot(dialog, "guest-force-close-confirmation");
                    dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else dialog.Close();
                await Pump(); Assert.True(window.IsVisible); Assert.False(process.HasExited); Assert.Empty(window.OwnedWindows);
            }
            bool? exitedWhenWindowClosed = null;
            window.Closed += (_, _) => exitedWhenWindowClosed = process.HasExited;
            window.Close(); await Pump();
            var confirmation = Assert.Single(window.OwnedWindows); Layout(confirmation);
            confirmation.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Force close").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (window.IsVisible && DateTime.UtcNow < deadline) await Pump();
            Assert.False(window.IsVisible); Assert.True(exitedWhenWindowClosed); Assert.False(manager.IsRunning(vm.Id));
        }
        finally
        {
            if (window is not null) foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            await Pump();
            if (manager.IsRunning(vm.Id)) await manager.ForceStopAsync(vm.Id);
            window?.Close();
        }
    }
    [AvaloniaFact]
    public async Task OldGuestCloseApprovalCannotStopReplacementSession()
    {
        if (Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR") is null) Assert.Skip("Opt in with QEMIK_QEMU_DIR for guest close validation.");
        using var store = new LibraryStore(CoreTests.TestDirectory()); using var manager = new VmManager();
        var prefs = store.LoadPreferences(); prefs.QemuDirectory = Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR")!;
        var vm = new VmConfig { Name = "Replacement guest fixture", MemoryMiB = 256, Cores = 1, Network = "none", ExtraArguments = "-S" };
        GuestWindow? window = null;
        try
        {
            await manager.StartAsync(vm, prefs);
            window = new GuestWindow(vm, manager); window.Show(); Layout(window);
            window.Close(); await Pump(); var dialog = Assert.Single(window.OwnedWindows); Layout(dialog);
            await manager.ForceStopAsync(vm.Id); await manager.StartAsync(vm, prefs);
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Force close").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Pump(); Assert.True(manager.IsRunning(vm.Id)); Assert.True(window.IsVisible);
            // Replacing an old display window must not prompt for or stop the new session.
            window.Close(); await Pump(); Assert.False(window.IsVisible); Assert.True(manager.IsRunning(vm.Id));
        }
        finally
        {
            if (window is not null) foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            await Pump();
            if (manager.IsRunning(vm.Id)) await manager.ForceStopAsync(vm.Id);
            window?.Close();
        }
    }
    [AvaloniaFact]
    public async Task MountedMediaDialogShowsLiveDrivesAndConfirmsEjection()
    {
        if (Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR") is null) Assert.Skip("Opt in with QEMIK_QEMU_DIR for live media dialog validation.");
        var prefs = CoreTests.Prefs(CoreTests.TestDirectory()); prefs.QemuDirectory = Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR")!;
        var iso = Path.Combine(prefs.LibraryDirectory, "installer.iso"); await File.WriteAllBytesAsync(iso, new byte[4096]);
        var vm = new VmConfig { Name = "Media controls fixture", MemoryMiB = 256, Cores = 1, Network = "none", ExtraArguments = "-S", Drives = [new() { Path = iso, CdRom = true, ReadOnly = true, Format = "raw", Interface = "ide" }] };
        using var manager = new VmManager(); GuestWindow? window = null;
        try
        {
            await manager.StartAsync(vm, prefs); window = new GuestWindow(vm, manager); window.Show(); Layout(window);
            window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Mounted disks").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Pump(); var dialog = Assert.Single(window.OwnedWindows); Layout(dialog); Screenshot(dialog, "mounted-disks");
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == iso);
            dialog.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Eject").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Pump(); var confirmation = Assert.Single(dialog.OwnedWindows); Layout(confirmation);
            confirmation.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Pump(); Assert.Equal(iso, Assert.Single(await manager.MountedDrivesAsync(vm.Id)).Path);
            dialog.Close();
        }
        finally { if (manager.IsRunning(vm.Id)) await manager.ForceStopAsync(vm.Id); window?.Close(); }
    }
    [AvaloniaFact]
    public async Task DedicatedUbuntuWindowBootsWithRealFramebuffer()
    {
        if (Environment.GetEnvironmentVariable("QEMIK_GUEST_BOOT_CONFIG") is null) Assert.Skip("Opt in with QEMIK_GUEST_BOOT_CONFIG and QEMIK_QEMU_DIR for a four-minute ISO-only guest window check.");
        var original = System.Text.Json.JsonSerializer.Deserialize<VmConfig>(File.ReadAllText(Environment.GetEnvironmentVariable("QEMIK_GUEST_BOOT_CONFIG")!))!;
        var vm = original.Clone(); vm.Id = Guid.NewGuid().ToString("N"); vm.Display = "qemik"; vm.Drives = vm.Drives.Where(d => d.CdRom).ToList(); vm.Audio = "none";
        var prefs = CoreTests.Prefs(CoreTests.TestDirectory()); prefs.QemuDirectory = Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR")!;
        using var manager = new VmManager(); GuestWindow? window = null;
        try
        {
            await manager.StartAsync(vm, prefs); window = new GuestWindow(vm, manager); window.Show(); Layout(window);
            for (var capture = 0; capture < 8; capture++)
            {
                var until = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(50); }
                Layout(window); Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Screenshot(window, $"ubuntu-live-{capture:00}");
                Assert.True(manager.IsRunning(vm.Id));
                if (capture == 2)
                {
                    var surface = window.GetVisualDescendants().OfType<GuestSurface>().Single();
                    surface.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape, PhysicalKey = Avalonia.Input.PhysicalKey.Escape });
                    surface.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyUpEvent, Key = Avalonia.Input.Key.Escape, PhysicalKey = Avalonia.Input.PhysicalKey.Escape });
                }
            }
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("Display disconnected:") == true);
        }
        finally { if (manager.IsRunning(vm.Id)) await manager.ForceStopAsync(vm.Id); window?.Close(); }
    }
    [AvaloniaFact]
    public void GuestWindowRendersScaledDisplayAndPowerControls()
    {
        using var manager = new VmManager(); var window = new GuestWindow(new VmConfig { Name = "Ubuntu guest" }, manager); window.Show(); Layout(window);
        var surface = window.GetVisualDescendants().OfType<GuestSurface>().Single();
        surface.Present(new GuestFrame(2, 1, [0, 100, 210, 0, 100, 210, 0, 0])); Layout(window);
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using (var rendered = new RenderTargetBitmap(new PixelSize((int)surface.Bounds.Width, (int)surface.Bounds.Height)))
        {
            rendered.Render(surface); var pixel = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
            try
            {
                rendered.CopyPixels(new PixelRect(50, (int)surface.Bounds.Height / 2, 1, 1), pixel, 4, 4);
                // Zero RFB padding must not make the visible framebuffer transparent/black.
                Assert.NotEqual(0, System.Runtime.InteropServices.Marshal.ReadInt32(pixel) & 0x00ffffff);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(pixel); }
        }
        Assert.Equal(new Rect(0, 150, 800, 400), GuestSurface.Fit(new Size(800, 700), 2, 1));
        Assert.Equal(0xff0dU, GuestSurface.KeySymbol(Avalonia.Input.PhysicalKey.Enter));
        Assert.Equal((uint)'a', GuestSurface.KeySymbol(Avalonia.Input.PhysicalKey.A));
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Avalonia.Automation.AutomationProperties.GetName(b) == "Fullscreen");
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Avalonia.Automation.AutomationProperties.GetName(b) == "Ctrl+Alt+Del");
        Screenshot(window, "guest-window"); window.Width = 820; Layout(window); Screenshot(window, "guest-window-narrow"); window.Close();
    }
    [AvaloniaFact]
    public void DownloadCatalogShowsExplorerOnlyForExistingImages()
    {
        using var store = new LibraryStore(CoreTests.TestDirectory()); using var manager = new VmManager();
        var image = ImageTests.Fixture();
        File.WriteAllText(Path.Combine(store.Root, "image-catalog.json"), System.Text.Json.JsonSerializer.Serialize(new[] { image }));
        var main = new MainWindow(store, manager); main.Show(); main.ShowImages(); Layout(main);
        Assert.False(main.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Show in Explorer").IsEnabled);
        var path = OsImages.ImagePath(Path.Combine(store.Root, "Images"), image); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture");
        main.ShowImages(); Layout(main);
        Assert.True(main.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Show in Explorer").IsEnabled);
        Assert.Contains(main.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Verify & install…");
        Screenshot(main, "downloaded-images"); main.Close();
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalRequiresExplicitOptInToDeleteFiles(bool delete)
    {
        using var store = new LibraryStore(CoreTests.TestDirectory()); using var manager = new VmManager();
        var disk = Path.Combine(store.Root, "disk.qcow2"); var iso = Path.Combine(store.Root, "installer.iso");
        File.WriteAllText(disk, "disk"); File.WriteAllText(iso, "iso");
        var vm = new VmConfig { Name = "Ubuntu install", Drives = [new() { Path = disk }, new() { Path = iso, CdRom = true, Interface = "ide", Format = "raw", ReadOnly = true }] }; store.Save(vm);
        var main = new MainWindow(store, manager); main.Show(); main.ShowVm(vm.Id); Layout(main);
        Assert.Equal(2, main.GetVisualDescendants().OfType<Button>().Count(b => b.Content as string == "Show in Explorer" && b.IsEnabled));
        main.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Remove from library").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        var dialog = Assert.Single(main.OwnedWindows); Layout(dialog);
        var option = Assert.Single(dialog.GetVisualDescendants().OfType<CheckBox>()); Assert.False(option.IsChecked);
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text!.Contains(iso));
        option.IsChecked = delete; Layout(dialog); Screenshot(dialog, delete ? "remove-with-files" : "remove-keep-files");
        dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == (delete ? "Delete files & remove machine" : "Remove from library")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Assert.Empty(store.List()); Assert.Equal(!delete, File.Exists(disk)); Assert.Equal(!delete, File.Exists(iso)); main.Close();
    }
    [AvaloniaFact]
    public async Task InstalledEngineHasItsPathAndExplorerAction()
    {
        if (Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR") is not { } qemu) Assert.Skip("Set QEMIK_QEMU_DIR to inspect an installed engine in the UI.");
        using var store = new LibraryStore(CoreTests.TestDirectory()); using var manager = new VmManager();
        var prefs = store.LoadPreferences(); prefs.QemuDirectory = Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR")!; store.SavePreferences(prefs);
        var window = new MainWindow(store, manager); window.Show(); window.ShowEngine();
        for (var i = 0; i < 200; i++)
        {
            Layout(window);
            if (window.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "Show installed engine in Explorer")) break;
            await Task.Delay(10);
        }
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == prefs.QemuDirectory);
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Show installed engine in Explorer" && b.IsEnabled);
        Screenshot(window, "engine-installed"); window.Close();
    }
    [AvaloniaFact]
    public void ChromeAndExpandedScrollbarsKeepControlsInsideTheViewport()
    {
        using var store = new LibraryStore(CoreTests.TestDirectory()); using var manager = new VmManager();
        var window = new MainWindow(store, manager) { Width = 1020, Height = 680 }; window.Show(); Layout(window);
        Assert.Equal(WindowDecorations.BorderOnly, window.WindowDecorations);
        var create = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "＋  New machine");
        Assert.Equal(VerticalAlignment.Center, create.VerticalContentAlignment);
        var label = create.GetVisualDescendants().OfType<TextBlock>().Single();
        var textCenter = label.TranslatePoint(new Point(label.Bounds.Width / 2, label.Bounds.Height / 2), create)!.Value;
        Assert.InRange(Math.Abs(textCenter.Y - create.Bounds.Height / 2), 0, 2);
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Extent.Height > s.Viewport.Height + 50 && s.Name != "SidebarNavigation");
        Assert.False(scroll.AllowAutoHide);
        var rail = scroll.GetVisualDescendants().OfType<ScrollBar>().First(s => s.Orientation == Orientation.Vertical && s.FindAncestorOfType<ScrollViewer>() == scroll);
        Assert.False(rail.AllowAutoHide); Layout(window);
        var search = scroll.GetVisualDescendants().OfType<TextBox>().Single(t => t.PlaceholderText == "Search your machines…");
        var right = search.TranslatePoint(new Point(search.Bounds.Width, 0), window)!.Value.X;
        var railLeft = rail.TranslatePoint(default, window)!.Value.X;
        Assert.True(right + 8 <= railLeft, $"Content right {right} overlaps rail {railLeft}");
        var viewport = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
        var createBottom = create.TranslatePoint(new Point(0, create.Bounds.Height), window)!.Value.Y;
        Assert.True(createBottom < viewport.TranslatePoint(default, window)!.Value.Y);
        scroll.Offset = new Vector(0, 120); Layout(window);
        Assert.Equal(createBottom, create.TranslatePoint(new Point(0, create.Bounds.Height), window)!.Value.Y);
        Screenshot(window, "compact-scrolled"); window.Close();
    }
    [AvaloniaFact]
    public async Task CreationFlowSavesTheEditedConfiguration()
    {
        using var store = new LibraryStore(CoreTests.TestDirectory()); using var manager = new VmManager();
        var main = new MainWindow(store, manager); main.Show(); Layout(main);
        main.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "＋  New machine").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Pump();
        var wizard = Assert.Single(main.OwnedWindows); Layout(wizard);
        wizard.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "MachineName").Text = "My first machine";
        wizard.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Continue  →").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Pump();
        var editor = Assert.IsType<SettingsWindow>(Assert.Single(main.OwnedWindows)); Layout(editor);
        editor.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Save settings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Pump(); Assert.Equal("My first machine", Assert.Single(store.List()).Name); main.Close();
    }
    private static async Task Pump() { for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); } }
    [AvaloniaFact]
    public void LibraryEngineAndPreferencesRenderWithoutFakeMachines()
    {
        using var store = new LibraryStore(CoreTests.TestDirectory()); using var manager = new VmManager();
        var window = new MainWindow(store, manager); window.Show(); Layout(window);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Make room for another world");
        Screenshot(window, "library-empty");
        window.ShowEngine(); Layout(window); Screenshot(window, "engine");
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Download & verify" && !b.IsEnabled);
        window.ShowPreferences(); Layout(window); Screenshot(window, "preferences");
        window.Close();
    }
    [AvaloniaFact]
    public void EverySettingsSectionRendersAndFieldsUpdateTheDraft()
    {
        using var manager = new VmManager(); var vm = VmConfig.Template("Linux"); vm.Name = "Ubuntu workspace";
        vm.Drives.Add(new() { Path = @"D:\Machines\Ubuntu\system.qcow2" }); vm.PortForwards.Add(new());
        var window = new SettingsWindow(vm, CoreTests.Prefs(), manager); window.Show();
        foreach (var section in SettingsWindow.Sections)
        {
            window.ShowSection(section); Layout(window); Screenshot(window, "settings-" + section.Replace(" & ", "-").ToLowerInvariant());
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == section);
        }
        window.ShowSection("General"); Layout(window);
        var name = window.GetVisualDescendants().OfType<TextBox>().First(); name.Text = "Edited name"; Assert.Equal("Edited name", vm.Name);
        window.Close();
    }
    [AvaloniaFact]
    public void PopulatedLibraryAndVmDetailsRender()
    {
        using var store = new LibraryStore(CoreTests.TestDirectory()); using var manager = new VmManager();
        var vm = VmConfig.Template("Linux"); vm.Name = "Ubuntu workspace"; vm.Description = "A quiet place to build and experiment.";
        vm.Drives.Add(new() { Path = @"D:\Machines\Ubuntu\system.qcow2" }); store.Save(vm);
        var win = VmConfig.Template("Windows"); win.Name = "Windows sandbox"; store.Save(win);
        var window = new MainWindow(store, manager); window.Show(); Layout(window); Screenshot(window, "library-populated");
        var status = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "● Stopped");
        var actions = status.FindAncestorOfType<StackPanel>()!;
        var statusCenter = status.TranslatePoint(new Point(0, status.Bounds.Height / 2), window)!.Value.Y;
        foreach (var button in actions.Children.OfType<Button>())
            Assert.InRange(Math.Abs(statusCenter - button.TranslatePoint(new Point(0, button.Bounds.Height / 2), window)!.Value.Y), 0, 1);
        window.GetVisualDescendants().OfType<Button>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Options").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(window); Screenshot(window, "machine");
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "▶  Start machine");
        window.Close();
    }
    [AvaloniaFact]
    public void CatalogFiltersPublishersAndLabelsNonLtsReleasesCorrectly()
    {
        using var store = new LibraryStore(CoreTests.TestDirectory()); using var manager = new VmManager();
        var fixture = Environment.GetEnvironmentVariable("QEMIK_CATALOG_FIXTURE");
        var fedora = ImageTests.Fixture() with { Id = "fedora-workstation", Name = "Fedora Workstation", Version = "44", FileName = "Fedora-Workstation-Live-44-1.7.x86_64.iso", Url = new Uri("https://download.fedoraproject.org/pub/fedora/linux/releases/44/Workstation/x86_64/iso/Fedora-Workstation-Live-44-1.7.x86_64.iso"), ChecksumUrl = new Uri("https://fedoraproject.org/releases.json") };
        var json = fixture is null ? System.Text.Json.JsonSerializer.Serialize(new[] { ImageTests.Fixture(), fedora }) : File.ReadAllText(fixture);
        File.WriteAllText(Path.Combine(store.Root, "image-catalog.json"), json);
        var main = new MainWindow(store, manager) { Width = 1020, Height = 680 }; main.Show(); main.ShowImages(); Layout(main);
        var filter = Assert.Single(main.GetVisualDescendants().OfType<ComboBox>()); filter.SelectedItem = "Fedora Project"; Layout(main);
        Assert.Contains(main.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Fedora Workstation 44");
        Assert.DoesNotContain(main.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Ubuntu Desktop 26.04.1 LTS");
        Screenshot(main, "fedora-catalog-compact");
        if (fixture is not null)
        {
            foreach (var publisher in new[] { "Linux Mint", "openSUSE", "Debian" })
            {
                main.GetVisualDescendants().OfType<ComboBox>().Single().SelectedItem = publisher; Layout(main);
                Assert.Contains(main.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Download & install…");
                Screenshot(main, publisher.ToLowerInvariant().Replace(' ', '-') + "-catalog");
            }
        }
        main.Close();
    }
    [AvaloniaFact]
    public void ExportBrandArtwork()
    {
        if (Environment.GetEnvironmentVariable("QEMIK_SCREENSHOTS") is not { } root) return;
        Directory.CreateDirectory(root);
        foreach (var size in new[] { 16, 24, 32, 48, 64, 128, 256, 512 })
        {
            var brand = new Brand { Width = size, Height = size }; brand.Measure(new Size(size, size)); brand.Arrange(new Rect(0, 0, size, size));
            using var bitmap = new RenderTargetBitmap(new PixelSize(size, size)); bitmap.Render(brand); bitmap.Save(Path.Combine(root, $"qemik-{size}.png"), new PngBitmapEncoderOptions());
            if (size == 256) bitmap.Save(Path.Combine(root, "qemik.png"), new PngBitmapEncoderOptions());
        }
    }
    private static void Layout(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private static void Screenshot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("QEMIK_SCREENSHOTS") is not { } root) return;
        Directory.CreateDirectory(root); using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height)); bitmap.Render(window); bitmap.Save(Path.Combine(root, name + ".png"), new PngBitmapEncoderOptions());
    }
}
