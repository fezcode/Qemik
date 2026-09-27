namespace Qemik.Core;

public static class InstallMedia
{
    public static void Attach(VmConfig vm, OsImage image, string path)
    {
        OsImages.Validate(image);
        if (vm.Architecture != image.Architecture) throw new InvalidOperationException($"This installer requires {image.Architecture}; this machine uses {vm.Architecture}.");
        if (!File.Exists(path)) throw new FileNotFoundException("The downloaded ISO is missing.", path);
        // Preserve existing drives; a repeated attach does not duplicate the optical device.
        var optical = vm.Drives.FirstOrDefault(d => d.CdRom && string.Equals(Path.GetFullPath(d.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        if (optical is not null) vm.Drives.Remove(optical);
        optical ??= new DriveConfig { Path = Path.GetFullPath(path), Format = "raw", Interface = "ide", CdRom = true, ReadOnly = true, Discard = false };
        vm.Drives.Insert(0, optical);
        vm.BootOrder = "dc"; vm.BootMenu = true;
    }
    public static async Task<VmConfig> CreateAsync(Preferences prefs, OsImage image, string path, string name, int diskGiB = 64, CancellationToken ct = default)
    {
        // Callers can only provision with bytes that still match the selected published image.
        await OsImages.VerifyAsync(path, image, ct);
        var vm = VmConfig.Template("Linux"); vm.Name = string.IsNullOrWhiteSpace(name) ? image.DisplayName : name.Trim();
        vm.Description = $"Installer: {image.FileName}\nSource: {image.Url}\nSHA-256: {image.Sha256}";
        vm.MemoryMiB = image.Description.StartsWith("Desktop", StringComparison.Ordinal) ? 8192 : 4096;
        vm.Accelerator = await HostAcceleration.BestForX86Async(prefs);
        var share = Path.Combine(prefs.QemuDirectory, "share");
        var code = Path.Combine(share, "edk2-x86_64-code.fd"); var vars = Path.Combine(share, "edk2-i386-vars.fd");
        if (File.Exists(code) && File.Exists(vars)) { vm.Uefi = true; vm.FirmwareCode = code; vm.FirmwareVarsTemplate = vars; }
        var disk = Path.Combine(AppPaths.VmDirectory(prefs, vm.Id), "system.qcow2");
        await VmManager.CreateDiskAsync(prefs, disk, diskGiB, "qcow2"); vm.Drives.Add(new DriveConfig { Path = disk }); Attach(vm, image, path); return vm;
    }
}
