using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using static Qemik.Desktop.Ui;

namespace Qemik.Desktop;

public sealed partial class GuestWindow
{
    private async Task ShowResizeSetupAsync()
    {
        var dialog = new Window { Title = "Auto-fit setup — " + vm.Name, Width = 730, Height = 610, MinWidth = 600, MinHeight = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var command = await App.LocalFolders.DisplaySetupCommandAsync();
        var feedback = Muted("The helper runs as your Ubuntu user and follows QEMU's requested resolution. It needs Ubuntu GNOME with VirtIO graphics.");
        var field = new TextBox { Text = command, IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 110 };
        dialog.Content = new Border { Padding = new Thickness(24), Child = Scroll(Stack(Heading("Let Ubuntu follow the window", 24),
            Muted("Ubuntu can keep its saved desktop resolution even after QEMU requests a different size. Install the small Qemik helper once inside Ubuntu to apply changes automatically, up to 4K."),
            Text("1. Keep Qemik open. Use Shared (user) networking without isolation."),
            Text("2. Open Terminal inside Ubuntu (Ctrl+Alt+T), paste this command, and press Enter."), field,
            AsyncButton("Copy setup command", async () => { if (Clipboard is not null) await Clipboard.SetTextAsync(command); feedback.Text = "Copied. Paste into the Ubuntu terminal. If clipboard sharing is not ready, use Set up clipboard first."; }, ex => feedback.Text = ex.Message, "primary"),
            Text("3. Keep Fit resolution enabled and resize this window."), feedback,
            Muted("No sudo or Windows password. Installs ~/.local/share/qemik/display-fit.py and a per-user autostart entry. To remove: delete ~/.config/autostart/qemik-display-fit.desktop and log out."),
            Row(Button("Retry fitting", () => { ScheduleResize(); dialog.Close(); }), Button("Close", dialog.Close)))) };
        Chrome.Frame(dialog); await dialog.ShowDialog(this);
    }
}
