using Qemik.Core;
using System.Text.Json;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var command = args.FirstOrDefault() ?? "help";
    string? Option(string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
    if (command == "enable-gpu")
    {
        if (System.Diagnostics.Process.GetProcesses().Any(p => { using (p) return p.ProcessName.StartsWith("qemu-system-", StringComparison.OrdinalIgnoreCase); }))
            throw new InvalidOperationException("Shut down QEMU guests before configuring GPU acceleration.");
        var root = Option("--data-dir") ?? AppPaths.DefaultRoot; Directory.CreateDirectory(root);
        using var appLock = new FileStream(Path.Combine(root, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var library = new LibraryStore(root);
        var guest = library.List().Single(v => v.Id == Option("--vm"));
        var original = JsonSerializer.Serialize(guest, new JsonSerializerOptions { WriteIndented = true });
        GraphicsProfiles.UseNativeGpu(guest);
        var backup = Path.Combine(root, "configuration-before-gpu-" + guest.Id + "-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(backup, original); library.Save(guest);
        Console.WriteLine($"Enabled native VirGL/OpenGL graphics for {guest.Name}. Start it in Qemik. Configuration backup: {backup}"); return 0;
    }
    if (command == "share-folder")
    {
        using var library = new LibraryStore(Option("--data-dir"));
        using var appLock = new FileStream(Path.Combine(library.Root, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var guest = library.List().Single(v => v.Id == Option("--vm"));
        await using var sharing = new LocalFolderSharing(library.Root);
        await sharing.CreateAsync(guest.Id, Option("--name") ?? "Files", Option("--folder") ?? throw new ArgumentException("Choose --folder."), !args.Contains("--write"));
        Console.WriteLine("Saved password-free share. Open Qemik, then use Shared folders to copy its Ubuntu address. Existing Windows SMB shares are unchanged."); return 0;
    }
    if (command == "enable-audio")
    {
        var runningQemu = System.Diagnostics.Process.GetProcesses().Any(p => { using (p) return p.ProcessName.StartsWith("qemu-system-", StringComparison.OrdinalIgnoreCase); });
        if (runningQemu) throw new InvalidOperationException("Shut down QEMU guests before configuring audio.");
        using var library = new LibraryStore(Option("--data-dir"));
        var guest = library.List().Single(v => v.Id == Option("--vm"));
        var backup = Path.Combine(library.Root, "configuration-before-audio-" + guest.Id + "-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(backup, JsonSerializer.Serialize(guest, new JsonSerializerOptions { WriteIndented = true }));
        library.EnableAudioPlayback(guest.Id);
        Console.WriteLine($"Enabled Intel HD Audio playback for {guest.Name}. Reopen Qemik before starting the guest so it loads the saved settings. Backup: {backup}"); return 0;
    }
    if (command == "configure-guest")
    {
        using var library = new LibraryStore(Option("--data-dir"));
        using var appLock = new FileStream(Path.Combine(library.Root, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var guest = library.List().Single(v => v.Id == Option("--vm")); var settings = library.LoadPreferences();
        var root = AppPaths.VmDirectory(settings, guest.Id); Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "configuration-before-guest-window-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json"), JsonSerializer.Serialize(guest));
        if (guest.Architecture is "x86_64" or "i386") { guest.Accelerator = await HostAcceleration.BestForX86Async(settings); guest.Cpu = "max"; }
        guest.Display = "qemik"; library.Save(guest);
        await File.WriteAllTextAsync(Path.Combine(root, "machine.qemik.json"), JsonSerializer.Serialize(guest, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Updated {guest.Name}: {guest.Accelerator}, {guest.Cpu}, Qemik guest window. Disk and ISO references preserved."); return 0;
    }
    if (command == "diagnose-boot")
    {
        using var library = new LibraryStore(Option("--data-dir"));
        var original = library.List().Single(v => v.Id == Option("--vm"));
        var settings = library.LoadPreferences(); settings.LibraryDirectory = Path.GetFullPath(Option("--output") ?? "artifacts/boot-diagnostic");
        var guest = original.Clone(); guest.Id = Guid.NewGuid().ToString("N"); guest.Display = "none"; guest.Audio = "none";
        guest.Drives = guest.Drives.Where(d => d.CdRom).ToList(); guest.Serial = "file";
        if (Option("--video") is { } video) guest.Video = video;
        if (Option("--cpu") is { } cpu) guest.Cpu = cpu;
        if (Option("--accel") is { } accel) guest.Accelerator = accel;
        if (args.Contains("--bios")) guest.Uefi = false;
        var output = AppPaths.VmDirectory(settings, guest.Id); Directory.CreateDirectory(output);
        Console.WriteLine("Diagnostic output: " + output);
        using var vmManager = new VmManager();
        try
        {
            await vmManager.StartAsync(guest, settings);
            for (var step = 0; step < int.Parse(Option("--frames") ?? "6"); step++)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), cancellation.Token);
                if (step == 3) await vmManager.Session(guest.Id)!.Qmp!.ExecuteAsync("send-key", new { keys = new[] { new { type = "qcode", data = "esc" } } }, cancellation.Token);
                var file = Path.Combine(output, $"screen-{step:00}.png");
                await vmManager.Session(guest.Id)!.Qmp!.ExecuteAsync("screendump", new { filename = file, format = "png" }, cancellation.Token);
                Console.WriteLine(file);
            }
        }
        finally { if (vmManager.IsRunning(guest.Id)) await vmManager.ForceStopAsync(guest.Id); }
        return 0;
    }
    if (command is not ("catalog" or "prepare-ubuntu")) { Console.WriteLine("Qemik image tools\n  enable-gpu --vm id [--data-dir folder]\n  share-folder --vm id --folder path [--name Files] [--write] [--data-dir folder]\n  enable-audio --vm id [--data-dir folder]\n  catalog\n  prepare-ubuntu [--data-dir folder] [--images-dir folder] [--qemu-dir folder]\n  configure-guest --vm id [--data-dir folder]\n  diagnose-boot --vm id [--data-dir folder] [--output folder] [--frames 6] [--accel tcg|whpx] [--cpu name] [--video device] [--bios]\nprepare-ubuntu downloads and verifies Ubuntu LTS and creates a VM with its installer mounted. configure-guest selects the best available x86 accelerator and dedicated display; close the app first. diagnose-boot starts a disposable ISO-only guest, captures a screen every 20 seconds, then stops it. It never opens existing writable guest disks."); return 0; }
    using var images = new OsImages(); var catalog = await images.RefreshAsync(cancellation.Token);
    foreach (var warning in images.RefreshWarnings) Console.Error.WriteLine(warning);
    if (command == "catalog") { Console.WriteLine(JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true })); return 0; }
    using var store = new LibraryStore(Option("--data-dir")); var prefs = store.LoadPreferences();
    await File.WriteAllTextAsync(Path.Combine(store.Root, "image-catalog.json"), JsonSerializer.Serialize(catalog));
    var engine = await QemuInstallation.DetectAsync(Option("--qemu-dir") ?? prefs.QemuDirectory) ?? throw new InvalidOperationException("Install or locate QEMU before preparing an installation.");
    prefs.QemuDirectory = engine.Directory;
    var image = catalog.Single(i => i.Id == "ubuntu-desktop-lts");
    Console.WriteLine($"Selected {image.DisplayName}\n{image.Url}\n{image.Size} bytes\nSHA-256: {image.Sha256}");
    var last = Environment.TickCount64; string? stage = null;
    var progress = new Progress<ImageProgress>(p => { if (p.Stage != stage || Environment.TickCount64 - last > 5000) { Console.WriteLine($"{p.Stage}: {p.Downloaded / 1048576.0:0} / {p.Total / 1048576.0:0} MiB ({100.0 * p.Downloaded / p.Total:0.0}%)"); last = Environment.TickCount64; stage = p.Stage; } });
    var path = await images.DownloadAsync(image, Option("--images-dir") ?? Path.Combine(store.Root, "Images"), progress, cancellation.Token);
    Console.WriteLine("Creating a new 64 GiB sparse system disk and attaching verified installation media…");
    var vm = await InstallMedia.CreateAsync(prefs, image, path, image.DisplayName, ct: cancellation.Token);
    store.SavePreferences(prefs); store.Save(vm);
    var result = Path.Combine(AppPaths.VmDirectory(prefs, vm.Id), "machine.qemik.json");
    await File.WriteAllTextAsync(result, JsonSerializer.Serialize(vm, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Ready: {vm.Name}\nVM ID: {vm.Id}\nISO: {path}\nVM configuration: {result}\nLibrary: {store.Root}"); return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
