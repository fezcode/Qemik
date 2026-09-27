using System.Text.Json;

namespace Qemik.Core;

public sealed record RemovalFile(string Path, long Size, DateTime LastWriteUtc);
public sealed record RemovalPlan(string VmId, IReadOnlyList<RemovalFile> Files, IReadOnlyList<string> Kept, string RuntimeDirectory);

public static class VmRemoval
{
    private static string Full(string path) => Path.GetFullPath(path);
    private static bool Inside(string path, string directory) => Full(path).StartsWith(Full(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool HasLink(string path)
    {
        for (var current = Full(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
    public static RemovalPlan Review(VmConfig vm, Preferences prefs, IEnumerable<VmConfig> all, IEnumerable<string>? backingFiles = null)
    {
        var runtime = AppPaths.VmDirectory(prefs, vm.Id);
        var shared = all.Where(v => v.Id != vm.Id).SelectMany(v => v.Drives.Select(d => d.Path).Concat([v.Kernel, v.Initrd, v.FirmwareCode, v.FirmwareVarsTemplate]))
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(Full).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (backingFiles is not null) shared.UnionWith(backingFiles.Select(Full));
        // Firmware and host executables are never VM-owned, even when mistakenly attached as a drive.
        foreach (var path in new[] { vm.Kernel, vm.Initrd, vm.FirmwareCode, vm.FirmwareVarsTemplate }.Where(p => !string.IsNullOrWhiteSpace(p))) shared.Add(Full(path));
        var candidates = vm.Drives.Select(d => Full(d.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        if (Directory.Exists(runtime))
        {
            if (HasLink(runtime)) kept.Add(runtime + " — linked directory; kept");
            else
            {
                // Enumerate without following junctions/symlinks. No recursive directory deletion.
                var pending = new Stack<string>(); pending.Push(runtime);
                while (pending.Count > 0)
                {
                    var dir = pending.Pop();
                    foreach (var file in Directory.EnumerateFiles(dir)) candidates.Add(Full(file));
                    foreach (var child in Directory.EnumerateDirectories(dir))
                        if (HasLink(child)) kept.Add(child + " — linked directory; kept"); else pending.Push(child);
                }
            }
        }
        var files = new List<RemovalFile>();
        foreach (var path in candidates.Order())
        {
            if (!File.Exists(path)) continue;
            if (HasLink(path)) { kept.Add(path + " — linked file or parent; kept"); continue; }
            if (shared.Contains(path)) { kept.Add(path + " — shared or firmware/boot file; kept"); continue; }
            if (!string.IsNullOrEmpty(prefs.QemuDirectory) && Inside(path, prefs.QemuDirectory)) { kept.Add(path + " — QEMU engine file; kept"); continue; }
            var info = new FileInfo(path); files.Add(new(path, info.Length, info.LastWriteTimeUtc));
        }
        return new(vm.Id, files, kept, runtime);
    }
    public static void DeleteReviewedFiles(RemovalPlan reviewed, VmConfig vm, Preferences prefs, IEnumerable<VmConfig> all, IEnumerable<string>? backingFiles = null)
    {
        if (reviewed.VmId != vm.Id) throw new InvalidOperationException("The removal review belongs to a different machine.");
        var current = Review(vm, prefs, all, backingFiles);
        var eligible = current.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var file in reviewed.Files)
        {
            if (!eligible.TryGetValue(file.Path, out var now) || now.Size != file.Size || now.LastWriteUtc != file.LastWriteUtc)
                throw new IOException("A file changed or became shared after review. Review removal again: " + file.Path);
        }
        foreach (var file in reviewed.Files) File.Delete(file.Path);
        // Remove only genuinely empty directories inside this VM's validated GUID directory.
        if (Directory.Exists(reviewed.RuntimeDirectory) && !HasLink(reviewed.RuntimeDirectory))
            RemoveEmpty(reviewed.RuntimeDirectory);
    }
    public static async Task<HashSet<string>> FindBackingFilesAsync(VmConfig selected, Preferences prefs, IEnumerable<VmConfig> all)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in all.Where(v => v.Id != selected.Id).SelectMany(v => v.Drives).Where(d => !d.CdRom && d.Format != "raw" && File.Exists(d.Path)))
        {
            // Refuse file deletion when another image's backing chain cannot be inspected.
            // -U makes this inspection read-only even if the other VM is running.
            var json = await ProcessRunner.RunAsync(Path.Combine(prefs.QemuDirectory, "qemu-img.exe"), ["info", "-U", "--backing-chain", "--output=json", drive.Path]);
            using var document = JsonDocument.Parse(json);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                foreach (var key in new[] { "filename", "full-backing-filename" })
                    if (item.TryGetProperty(key, out var value) && value.GetString() is { Length: > 0 } path)
                        result.Add(Full(Path.IsPathFullyQualified(path) ? path : Path.Combine(Path.GetDirectoryName(Full(drive.Path))!, path)));
            }
        }
        return result;
    }
    private static void RemoveEmpty(string directory)
    {
        foreach (var child in Directory.EnumerateDirectories(directory)) if (!HasLink(child)) RemoveEmpty(child);
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, false);
    }
}
