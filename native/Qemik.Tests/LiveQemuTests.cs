using Qemik.Core;
using Xunit;

namespace Qemik.Tests;

public sealed class LiveQemuTests
{
    [Fact]
    public async Task NativeGpuDisplayStartsWithoutVncAndKeepsQmpControls()
    {
        if (Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR") is null) Assert.Skip("Opt in for the native SDL GPU display check.");
        var prefs = CoreTests.Prefs(CoreTests.TestDirectory()); prefs.QemuDirectory = Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR")!;
        var vm = new VmConfig { MemoryMiB = 256, Cores = 1, Network = "none", ExtraArguments = "-S" }; GraphicsProfiles.UseNativeGpu(vm);
        using var manager = new VmManager();
        try
        {
            await manager.StartAsync(vm, prefs);
            var session = manager.Session(vm.Id)!;
            Assert.Null(session.GuestPort); Assert.Equal("sdl", session.DisplayBackend);
            Assert.Contains("window-close=off", session.LaunchCommand);
            await manager.ControlAsync(vm.Id, "stop"); Assert.Equal("Paused", manager.State(vm.Id));
            Assert.Equal("virtio-vga-gl", (await manager.RunningConfigurationAsync(vm.Id)).Video);
            await manager.ControlAsync(vm.Id, "cont"); Assert.True(manager.IsRunning(vm.Id));
        }
        finally { if (manager.IsRunning(vm.Id)) await manager.ForceStopAsync(vm.Id); }
    }
    [Theory]
    [InlineData("virtio-vga")]
    [InlineData("virtio-vga-gl")]
    public async Task GuestIntegrationsAndLiveMediaWorkWithoutTouchingUserDisks(string video)
    {
        if (Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR") is null) Assert.Skip("Opt in with QEMIK_QEMU_DIR for graphics, audio, clipboard channel and removable media checks.");
        var prefs = CoreTests.Prefs(CoreTests.TestDirectory()); prefs.QemuDirectory = Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR")!;
        var iso = Path.Combine(prefs.LibraryDirectory, "first.iso"); var replacement = Path.Combine(prefs.LibraryDirectory, "Türkçe second.iso");
        await File.WriteAllBytesAsync(iso, new byte[4096]); await File.WriteAllBytesAsync(replacement, new byte[8192]);
        var disk = Path.Combine(prefs.LibraryDirectory, "blank.qcow2"); await VmManager.CreateDiskAsync(prefs, disk, 1, "qcow2");
        var vm = new VmConfig { Display = "qemik", Video = video, MemoryMiB = 256, Cores = 1, Network = "none", Audio = "intel-hda", SharedClipboard = true, ExtraArguments = "-S",
            Drives = [new() { Path = disk }, new() { Path = iso, CdRom = true, ReadOnly = true, Format = "raw", Interface = "ide" }] };
        vm.Accelerator = await HostAcceleration.BestForX86Async(prefs);
        using var manager = new VmManager();
        try
        {
            await manager.StartAsync(vm, prefs);
            var drives = await manager.MountedDrivesAsync(vm.Id); Assert.Equal(2, drives.Count);
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ChangeMediumAsync(vm.Id, "drive0", null));
            await manager.ChangeMediumAsync(vm.Id, "drive1", null);
            Assert.Empty((await manager.MountedDrivesAsync(vm.Id)).Single(d => d.Device == "drive1").Path);
            await manager.ChangeMediumAsync(vm.Id, "drive1", replacement);
            var mounted = (await manager.MountedDrivesAsync(vm.Id)).Single(d => d.Device == "drive1"); Assert.Equal(replacement, mounted.Path); Assert.True(mounted.ReadOnly);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var display = await GuestDisplayClient.ConnectAsync(manager.Session(vm.Id)!.GuestPort!.Value, deadline.Token); var received = false;
            try { await display.ReadFramesAsync(_ => { received = true; deadline.Cancel(); return Task.CompletedTask; }, deadline.Token); }
            catch (OperationCanceledException) when (received) { }
            Assert.True(received); Assert.True(manager.IsRunning(vm.Id));
            var log = await VmManager.ReadLogAsync(vm, prefs); Assert.DoesNotContain("Could not initialize ADC", log); Assert.DoesNotContain("Could not initialize DAC", log);
            Assert.Equal(4096, new FileInfo(iso).Length); Assert.Equal(8192, new FileInfo(replacement).Length);
        }
        finally { if (manager.IsRunning(vm.Id)) await manager.ForceStopAsync(vm.Id); }
    }
    [Fact]
    public async Task DedicatedDisplayReceivesRealFramesAndReconnectsWithoutStoppingGuest()
    {
        if (Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR") is null) Assert.Skip("Opt in with QEMIK_QEMU_DIR for the guest display integration test.");
        var prefs = CoreTests.Prefs(CoreTests.TestDirectory()); prefs.QemuDirectory = Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR")!;
        var vm = new VmConfig { Display = "qemik", Video = "VGA", MemoryMiB = 256, Cores = 1, Network = "none" };
        using var manager = new VmManager();
        try
        {
            await manager.StartAsync(vm, prefs); var port = manager.Session(vm.Id)!.GuestPort!.Value;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var display = await GuestDisplayClient.ConnectAsync(port, deadline.Token); var received = false;
                try { await display.ReadFramesAsync(async frame => { Assert.True(frame.Pixels.Length > 0); received = true; await display.KeyAsync(0xff1b, true); await display.KeyAsync(0xff1b, false); await display.PointerAsync(10, 10, 0); deadline.Cancel(); }, deadline.Token); }
                catch (OperationCanceledException) when (received) { }
                Assert.True(received); Assert.True(manager.IsRunning(vm.Id));
            }
        }
        finally { if (manager.IsRunning(vm.Id)) await manager.ForceStopAsync(vm.Id); }
    }
    [Fact]
    public async Task DownloadedInstallerIsMountedReadOnlyInRealQemu()
    {
        if (Environment.GetEnvironmentVariable("QEMIK_INSTALL_CONFIG") is null) Assert.Skip("Opt in with QEMIK_INSTALL_CONFIG and QEMIK_QEMU_DIR to check a prepared installer.");
        var original = System.Text.Json.JsonSerializer.Deserialize<VmConfig>(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("QEMIK_INSTALL_CONFIG")!))!;
        var prefs = CoreTests.Prefs(CoreTests.TestDirectory()); prefs.QemuDirectory = Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR")!;
        var vm = original.Clone(); vm.Id = Guid.NewGuid().ToString("N"); vm.Display = "none"; vm.MemoryMiB = 2048; vm.Cores = 2; vm.Network = "none"; vm.Audio = "none";
        // Boot the real ISO in isolation; never open the user's writable system disk.
        vm.Drives = vm.Drives.Where(d => d.CdRom).ToList(); var iso = Assert.Single(vm.Drives);
        using var manager = new VmManager();
        try
        {
            await manager.StartAsync(vm, prefs);
            var blocks = await manager.Session(vm.Id)!.Qmp!.ExecuteAsync("query-block");
            var mounted = blocks.EnumerateArray().Single(b => b.TryGetProperty("inserted", out var inserted) && inserted.GetProperty("file").GetString() == iso.Path);
            Assert.True(mounted.GetProperty("inserted").GetProperty("ro").GetBoolean());
            Assert.False(mounted.GetProperty("tray_open").GetBoolean());
            Assert.True(manager.IsRunning(vm.Id));
        }
        finally { if (manager.IsRunning(vm.Id)) await manager.ForceStopAsync(vm.Id); }
    }
    [Theory]
    [InlineData("virtio")]
    [InlineData("ide")]
    [InlineData("scsi")]
    [InlineData("usb")]
    public async Task DriveControllersStartWithAttachedDisk(string bus)
    {
        if (Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR") is null) Assert.Skip("Opt in with QEMIK_QEMU_DIR for storage controller validation.");
        var prefs = CoreTests.Prefs(CoreTests.TestDirectory()); prefs.QemuDirectory = Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR")!;
        var path = Path.Combine(prefs.LibraryDirectory, "disk.qcow2"); await VmManager.CreateDiskAsync(prefs, path, 1, "qcow2");
        var vm = new VmConfig { Display = "none", MemoryMiB = 256, Cores = 1, Drives = [new DriveConfig { Path = path, Interface = bus }] };
        using var manager = new VmManager();
        try { await manager.StartAsync(vm, prefs); Assert.True(manager.IsRunning(vm.Id)); }
        finally { if (manager.IsRunning(vm.Id)) await manager.ForceStopAsync(vm.Id); }
    }
    [Fact]
    public async Task UefiBootCopiesVariablesAndPreservesTheTemplate()
    {
        if (Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR") is null) Assert.Skip("Opt in with QEMIK_QEMU_DIR for UEFI validation.");
        var prefs = CoreTests.Prefs(CoreTests.TestDirectory()); prefs.QemuDirectory = Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR")!;
        var share = Path.Combine(prefs.QemuDirectory, "share");
        var template = Path.Combine(share, "edk2-i386-vars.fd"); var before = await File.ReadAllBytesAsync(template);
        var vm = new VmConfig { MemoryMiB = 256, Cores = 1, Display = "none", Uefi = true, FirmwareCode = Path.Combine(share, "edk2-x86_64-code.fd"), FirmwareVarsTemplate = template };
        using var manager = new VmManager();
        try { await manager.StartAsync(vm, prefs); Assert.True(manager.IsRunning(vm.Id)); Assert.True(File.Exists(Path.Combine(AppPaths.VmDirectory(prefs, vm.Id), "nvram.fd"))); }
        finally { if (manager.IsRunning(vm.Id)) await manager.ForceStopAsync(vm.Id); }
        Assert.Equal(before, await File.ReadAllBytesAsync(template));
    }
    [Theory]
    [InlineData("x86_64")]
    [InlineData("i386")]
    [InlineData("aarch64")]
    [InlineData("arm")]
    [InlineData("riscv64")]
    [InlineData("ppc")]
    public async Task DefaultArchitectureProfileStarts(string architecture)
    {
        if (Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR") is null) Assert.Skip("Opt in with QEMIK_QEMU_DIR for real architecture smoke tests.");
        var prefs = CoreTests.Prefs(CoreTests.TestDirectory()); prefs.QemuDirectory = Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR")!;
        var vm = new VmConfig { MemoryMiB = 256, Cores = 1, Display = "none" }; vm.SetArchitecture(architecture);
        using var manager = new VmManager();
        try { await manager.StartAsync(vm, prefs); Assert.True(manager.IsRunning(vm.Id)); }
        finally { if (manager.IsRunning(vm.Id)) await manager.ForceStopAsync(vm.Id); }
    }
    [Fact]
    public async Task PublisherDownloadVerifiesAndExtractsForIsolatedTesting()
    {
        if (Environment.GetEnvironmentVariable("QEMIK_DOWNLOAD_TEST") is not { } destination) Assert.Skip("Opt in with QEMIK_DOWNLOAD_TEST and QEMIK_7ZIP. Downloads a publisher installer; never runs it.");
        var root = Environment.GetEnvironmentVariable("QEMIK_DOWNLOAD_TEST")!;
        var sevenZip = Environment.GetEnvironmentVariable("QEMIK_7ZIP") ?? throw new InvalidOperationException("Set QEMIK_7ZIP to a 7z.exe binary.");
        using var service = new QemuInstallation();
        var release = await service.ResolveAsync();
        var path = await service.DownloadAsync(release, root, new Progress<TransferProgress>(), default);
        Assert.True(File.Exists(path));
        await File.WriteAllTextAsync(Path.Combine(root, "release.json"), System.Text.Json.JsonSerializer.Serialize(release));
        await ProcessRunner.RunAsync(sevenZip, ["x", path, "-o" + Path.Combine(root, "qemu"), "-y"], timeoutSeconds: 180);
        Assert.True(File.Exists(Path.Combine(root, "qemu", "qemu-system-x86_64.exe")));
    }
    [Fact]
    public async Task RealQemuBootsRespondsToLifecycleAndSnapshots()
    {
        if (Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR") is null) Assert.Skip("Opt in with QEMIK_QEMU_DIR to test a real QEMU build in an isolated machine directory.");
        var root = CoreTests.TestDirectory(); var prefs = CoreTests.Prefs(root); prefs.QemuDirectory = Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR")!;
        var installation = await QemuInstallation.DetectAsync(prefs.QemuDirectory); Assert.NotNull(installation);
        var disk = Path.Combine(root, "test.qcow2"); await VmManager.CreateDiskAsync(prefs, disk, 1, "qcow2");
        var vm = new VmConfig { Name = "Qemik integration fixture", MemoryMiB = 256, Cores = 1, Display = "none", Video = "VGA", Network = "none", Audio = "none" };
        vm.Drives.Add(new DriveConfig { Path = disk });
        using var manager = new VmManager();
        try
        {
            await manager.StartAsync(vm, prefs); Assert.True(manager.IsRunning(vm.Id));
            var status = await manager.Session(vm.Id)!.Qmp!.ExecuteAsync("query-status"); Assert.True(status.GetProperty("running").GetBoolean());
            await manager.ControlAsync(vm.Id, "stop"); Assert.Equal("Paused", manager.State(vm.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SnapshotAsync(vm, prefs, vm.Drives[0], "-c", "unsafe-running"));
            await manager.ControlAsync(vm.Id, "cont"); Assert.Equal("Running", manager.State(vm.Id));
            await manager.ControlAsync(vm.Id, "system_reset");
            await manager.ControlAsync(vm.Id, "quit"); await manager.Session(vm.Id)!.Process.WaitForExitAsync();
            Assert.False(manager.IsRunning(vm.Id));
            await manager.SnapshotAsync(vm, prefs, vm.Drives[0], "-c", "clean");
            Assert.Contains("clean", await manager.SnapshotAsync(vm, prefs, vm.Drives[0], "-l"));
            await manager.SnapshotAsync(vm, prefs, vm.Drives[0], "-a", "clean");
            await manager.SnapshotAsync(vm, prefs, vm.Drives[0], "-d", "clean");
            Assert.DoesNotContain("clean", await manager.SnapshotAsync(vm, prefs, vm.Drives[0], "-l"));
        }
        finally { if (manager.IsRunning(vm.Id)) await manager.ForceStopAsync(vm.Id); }
    }
}
