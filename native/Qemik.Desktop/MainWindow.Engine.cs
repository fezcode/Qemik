using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Qemik.Core;
using System.Text.Json;
using static Qemik.Desktop.Ui;

namespace Qemik.Desktop;

public sealed partial class MainWindow
{
    private InstallerRelease? release;
    private string? downloadedInstaller;
    private CancellationTokenSource? downloadCancellation;
    private string engineMessage = "Check the publisher for the latest Windows x64 installer, or locate a QEMU installation you already have.";
    private async Task DetectAsync()
    {
        installation = await QemuInstallation.DetectAsync(prefs.QemuDirectory);
        if (installation is null) engineStatus.Text = "Not installed";
        else
        {
            prefs.QemuDirectory = installation.Directory; store.SavePreferences(prefs);
            engineStatus.Text = installation.Version.Split('\n')[0].Replace("QEMU emulator version ", "QEMU ");
        }
        if (location == "engine") ShowEngine();
    }
    public void ShowEngine()
    {
        var details = installation is null
            ? Stack(Heading("Let's give Qemik its engine", 25), Muted("QEMU runs your virtual machines. Install it here or connect an existing installation."))
            : Stack(Heading(installation.Version.Split('\n')[0], 23), Muted(installation.Directory), Muted($"{installation.Architectures.Length} system emulators available  ·  qemu-img {(File.Exists(Path.Combine(installation.Directory, "qemu-img.exe")) ? "found" : "missing")}"));
        var detect = AsyncButton("Detect again", async () => { EnsureIdle(); await DetectAsync(); Notify(installation is null ? "QEMU not found. Browse to its folder or install it below." : "QEMU detected."); }, Error);
        var browse = AsyncButton("Locate QEMU folder", async () =>
        {
            EnsureIdle();
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose your QEMU installation" });
            var path = folders.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return;
            if (!File.Exists(Path.Combine(path, "qemu-system-x86_64.exe"))) throw new InvalidDataException("The selected folder does not contain qemu-system-x86_64.exe.");
            prefs.QemuDirectory = path; await DetectAsync();
        }, Error);
        details.Children.Add(Row(detect, browse));
        if (installation is not null)
            details.Children.Add(AsyncButton("Show installed engine in Explorer", () =>
            {
                var directory = Path.GetFullPath(installation.Directory);
                if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The installed engine folder no longer exists. Detect QEMU again.");
                var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                start.ArgumentList.Add(directory); System.Diagnostics.Process.Start(start);
                return Task.CompletedTask;
            }, Error));
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6 };
        var transfer = Muted(engineMessage);
        var releaseDetails = release is null ? (Control)Muted("No release metadata loaded yet.") : Stack(Field("Installer", Text(release.FileName)), Field("Download", Muted(release.Url.AbsoluteUri)), Field("Size", Text(release.Size.HasValue ? $"{release.Size.Value / 1024.0 / 1024:0.0} MiB" : "Not supplied by publisher")), Field("Publisher SHA-512", Code(release.Sha512)));
        var check = AsyncButton("Check available release", async () =>
        {
            EnsureIdle();
            busy = true;
            try { using var service = new QemuInstallation(); release = await service.ResolveAsync(); downloadedInstaller = null; engineMessage = "Release metadata loaded. Ready to download and verify."; }
            finally { busy = false; }
            ShowEngine();
        }, Error);
        var download = AsyncButton("Download & verify", async () =>
        {
            EnsureIdle();
            if (release is null) return;
            busy = true; downloadCancellation = new CancellationTokenSource();
            try
            {
                using var service = new QemuInstallation();
                var reporter = new Progress<TransferProgress>(p =>
                {
                    progress.IsIndeterminate = !p.Total.HasValue;
                    if (p.Total > 0) progress.Value = 100.0 * p.Downloaded / p.Total.Value;
                    transfer.Text = $"{p.Stage} · {p.Downloaded / 1024.0 / 1024:0.0} MiB" + (p.Total.HasValue ? $" / {p.Total.Value / 1024.0 / 1024:0.0} MiB" : "");
                });
                downloadedInstaller = await service.DownloadAsync(release, Path.Combine(store.Root, "Downloads"), reporter, downloadCancellation.Token);
                engineMessage = "SHA-512 verified. Ready to open the QEMU installation wizard.";
            }
            catch (OperationCanceledException) { engineMessage = "Download cancelled. Partial file removed."; }
            finally { busy = false; downloadCancellation.Dispose(); downloadCancellation = null; ShowEngine(); }
        }, Error, "primary");
        download.IsEnabled = release is not null && !busy;
        var install = AsyncButton("Open QEMU Setup", async () =>
        {
            EnsureIdle();
            if (downloadedInstaller is null || release is null) return;
            if (manager.HasRunning) throw new InvalidOperationException("Stop all machines before updating QEMU.");
            busy = true; transfer.Text = "QEMU Setup is open. Complete or cancel its wizard to continue.";
            try
            {
                var exit = await QemuInstallation.InstallAsync(downloadedInstaller, release.Sha512);
                await DetectAsync();
                engineMessage = $"Setup exited with code {exit}. " + (installation is null ? "QEMU was not detected; setup may have been cancelled. Browse to a custom installation folder if needed." : "Detected: " + installation.Version.Split('\n')[0]);
                if (exit != 0) engineMessage += " Installation was not reported as successful.";
            }
            finally { busy = false; ShowEngine(); }
        }, Error, "primary");
        install.IsEnabled = downloadedInstaller is not null && !busy;
        check.IsEnabled = !busy; browse.IsEnabled = !busy; detect.IsEnabled = !busy;
        var cancel = Button("Cancel download", () => downloadCancellation?.Cancel());
        var installer = Stack(Eyebrow("INSTALL QEMU FOR WINDOWS"), Heading("An engine you can inspect", 23), Muted("Publisher: Stefan Weil / weilnetz.de · Windows x64 · QEMU is GPL-licensed.\nThese upstream Windows builds can include release candidates. Review the publisher's release history before installing."),
            Row(check, Button("Publisher & release history ↗", () => OpenUrl(QemuInstallation.SourceUrl))), releaseDetails,
            Muted("The SHA-512 checksum verifies download integrity against the publisher's HTTPS metadata; it is not proof of a trusted code signature. Windows will show the installer's signing and elevation prompts. Setup handles installation options and license acceptance."),
            Row(download, install, cancel), progress, transfer);
        var diagnostic = Stack(Heading("Host & engine details", 20), Muted($"Host: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\nArchitecture: {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}\nLogical processors: {Environment.ProcessorCount}"),
            Muted("WHPX needs CPU virtualization enabled in firmware and the Windows Hypervisor Platform optional feature. QEMU's accelerator list indicates build support; the VM's launch log confirms whether it works on this host."), Button("Open Windows optional features", () => { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("optionalfeatures.exe") { UseShellExecute = true }); }),
            Code(installation is null ? "Install QEMU to inspect its capabilities." : installation.Version + "\n\n" + installation.Accelerators + "\n\nEmulators:\n" + string.Join(", ", installation.Architectures)));
        if (installation is not null)
        {
            var architecture = "x86_64"; var query = "Machines"; var output = Code("Select a capability and inspect it directly from your QEMU build.");
            diagnostic.Children.Add(Row(Select(architecture, installation.Architectures, s => architecture = s), Select(query, ["Machines", "CPUs", "Devices", "Displays", "Audio backends", "Accelerators"], s => query = s)));
            diagnostic.Children.Add(AsyncButton("Inspect capabilities", async () =>
            {
                var args = query switch { "Machines" => new[] { "-machine", "help" }, "CPUs" => ["-cpu", "help"], "Devices" => ["-device", "help"], "Displays" => ["-display", "help"], "Audio backends" => ["-audiodev", "help"], _ => ["-accel", "help"] };
                output.Text = await ProcessRunner.RunAsync(Path.Combine(prefs.QemuDirectory, $"qemu-system-{architecture}.exe"), args);
            }, Error)); diagnostic.Children.Add(output);
        }
        SetPage("engine", Scroll(Stack(PageHeader("THE ENGINE ROOM", "QEMU engine", "Install, inspect, and keep control of the software behind your machines."), Card(details), Card(installer), Card(diagnostic))));
    }
    public void ShowPreferences()
    {
        var folder = prefs.LibraryDirectory;
        var path = Input(folder, s => folder = s);
        var panel = Stack(PageHeader("MAKE IT YOURS", "Preferences", "Local by design. No account, no background service."),
            Card(Stack(Heading("Storage & library", 20), Field("Machine data folder", path, "Choose this location before creating machines. It holds generated disks, firmware variables and logs."), AsyncButton("Save location", async () =>
            {
                if (manager.HasRunning) throw new InvalidOperationException("Stop all machines before changing the data folder.");
                if (store.List().Count > 0 && !string.Equals(Path.GetFullPath(folder), Path.GetFullPath(prefs.LibraryDirectory), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The library already contains machines. Keep its current folder so firmware variables and runtime data stay attached to those machines. New disks can be created at any path in drive settings.");
                if (!Path.IsPathFullyQualified(folder)) throw new InvalidDataException("Use an absolute folder path.");
                Directory.CreateDirectory(folder); prefs.LibraryDirectory = folder; store.SavePreferences(prefs); Notify("Storage location saved. Existing machine files were not moved."); await Task.CompletedTask;
            }, Error, "primary"), Muted("Library database: " + Path.Combine(store.Root, "library.db")))),
            Card(Stack(Heading("Bring a machine with you", 20), Muted("Import a Qemik JSON configuration. Disk images are referenced at their original paths. A new ID and MAC address are assigned; advanced arguments are cleared for review."), AsyncButton("Import configuration", async () =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import configuration", FileTypeFilter = [new FilePickerFileType("Qemik configuration") { Patterns = ["*.json"] }] });
                if (files.Count == 0) return;
                await using var stream = await files[0].OpenReadAsync();
                if (stream.Length > 1024 * 1024) throw new InvalidDataException("Configuration exceeds the 1 MiB limit.");
                var vm = await JsonSerializer.DeserializeAsync<VmConfig>(stream) ?? throw new InvalidDataException("Invalid configuration.");
                vm.Id = Guid.NewGuid().ToString("N"); vm.MacAddress = VmConfig.NewMac(); vm.ExtraArguments = ""; vm.Name += " (imported)";
                QemuCommand.Validate(vm);
                if (await new SettingsWindow(vm, prefs, manager).ShowDialog<bool>(this)) { store.Save(vm); ShowVm(vm.Id); Notify("Configuration imported. Check disk paths before starting."); }
            }, Error))),
            Card(Stack(Row(new Brand(), Stack(Heading("Qemik", 23), Muted("QEMU + kemik. Virtual machines, with backbone."))), Muted("Built with C# / .NET 10, Avalonia 12 and SQLite — the native Airlift stack.\nVersion " + AppVersion.Current + " · Fezcode"), Row(Button("QEMU documentation ↗", () => OpenUrl("https://www.qemu.org/docs/master/")), Button("UTM inspiration ↗", () => OpenUrl("https://docs.getutm.app/"))),
                Muted("Platform notes: guest display uses QEMU's separate SDL/GTK window or a local VNC viewer. SPICE clipboard/WebDAV integration, guest agents, TPM provisioning and suspend-to-disk are not implemented. VirtFS and USB passthrough depend on your QEMU build. Windows 11 guests need suitable UEFI/TPM configuration; the Windows template does not provision a TPM."))));
        SetPage("preferences", Scroll(panel));
    }
}
