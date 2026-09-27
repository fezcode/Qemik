using System.Runtime.InteropServices;
using System.Text;

namespace Qemik.Core;

internal static class QemuWindowsPath
{
    // Some Windows QEMU builds open QMP filenames through the ANSI CRT. Use an
    // existing ASCII short alias when possible; never rename/copy a user's image.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetShortPathNameW")]
    private static extern uint GetShortPath(string path, StringBuilder result, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetLongPathNameW")]
    private static extern uint GetLongPath(string path, StringBuilder result, uint length);
    public static string ForMonitor(string path)
    {
        path = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows() || path.All(c => c < 128)) return path;
        var result = new StringBuilder(32768); var length = GetShortPath(path, result, (uint)result.Capacity);
        return length > 0 && length < result.Capacity && result.ToString().All(c => c < 128) ? result.ToString() : path;
    }
    public static string ForDisplay(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(path)) return path;
        var result = new StringBuilder(32768); var length = GetLongPath(path, result, (uint)result.Capacity);
        return length > 0 && length < result.Capacity ? result.ToString() : path;
    }
}
