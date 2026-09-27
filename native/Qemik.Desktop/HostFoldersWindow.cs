using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Qemik.Core;
using System.Diagnostics;
using static Qemik.Desktop.Ui;

namespace Qemik.Desktop;

public sealed class HostFoldersWindow : Window
{
    private readonly VmConfig vm;
    private readonly IHostFolderSharing sharing;
    private readonly StackPanel shares = Stack();
    private readonly TextBlock message = Muted("Choose a folder to make it available inside Ubuntu.");
    private bool busy;
    private readonly bool local;
    public HostFoldersWindow(VmConfig machine, IHostFolderSharing? sharing = null)
    {
        vm = machine; this.sharing = sharing ?? App.LocalFolders; local = this.sharing is LocalFolderSharing;
        Title = "Shared folders — " + vm.Name; Width = 850; Height = 780; MinWidth = 700; MinHeight = 580; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var path = new TextBox { IsReadOnly = true, PlaceholderText = "Choose a Windows folder…" };
        var name = new TextBox { Text = "Files", MaxLength = 40 };
        var readOnly = Check("Read-only — Ubuntu can read and copy, but cannot change host files", true, _ => { });
        var pick = AsyncButton("Choose folder…", async () =>
        {
            var selected = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a Windows folder to share", AllowMultiple = false });
            if (selected.FirstOrDefault()?.TryGetLocalPath() is { } folder) path.Text = folder;
        }, Error);
        var create = AsyncButton("Share folder…", async () =>
        {
            if (busy) return;
            if (string.IsNullOrWhiteSpace(path.Text)) throw new InvalidOperationException("Choose a host folder first.");
            await Change(async () => await this.sharing.CreateAsync(vm.Id, name.Text ?? "", path.Text, readOnly.IsChecked == true));
        }, Error, "primary");
        var setup = Card(Stack(Heading("Share a Windows folder", 19), Columns(path, pick, "*,Auto"), Field("Share name", name), readOnly,
            Muted(local ? "No Windows password or administrator prompt. Available while Qemik is open, through a private link on this computer. Keep the link private; anyone with it on this host or its guests can use the access you select." : "Access is granted to " + this.sharing.Account + ". Windows will ask for administrator approval. Existing file permissions still apply. These older SMB shares require your Windows password."), create));
        var content = Stack(setup, Heading("Folders shared for this machine", 19), shares);
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(24), RowSpacing = 16 };
        grid.Children.Add(Stack(Heading(local ? "Shared folders · No password" : "Windows SMB shares", 26), Muted(vm.Network == "user" && !vm.IsolateNetwork ? "Open these folders in Ubuntu Files using the shown address. The guest can keep running." : "These addresses require Shared (user) networking with isolation disabled. Change Network settings before connecting.")));
        var scroll = Scroll(content); Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var footerActions = Row(AsyncButton("Refresh", RefreshAsync, Error), Button("Close", Close));
        if (local) footerActions.Children.Add(AsyncButton("Previous Windows SMB shares…", () => new HostFoldersWindow(vm, new WindowsHostFolderSharing()).ShowDialog(this), Error));
        var footer = Stack(message, footerActions); Grid.SetRow(footer, 2); grid.Children.Add(footer);
        Content = grid; Chrome.Frame(this);
        Opened += async (_, _) => { try { await RefreshAsync(); } catch (Exception ex) { Error(ex); } };
        Closing += (_, e) => { if (busy) { e.Cancel = true; message.Text = "Finish or cancel the Windows administrator prompt first."; } };
    }
    private void Error(Exception ex) => message.Text = ex.Message;
    private async Task Change(Func<Task> operation)
    {
        if (busy) return;
        busy = true;
        try { await operation(); await RefreshAsync(); message.Text = "Shared folders updated. The guest has not been restarted."; }
        finally { busy = false; }
    }
    private async Task RefreshAsync()
    {
        var current = await sharing.ListAsync(vm.Id); shares.Children.Clear();
        if (current.Count == 0) shares.Children.Add(Muted("No folders shared for this machine yet."));
        foreach (var share in current)
        {
            var address = share.Address ?? WindowsHostFolderSharing.GuestAddress(share.Name);
            var actions = new WrapPanel();
            actions.Children.Add(AsyncButton("Copy Ubuntu address", async () => { if (Clipboard is not null) await Clipboard.SetTextAsync(address); message.Text = "In Ubuntu Files, press Ctrl+L and enter this SMB address."; }, Error));
            actions.Children.Add(Button("Show in Explorer", () => { var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; start.ArgumentList.Add(share.Path); Process.Start(start); }));
            if (!local) actions.Children.Add(AsyncButton("Use without a password", async () =>
            {
                var suffix = share.Name.StartsWith("Qemik_" + vm.Id[..8] + "_", StringComparison.Ordinal) ? share.Name[("Qemik_" + vm.Id[..8] + "_").Length..] : "Files";
                // New access defaults to read-only; users explicitly opt into writing in the local setup form.
                await App.LocalFolders.CreateAsync(vm.Id, suffix, share.Path, true);
                message.Text = "Password-free share created (read-only). Return to Shared folders to copy its address. Stop the old SMB share when it is no longer needed.";
            }, Error));
            actions.Children.Add(AsyncButton("Stop sharing…", async () =>
            {
                if (busy) return;
                var confirm = new Window { Title = "Stop sharing this folder?", Width = 490, Height = 250, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                confirm.Content = new Border { Padding = new Thickness(24), Child = Stack(Heading("Stop sharing?", 22), Muted("Close files opened from this share in Ubuntu first. Existing connections will disconnect. The Windows folder and its files stay in place."), Row(Button("Cancel", () => confirm.Close(false)), Button("Stop sharing", () => confirm.Close(true), "danger"))) };
                Chrome.Frame(confirm); if (await confirm.ShowDialog<bool>(this)) await Change(() => sharing.RemoveAsync(vm.Id, share));
            }, Error, "danger"));
            foreach (var action in actions.Children) action.Margin = new Thickness(0, 0, 8, 6);
            shares.Children.Add(Card(Stack(Heading(share.Name, 16), Text(share.Path), Muted(share.Access), Text(address, 15),
                Muted(local ? "Ubuntu Files → Ctrl+L → paste the address. No login required. Bookmark it for later; Qemik must stay open. To change access, stop sharing and add the folder again." : "Ubuntu Files → Ctrl+L → enter the address. Sign in with your Windows account and password (not your Windows Hello PIN)."), actions)));
        }
        message.Text = current.Count == 0 ? "Choose a folder to make it available inside Ubuntu." : $"Loaded {current.Count} shared folder(s). Open the shown address in Ubuntu Files.";
    }
}
