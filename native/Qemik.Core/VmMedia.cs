namespace Qemik.Core;

public sealed record MountedDrive(string Device, string Path, bool Removable, bool ReadOnly, bool Locked, bool TrayOpen);

public sealed partial class VmManager
{
    public async Task<VmConfig> RunningConfigurationAsync(string id)
    {
        var session = Session(id);
        if (session is not { Active: true, StartedConfiguration: { } started }) throw new InvalidOperationException("This machine is no longer running.");
        var current = started.Clone();
        foreach (var drive in await MountedDrivesAsync(id))
            if (int.TryParse(drive.Device.AsSpan(5), out var index) && index >= 0 && index < current.Drives.Count)
            { current.Drives[index].Path = drive.Path; current.Drives[index].ReadOnly = drive.ReadOnly || current.Drives[index].CdRom; }
        return current;
    }
    private QmpClient RunningControl(string id) => Session(id) is { Active: true, Qmp: { } control } ? control : throw new InvalidOperationException("Start the machine to manage its mounted media.");
    public async Task<IReadOnlyList<MountedDrive>> MountedDrivesAsync(string id)
    {
        var blocks = await RunningControl(id).ExecuteAsync("query-block");
        var drives = new List<MountedDrive>();
        foreach (var block in blocks.EnumerateArray())
        {
            var device = block.GetProperty("device").GetString() ?? "";
            // Exclude private firmware and other internal QEMU block devices.
            if (!device.StartsWith("drive", StringComparison.Ordinal) || !int.TryParse(device.AsSpan(5), out _)) continue;
            var inserted = block.TryGetProperty("inserted", out var media);
            drives.Add(new MountedDrive(device, inserted ? QemuWindowsPath.ForDisplay(media.GetProperty("file").GetString() ?? "") : "",
                block.TryGetProperty("removable", out var removable) && removable.GetBoolean(),
                inserted && media.GetProperty("ro").GetBoolean(),
                block.TryGetProperty("locked", out var locked) && locked.GetBoolean(),
                block.TryGetProperty("tray_open", out var tray) && tray.GetBoolean()));
        }
        return drives;
    }
    public async Task ChangeMediumAsync(string id, string device, string? isoPath)
    {
        var drive = (await MountedDrivesAsync(id)).SingleOrDefault(d => d.Device == device) ?? throw new InvalidOperationException("The drive is no longer attached.");
        if (!drive.Removable) throw new InvalidOperationException("Shut down the guest before changing a hard disk.");
        if (drive.Locked) throw new InvalidOperationException("Ubuntu has locked this disc. Finish using it or eject it inside the guest first.");
        if (isoPath is null) await RunningControl(id).ExecuteAsync("eject", new { device, force = false });
        else
        {
            if (!File.Exists(isoPath) || !string.Equals(System.IO.Path.GetExtension(isoPath), ".iso", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Select an existing ISO image.");
            try { await RunningControl(id).ExecuteAsync("blockdev-change-medium", new Dictionary<string, object> { ["device"] = device, ["filename"] = QemuWindowsPath.ForMonitor(isoPath), ["format"] = "raw", ["read-only-mode"] = "read-only", ["force"] = false }); }
            catch (InvalidOperationException ex) when (isoPath.Any(c => c >= 128)) { throw new InvalidOperationException(ex.Message + " This Windows QEMU build may need an ISO path with ASCII-only names if Windows short filenames are unavailable.", ex); }
        }
    }
}
