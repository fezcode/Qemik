using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Qemik.Core;
using System.Text.Json;
using static Qemik.Desktop.Ui;

namespace Qemik.Desktop;

public sealed partial class MainWindow
{
    private List<OsImage> imageCatalog = [];
    private string? imageTarget;
    private string imageMessage = "Check current Linux releases, then download an installer and attach it to a machine.";
    private string imageFilter = "All distributions";
    private string ImagesDirectory => Path.Combine(store.Root, "Images");
    public void ShowImages(string? targetId = null)
    {
        if (busy && location != "images") { Notify("Finish or pause the active download first."); return; }
        imageTarget = targetId;
        if (imageCatalog.Count == 0)
        {
            var cache = Path.Combine(store.Root, "image-catalog.json");
            if (File.Exists(cache))
            {
                try { imageCatalog = JsonSerializer.Deserialize<List<OsImage>>(File.ReadAllText(cache)) ?? []; foreach (var image in imageCatalog) OsImages.Validate(image); imageMessage = "Showing the saved catalog. Check for newer releases before downloading."; }
                catch (Exception ex) when (ex is JsonException or InvalidDataException) { imageCatalog = []; }
            }
        }
        var status = Muted(imageMessage);
        var check = AsyncButton("Check latest images", async () =>
        {
            EnsureIdle(); busy = true; status.Text = "Checking Ubuntu, Fedora, Linux Mint, openSUSE and Debian release metadata…";
            try
            {
                using var catalog = new OsImages(); imageCatalog = await catalog.RefreshAsync();
                await File.WriteAllTextAsync(Path.Combine(store.Root, "image-catalog.json"), JsonSerializer.Serialize(imageCatalog));
                imageMessage = $"{imageCatalog.Count} images checked " + DateTime.Now.ToString("g");
                if (catalog.RefreshWarnings.Count > 0) imageMessage += "\nSome publishers could not be checked: " + string.Join("; ", catalog.RefreshWarnings);
            }
            finally { busy = false; ShowImages(imageTarget); }
        }, Error, "primary");
        var filter = Select(imageFilter, ["All distributions", "Canonical", "Fedora Project", "Linux Mint", "openSUSE", "Debian"], value => { if (!busy) { imageFilter = value; ShowImages(imageTarget); } });
        filter.MinWidth = 200;
        var panel = Stack(PageHeader("OPERATING SYSTEMS", "Download an installer", "Official images, verified downloads, ready-to-install machines.", check), Row(filter, ExplorerButton(ImagesDirectory, "Open images folder", true)), status);
        if (targetId is not null)
        {
            var target = store.List().SingleOrDefault(v => v.Id == targetId);
            if (target is not null) panel.Children.Add(Card(Stack(Heading("Attach to " + target.Name, 18), Muted("The ISO will be added as a read-only CD/DVD and placed first in the boot order. Existing drives are preserved."), Button("Create a new machine instead", () => { if (!busy) ShowImages(); }))));
        }
        if (imageCatalog.Count == 0) panel.Children.Add(Card(Stack(Heading("Choose your next system", 22), Muted("Check latest images to load Ubuntu, Fedora Workstation and KDE, Linux Mint Cinnamon/MATE/Xfce, openSUSE Tumbleweed and Debian. Versions and download sizes come from their publishers."))));
        var visibleImages = imageCatalog.Where(image => imageFilter == "All distributions" || image.PublisherName == imageFilter).ToList();
        if (imageCatalog.Count > 0 && visibleImages.Count == 0) panel.Children.Add(Card(Muted("No images from this publisher in the saved catalog. Check latest images to refresh it.")));
        foreach (var image in visibleImages)
        {
            var path = OsImages.ImagePath(ImagesDirectory, image);
            var progress = new ProgressBar { Height = 5, Minimum = 0, Maximum = 100 };
            var progressText = Muted(File.Exists(path) ? "Downloaded — checksum will be rechecked before attaching." : "Not downloaded");
            var cancelSource = new CancellationTokenSource();
            var cancel = Button("Pause download", () => cancelSource.Cancel()); cancel.IsVisible = false;
            var start = AsyncButton(File.Exists(path) ? "Verify & install…" : "Download & install…", async () =>
            {
                EnsureIdle(); busy = true; cancel.IsVisible = true; check.IsEnabled = false; filter.IsEnabled = false;
                using var source = new OsImages(); using var cancellation = cancelSource;
                string? ready = null;
                try
                {
                    var reporter = new Progress<ImageProgress>(p => { progress.Value = 100.0 * p.Downloaded / p.Total; progressText.Text = $"{p.Stage} · {p.Downloaded / 1073741824.0:0.00} / {p.Total / 1073741824.0:0.00} GiB ({progress.Value:0}%)"; });
                    ready = await source.DownloadAsync(image, ImagesDirectory, reporter, cancellation.Token);
                    imageMessage = image.DisplayName + " downloaded and verified.";
                }
                catch (OperationCanceledException) { imageMessage = "Download paused. Click Download & install to resume it."; }
                finally { busy = false; cancel.IsVisible = false; check.IsEnabled = true; ShowImages(imageTarget); }
                if (ready is not null) await InstallDownloadedImage(image, ready, targetId);
            }, Error, "primary");
            panel.Children.Add(Card(Stack(Columns(Stack(Heading(image.DisplayName, 21), Muted($"Intel / AMD 64-bit · {image.Size / 1073741824.0:0.00} GiB · {image.Description}")), Text(image.PublisherName.ToUpperInvariant(), 11, "#D7F59A"), "*,Auto"),
                Muted(image.Url.AbsoluteUri), new Expander { Header = "Verification details", Content = Stack(Muted("SHA-256 from " + image.ChecksumUrl.AbsoluteUri), Code(image.Sha256), Muted("Verified against publisher checksum metadata over HTTPS. Detached GPG signatures are not checked by Qemik.")) },
                Row(start, cancel, ExplorerButton(path, "Show in Explorer"), Button("Publisher page ↗", () => OpenUrl(image.Website))), progress, progressText)));
        }
        panel.Children.Add(Card(Stack(Heading("Other operating systems", 18), Muted("Automatic downloads support Intel/AMD 64-bit Linux guests. For Windows or another system, download from its publisher and attach the ISO in Drives. Microsoft download links can expire and depend on language selection."), Row(Button("Windows 11 downloads ↗", () => OpenUrl("https://www.microsoft.com/software-download/windows11")), Button("Use a local ISO", () => _ = Run(NewMachine))))));
        SetPage("images", Scroll(panel));
    }
    private Button ExplorerButton(string path, string label, bool directory = false)
    {
        var button = AsyncButton(label, () =>
        {
            var full = Path.GetFullPath(path);
            if (directory) Directory.CreateDirectory(full);
            else if (!File.Exists(full)) throw new FileNotFoundException("This image is no longer at its saved location.", full);
            var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            if (!directory) start.ArgumentList.Add("/select,");
            start.ArgumentList.Add(full); System.Diagnostics.Process.Start(start);
            return Task.CompletedTask;
        }, Error);
        button.IsEnabled = directory || File.Exists(path);
        ToolTip.SetTip(button, path);
        return button;
    }
    private async Task InstallDownloadedImage(OsImage image, string path, string? targetId)
    {
        if (targetId is not null)
        {
            var vm = store.List().SingleOrDefault(v => v.Id == targetId) ?? throw new InvalidOperationException("The target machine was removed.");
            if (manager.IsRunning(vm.Id)) throw new InvalidOperationException("Stop the target machine before attaching installation media.");
            InstallMedia.Attach(vm, image, path); store.Save(vm); ShowVm(vm.Id); Notify("Verified installer attached. Start the machine to install " + image.DisplayName + "."); return;
        }
        var name = image.DisplayName; var capacity = 64;
        var dialog = new Window { Title = "Install " + image.DisplayName, Width = 610, Height = 430, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var error = Muted("The ISO is verified. A new sparse disk will be created for the guest.");
        var creating = false;
        dialog.Closing += (_, e) => { if (creating) e.Cancel = true; };
        dialog.Content = new Border { Padding = new Thickness(28), Child = Stack(Heading("Ready to install", 25), Field("Machine name", Input(name, s => name = s)), Field("System disk capacity (GiB)", Number(capacity, 25, 65536, n => capacity = n)), error,
            Row(Button("Later", () => dialog.Close()), AsyncButton("Create machine & mount ISO", async () =>
            {
                creating = true;
                try
                {
                    var detected = await QemuInstallation.DetectAsync(prefs.QemuDirectory) ?? throw new InvalidOperationException("Install or locate QEMU in QEMU Engine first. Your ISO is safely cached.");
                    prefs.QemuDirectory = detected.Directory; store.SavePreferences(prefs);
                    var vm = await InstallMedia.CreateAsync(prefs, image, path, name, capacity); store.Save(vm);
                    creating = false; dialog.Close(); ShowVm(vm.Id); Notify("Machine created with installer mounted. Click Start machine to begin installation.");
                }
                finally { creating = false; }
            }, ex => error.Text = ex.Message, "primary"))) };
        Chrome.Frame(dialog); await dialog.ShowDialog(this);
    }
    private async Task RemoveVm(VmConfig vm)
    {
        if (manager.IsRunning(vm.Id)) throw new InvalidOperationException("Stop this machine before removing it.");
        var backingFiles = new HashSet<string>(); string? backingError = null;
        try { backingFiles = await VmRemoval.FindBackingFilesAsync(vm, prefs, store.List()); }
        catch (Exception ex) { backingError = "File deletion is unavailable because another disk's backing chain could not be checked: " + ex.Message; }
        var plan = VmRemoval.Review(vm, prefs, store.List(), backingFiles); var deleteFiles = false;
        var window = new Window { Title = "Remove " + vm.Name, Width = 680, Height = 610, MinWidth = 620, MinHeight = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var details = Stack();
        foreach (var file in plan.Files) details.Children.Add(Muted($"{file.Path}\n{file.Size / 1048576.0:0.0} MiB"));
        foreach (var file in plan.Kept) details.Children.Add(Muted("KEPT: " + file));
        if (plan.Files.Count == 0) details.Children.Add(Muted("No removable image or runtime files were found."));
        var error = Muted("Removing only the library entry keeps all files. File deletion is permanent.");
        var remove = Button("Remove from library", () => window.Close(true), "danger");
        var checkbox = Check("Also permanently delete attached disks, installer ISOs and machine files", false, value => { deleteFiles = value; remove.Content = value ? "Delete files & remove machine" : "Remove from library"; });
        if (backingError is not null) { checkbox.IsEnabled = false; error.Text = backingError + " You can still remove only the library entry."; }
        checkbox.Content = new TextBlock { Text = "Also permanently delete attached disks, installer ISOs and machine files", TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 570 };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 18, Margin = new Thickness(28) };
        layout.Children.Add(Stack(Heading("Remove " + vm.Name + "?", 23), error, checkbox, Muted($"{plan.Files.Count} files · {plan.Files.Sum(f => f.Size) / 1073741824.0:0.00} GiB. Files referenced by another library machine, firmware and linked paths are protected.")));
        var list = Scroll(details); Grid.SetRow(list, 1); layout.Children.Add(list);
        var actions = Row(Button("Cancel", () => window.Close(false)), remove); actions.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetRow(actions, 2); layout.Children.Add(actions);
        window.Content = layout; Chrome.Frame(window);
        if (!await window.ShowDialog<bool>(this)) return;
        if (manager.IsRunning(vm.Id)) throw new InvalidOperationException("This machine started while removal was being reviewed. Stop it first.");
        if (deleteFiles) VmRemoval.DeleteReviewedFiles(plan, vm, prefs, store.List(), await VmRemoval.FindBackingFilesAsync(vm, prefs, store.List()));
        store.Remove(vm.Id); ShowLibrary(); Notify(deleteFiles ? "Machine removed and reviewed files deleted. Protected shared files were kept." : "Machine removed from the library. All files were kept.");
    }
}
