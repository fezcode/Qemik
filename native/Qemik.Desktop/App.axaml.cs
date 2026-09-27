using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using Qemik.Core;

namespace Qemik.Desktop;
public sealed partial class App : Application
{
    private FileStream? instanceLock;
    public static string? DataDirectory { get; set; }
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var root = DataDirectory ?? AppPaths.DefaultRoot;
            Directory.CreateDirectory(root);
            try { instanceLock = new FileStream(Path.Combine(root, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                desktop.MainWindow = new Window { Title = "Qemik is already open", Width = 460, Height = 180, Content = new Border { Padding = new Thickness(24), Child = Ui.Stack(Ui.Heading("This library is already open", 22), Ui.Muted("Use the existing Qemik window, or choose another --data-dir.")) } };
                base.OnFrameworkInitializationCompleted(); return;
            }
            var store = new LibraryStore(DataDirectory);
            var manager = new VmManager();
            desktop.MainWindow = new MainWindow(store, manager);
            desktop.Exit += (_, _) => { manager.Dispose(); store.Dispose(); instanceLock.Dispose(); };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
