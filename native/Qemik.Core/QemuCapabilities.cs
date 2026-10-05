using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Qemik.Core;

/// <summary>A selectable setting value with an optional human description.</summary>
public sealed record ChoiceOption(string Value, string Description);

/// <summary>
/// What one QEMU system emulator supports, read from its -machine/-cpu/-device help.
/// <see cref="Fallback"/> supplies the architecture defaults when QEMU is unavailable.
/// </summary>
public sealed partial record QemuCapabilities(IReadOnlyList<ChoiceOption> Machines, IReadOnlyList<ChoiceOption> Cpus, IReadOnlyList<string> CpuFlags, IReadOnlyList<ChoiceOption> NetworkDevices, bool FromQemu)
{
    private static readonly ConcurrentDictionary<string, Task<QemuCapabilities>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Task<QemuCapabilities> LoadAsync(string qemuDirectory, string architecture)
    {
        var exe = Path.Combine(qemuDirectory, $"qemu-system-{architecture}.exe");
        if (qemuDirectory.Length == 0 || !File.Exists(exe)) return Task.FromResult(Fallback(architecture));
        var key = exe + "|" + File.GetLastWriteTimeUtc(exe).Ticks;
        var load = Cache.GetOrAdd(key, _ => ReadAsync(exe, architecture));
        // A failed read is not cached, so a later visit can try again.
        load.ContinueWith(t => { if (!t.IsCompletedSuccessfully || !t.Result.FromQemu) Cache.TryRemove(key, out _); }, TaskScheduler.Default);
        return load;
    }

    private static async Task<QemuCapabilities> ReadAsync(string exe, string architecture)
    {
        try
        {
            var machines = ParseMachines(await ProcessRunner.RunAsync(exe, ["-machine", "help"], standardOutputOnly: true));
            var cpuHelp = await ProcessRunner.RunAsync(exe, ["-cpu", "help"], standardOutputOnly: true);
            var network = ParseDevices(await ProcessRunner.RunAsync(exe, ["-device", "help"], standardOutputOnly: true), "Network devices");
            var fallback = Fallback(architecture);
            return new QemuCapabilities(machines.Count > 0 ? machines : fallback.Machines, ParseCpus(cpuHelp) is { Count: > 0 } cpus ? cpus : fallback.Cpus,
                ParseCpuFlags(cpuHelp), network.Count > 0 ? network : fallback.NetworkDevices, machines.Count > 0);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or IOException or System.ComponentModel.Win32Exception) { return Fallback(architecture); }
    }

    public static IReadOnlyList<ChoiceOption> ParseMachines(string help) =>
        help.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0 && !l.EndsWith(':') && !char.IsWhiteSpace(l[0]))
            .Select(l => NameAndRest().Match(l)).Where(m => m.Success).Select(m => new ChoiceOption(m.Groups[1].Value, m.Groups[2].Value.Trim())).ToArray();

    public static IReadOnlyList<ChoiceOption> ParseCpus(string help)
    {
        var options = new List<ChoiceOption>();
        foreach (var raw in help.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { if (options.Count > 0) break; continue; }
            if (!char.IsWhiteSpace(line[0])) { if (options.Count > 0) break; continue; }
            var m = NameAndRest().Match(line.Trim());
            if (m.Success) options.Add(new ChoiceOption(m.Groups[1].Value, m.Groups[2].Value.Trim()));
        }
        return options;
    }

    public static IReadOnlyList<string> ParseCpuFlags(string help)
    {
        var start = help.IndexOf("Recognized CPUID flags:", StringComparison.Ordinal);
        if (start < 0) return [];
        var block = help[(start + "Recognized CPUID flags:".Length)..].Split('\n').Skip(1).TakeWhile(l => l.Trim().Length > 0);
        return block.SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct().ToArray();
    }

    public static IReadOnlyList<ChoiceOption> ParseDevices(string help, string category)
    {
        var lines = help.Split('\n').Select(l => l.TrimEnd()).ToArray();
        var start = Array.IndexOf(lines, category + ":");
        if (start < 0) return [];
        return lines.Skip(start + 1).TakeWhile(l => l.Length > 0 && !l.EndsWith(':')).Select(l => DeviceLine().Match(l)).Where(m => m.Success)
            .Select(m => new ChoiceOption(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : "")).ToArray();
    }

    public static QemuCapabilities Fallback(string architecture)
    {
        ChoiceOption[] O(params string[] values) => values.Select(v => new ChoiceOption(v, "")).ToArray();
        var (machines, cpus) = architecture switch
        {
            "aarch64" => (O("virt", "sbsa-ref", "raspi3b", "raspi4b"), O("cortex-a72", "cortex-a57", "cortex-a53", "cortex-a76", "neoverse-n1", "max")),
            "arm" => (O("virt", "raspi2b", "vexpress-a15"), O("cortex-a15", "cortex-a7", "cortex-a9", "max")),
            "riscv64" => (O("virt", "sifive_u", "spike"), O("rv64", "sifive-u54", "max")),
            "ppc" => (O("mac99", "g3beige", "ppce500"), O("G4", "G3", "7400", "e500")),
            "i386" => (O("pc", "q35", "isapc", "microvm"), O("max", "qemu32", "pentium3", "486", "kvm32")),
            _ => (O("q35", "pc", "microvm", "isapc"), O("max", "host", "qemu64", "Skylake-Client", "Haswell", "EPYC", "Nehalem"))
        };
        string[] flags = architecture is "x86_64" or "i386" ? ["aes", "avx", "avx2", "avx512f", "hypervisor", "pcid", "popcnt", "sse4.1", "sse4.2", "ssse3", "vmx", "svm", "x2apic", "xsave"] : [];
        return new QemuCapabilities(machines, cpus, flags, O("virtio-net-pci", "e1000e", "e1000", "rtl8139", "vmxnet3", "usb-net"), false);
    }

    [GeneratedRegex(@"^(\S+)\s*(.*)$")] private static partial Regex NameAndRest();
    [GeneratedRegex("""^name "([^"]+)"(?:.*?desc "([^"]*)")?""")] private static partial Regex DeviceLine();
}

public sealed record CpuFeature(string Name, bool Enabled)
{
    public override string ToString() => (Enabled ? "+" : "-") + Name;
}

public static class CpuFeatures
{
    public static IReadOnlyList<CpuFeature> Parse(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t[0] == '-' ? new CpuFeature(t[1..], false) : new CpuFeature(t.TrimStart('+'), true)).Where(f => f.Name.Length > 0).ToArray();
    public static string Format(IEnumerable<CpuFeature> features) => string.Join(',', features.Select(f => f.ToString()));
}
