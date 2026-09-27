using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Diagnostics;
using static Qemik.Desktop.Ui;

namespace Qemik.Desktop;

public sealed partial class GuestWindow
{
    private async Task ShowMediaAsync()
    {
        var dialog = new Window { Title = "Mounted disks — " + vm.Name, Width = 760, Height = 650, MinWidth = 600, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = Stack(); var message = Muted("");
        void MediaError(Exception ex) => message.Text = ex.Message;
        async Task Refresh()
        {
            var drives = await manager.MountedDrivesAsync(vm.Id); list.Children.Clear();
            foreach (var drive in drives)
            {
                var controls = new WrapPanel();
                if (drive.Removable)
                {
                    controls.Children.Add(WithIcon(AsyncButton("Mount ISO…", async () =>
                    {
                        var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Mount an installation ISO", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("ISO images") { Patterns = ["*.iso"] }] });
                        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
                        if (drive.Path.Length > 0 && !await Confirm("Replace this disc?", "Make sure the guest has finished using the mounted ISO. This changes the current session only.", "Replace", dialog)) return;
                        await manager.ChangeMediumAsync(vm.Id, drive.Device, path); message.Text = "ISO mounted read-only for this session."; await Refresh();
                    }, MediaError), "Mount ISO…", Icons.Disk));
                    var eject = WithIcon(AsyncButton("Eject", async () =>
                    {
                        if (!await Confirm("Eject this disc?", "Wait until the installer asks you to remove its media. The ISO file stays on disk.", "Eject", dialog)) return;
                        await manager.ChangeMediumAsync(vm.Id, drive.Device, null); message.Text = "Disc ejected. Its file is unchanged."; await Refresh();
                    }, MediaError), "Eject", Icons.Eject);
                    eject.IsEnabled = drive.Path.Length > 0; controls.Children.Add(eject);
                }
                var explorer = Button("Show in Explorer", () =>
                {
                    var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; info.ArgumentList.Add("/select,"); info.ArgumentList.Add(drive.Path); Process.Start(info);
                });
                explorer.IsEnabled = File.Exists(drive.Path); controls.Children.Add(explorer);
                foreach (var action in controls.Children) action.Margin = new Thickness(0, 0, 8, 6);
                list.Children.Add(Card(Stack(Heading((drive.Removable ? "CD / DVD · " : "Virtual disk · ") + drive.Device, 17),
                    Text(drive.Path.Length == 0 ? "No media inserted" : drive.Path),
                    Muted((drive.ReadOnly ? "Read-only" : "Writable") + (drive.Locked ? " · Locked by guest" : "") + (drive.TrayOpen ? " · Tray open" : "")), controls)));
            }
            if (drives.Count == 0) list.Children.Add(Muted("No disk devices are attached. Shut down and add a drive in machine Settings → Drives."));
        }
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(22), RowSpacing = 14 };
        grid.Children.Add(Stack(Heading("Mounted disks", 24), Muted("ISO changes apply to this running session. Configure startup media and add or detach hard disks in Settings → Drives after shutdown.")));
        var scroll = Scroll(list); Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var bottom = Stack(message, Row(AsyncButton("Refresh", Refresh, MediaError), Button("Close", dialog.Close))); Grid.SetRow(bottom, 2); grid.Children.Add(bottom);
        dialog.Content = grid; Chrome.Frame(dialog); await Refresh(); await dialog.ShowDialog(this);
    }
}
