using Avalonia;

namespace Qemik.Desktop;
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--version") || args.Contains("-v")) { Console.WriteLine("Qemik 0.1.0"); return; }
        if (args.Contains("--help") || args.Contains("-h")) { Console.WriteLine("Qemik — QEMU desktop for Windows.\n--data-dir <folder>  Use an isolated library\n--version           Show version\n--help              Show help"); return; }
        var index = Array.IndexOf(args, "--data-dir");
        if (index >= 0 && index + 1 < args.Length) App.DataDirectory = Path.GetFullPath(args[index + 1]);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
