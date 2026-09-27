using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Qemik.Desktop;

internal static class NativeDisplay
{
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    public static void GrantForeground(int processId) { if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(processId); }
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    private delegate bool EnumWindow(nint window, nint state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int maxCount);
    internal static nint FindWindow(Process process)
    {
        if (!OperatingSystem.IsWindows() || process.HasExited) return 0;
        process.Refresh(); if (process.MainWindowHandle is var main && main != 0) return main;
        // Process.MainWindowHandle excludes hidden windows after closing our shell.
        nint found = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var pid);
            if (pid != process.Id) return true;
            var name = new StringBuilder(128); GetClassName(window, name, name.Capacity);
            if (name.ToString() != "SDL_app") return true;
            found = window; return false;
        }, 0);
        return found;
    }
    public static bool Activate(Process process)
    {
        if (!OperatingSystem.IsWindows() || process.HasExited) return false;
        var handle = FindWindow(process);
        if (handle == 0) return false;
        ShowWindowAsync(handle, IsIconic(handle) ? 9 : 5);
        SetForegroundWindow(handle); return true;
    }
}
