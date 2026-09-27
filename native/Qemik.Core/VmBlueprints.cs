namespace Qemik.Core;

public sealed partial class VmManager
{
    public async Task SetBlueprintAsync(VmConfig vm, bool value, LibraryStore store)
    {
        var gate = gates.GetOrAdd(vm.Id, _ => new SemaphoreSlim(1)); await gate.WaitAsync();
        try
        {
            if (IsRunning(vm.Id)) throw new InvalidOperationException("Fully shut down the machine before changing its blueprint status.");
            var previous = vm.IsBlueprint; vm.IsBlueprint = value;
            try { store.Save(vm); } catch { vm.IsBlueprint = previous; throw; }
            Changed?.Invoke();
        }
        finally { gate.Release(); }
    }
    public async Task<VmConfig> CloneBlueprintAsync(VmConfig blueprint, Preferences prefs, string name, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var gate = gates.GetOrAdd(blueprint.Id, _ => new SemaphoreSlim(1)); await gate.WaitAsync(ct);
        string? destination = null;
        try
        {
            if (!blueprint.IsBlueprint) throw new InvalidOperationException("Set this machine as a blueprint first.");
            if (IsRunning(blueprint.Id)) throw new InvalidOperationException("Stop the blueprint before copying its disks.");
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Give the new machine a name.");
            QemuCommand.Validate(blueprint);
            var clone = blueprint.Clone(); clone.Id = Guid.NewGuid().ToString("N"); clone.Name = name.Trim(); clone.IsBlueprint = false;
            clone.Created = DateTimeOffset.UtcNow; clone.MacAddress = VmConfig.NewMac(); clone.ExtraArguments = ""; clone.Ephemeral = false;
            // Each copy must have its own host endpoints and cannot inherit advanced disk overrides.
            clone.PortForwards.Clear(); if (clone.Serial == "tcp") clone.Serial = "none"; if (clone.Display == "vnc") clone.Display = "qemik";
            var folder = AppPaths.VmDirectory(prefs, clone.Id);
            if (Directory.Exists(folder)) throw new IOException("The clone destination already exists.");
            Directory.CreateDirectory(folder); destination = folder;
            for (var i = 0; i < clone.Drives.Count; i++)
            {
                ct.ThrowIfCancellationRequested(); var drive = clone.Drives[i];
                if (drive.CdRom) { drive.ReadOnly = true; continue; }
                if (!File.Exists(drive.Path)) throw new FileNotFoundException("A blueprint disk is missing.", drive.Path);
                var target = Path.Combine(folder, $"disk-{i}.qcow2");
                progress?.Report($"Copying disk {i + 1} of {clone.Drives.Count} for {clone.Name}…");
                await ProcessRunner.RunAsync(Path.Combine(prefs.QemuDirectory, "qemu-img.exe"), ["convert", "-f", drive.Format, "-O", "qcow2", drive.Path, target], ct, timeoutSeconds: 3600);
                drive.Path = target; drive.Format = "qcow2";
            }
            var nvram = Path.Combine(AppPaths.VmDirectory(prefs, blueprint.Id), "nvram.fd");
            if (blueprint.Uefi && File.Exists(nvram)) File.Copy(nvram, Path.Combine(folder, "nvram.fd"), false);
            ct.ThrowIfCancellationRequested(); QemuCommand.Validate(clone); return clone;
        }
        catch
        {
            // Only a fresh GUID directory created by this operation is eligible for cleanup.
            if (destination is not null)
            {
                var root = Path.GetFullPath(prefs.LibraryDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (Path.GetFullPath(destination).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(destination, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            throw;
        }
        finally { gate.Release(); }
    }
}
