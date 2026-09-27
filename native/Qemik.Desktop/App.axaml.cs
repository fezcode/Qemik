using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using Qemik.Core;
using Avalonia.Threading;

namespace Qemik.Desktop;
public sealed partial class App : Application
{
    private FileStream? instanceLock;
    private LibraryActivation? activation;
    public static FileStream? StartupLock { get; set; }
    public static string? DataDirectory { get; set; }
    public static LocalFolderSharing LocalFolders { get; private set; } = new(AppPaths.DefaultRoot);
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var root = DataDirectory ?? AppPaths.DefaultRoot;
            Directory.CreateDirectory(root);
            try { instanceLock = StartupLock ?? new FileStream(Path.Combine(root, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                desktop.MainWindow = new Window { Title = "Qemik is already open", Width = 510, Height = 260, Content = new Border { Padding = new Thickness(24), Child = Ui.Stack(Ui.Heading("This library is already open", 22), Ui.Muted("The other Qemik did not respond. It may be an older build. Close its window, then reopen this build. Your virtual machines are still in the same library."), Ui.Button("Close", () => desktop.Shutdown())) } };
                base.OnFrameworkInitializationCompleted(); return;
            }
            var store = new LibraryStore(DataDirectory);
            var manager = new VmManager();
            LocalFolders = new LocalFolderSharing(root);
            desktop.MainWindow = new MainWindow(store, manager);
            activation = new LibraryActivation(root, () => Dispatcher.UIThread.Post(() =>
            {
                var window = desktop.MainWindow;
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Show(); window.Activate();
                var dialog = window.OwnedWindows.LastOrDefault(w => w.IsVisible);
                dialog?.Activate();
            }));
            desktop.MainWindow.Opened += async (_, _) => { try { await LocalFolders.StartAsync(); } catch (Exception ex) { ((MainWindow)desktop.MainWindow).ShowSharingError(ex); } };
            desktop.Exit += (_, _) =>
            {
                activation.Dispose();
                // ASP.NET shutdown may await disposal internally. Run it without
                // Avalonia's synchronization context, which is stopping here.
                Task.Run(async () => await LocalFolders.DisposeAsync()).GetAwaiter().GetResult();
                manager.Dispose(); store.Dispose(); instanceLock.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
