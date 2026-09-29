using Avalonia;
using Qemik.Core;

namespace Qemik.Desktop;
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--version") || args.Contains("-v")) { Console.WriteLine(AppVersion.Display); return; }
        if (args.Contains("--help") || args.Contains("-h")) { Console.WriteLine("Qemik — QEMU desktop for Windows.\n--data-dir <folder>  Use an isolated library\n--version           Show version\n--help              Show help"); return; }
        var index = Array.IndexOf(args, "--data-dir");
        if (index >= 0 && index + 1 < args.Length) App.DataDirectory = Path.GetFullPath(args[index + 1]);
        var root = App.DataDirectory ?? AppPaths.DefaultRoot; Directory.CreateDirectory(root);
        try { App.StartupLock = new FileStream(Path.Combine(root, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            // Activate before starting Avalonia: its lifetime cannot shut down
            // safely while framework initialization is still in progress.
            if (LibraryActivation.NotifyAsync(root, NativeDisplay.GrantForeground).GetAwaiter().GetResult()) return;
        }
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { App.StartupLock?.Dispose(); App.StartupLock = null; }
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
