using System.Runtime.InteropServices;

namespace Qemik.Core;
public static class HostAcceleration
{
    [DllImport("WinHvPlatform.dll", ExactSpelling = true)]
    private static extern int WHvGetCapability(int capabilityCode, out int capability, uint capabilityBufferSizeInBytes, out uint writtenSizeInBytes);
    public static bool HasWhpx()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return WHvGetCapability(0, out var present, 4, out _) == 0 && present != 0; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }
    public static async Task<string> BestForX86Async(Preferences prefs)
    {
        if (!HasWhpx()) return "tcg";
        var help = await ProcessRunner.RunAsync(Path.Combine(prefs.QemuDirectory, "qemu-system-x86_64.exe"), ["-accel", "help"]);
        return help.Split('\n').Any(line => line.Trim() == "whpx") ? "whpx" : "tcg";
    }
}
