using Avalonia;
using Avalonia.Controls;
using Qemik.Core;
using static Qemik.Desktop.Ui;

namespace Qemik.Desktop;

public sealed partial class MainWindow
{
    private async Task ChangeBlueprint(VmConfig vm)
    {
        await manager.SetBlueprintAsync(vm, !vm.IsBlueprint, store); ShowVm(vm.Id);
        Notify(vm.IsBlueprint ? "Blueprint saved. Create independent machines from it whenever you need." : "Blueprint converted back to a regular virtual machine.");
    }
    private async Task CreateFromBlueprint(VmConfig vm)
    {
        var dialog = new Window { Title = "Create from blueprint — " + vm.Name, Width = 600, Height = 540, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var name = Input(vm.Name + " copy", _ => { }); var copies = 1;
        var count = Number(1, 1, 100, value => copies = value);
        var message = Muted("Each machine gets a full, independent copy of the virtual disks. The blueprint remains unchanged.");
        using var cancellation = new CancellationTokenSource(); var copying = false; var created = 0;
        var close = Button("Close", dialog.Close); var cancel = Button("Cancel copying", () => cancellation.Cancel()); cancel.IsVisible = false;
        Button create = null!;
        create = AsyncButton("Create machines", async () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) throw new InvalidOperationException("Give the copies a name.");
            copying = true; name.IsEnabled = count.IsEnabled = close.IsEnabled = false; cancel.IsVisible = true;
            var requested = copies; var baseName = name.Text;
            try
            {
                for (var i = 0; i < requested; i++)
                {
                    var copyName = requested == 1 ? baseName : baseName + " " + (i + 1);
                    var clone = await manager.CloneBlueprintAsync(vm, prefs, copyName, new Progress<string>(s => message.Text = s), cancellation.Token);
                    try { store.Save(clone); }
                    catch (Exception ex) { throw new IOException("The copy is at " + AppPaths.VmDirectory(prefs, clone.Id) + " but could not be added to the library: " + ex.Message, ex); }
                    created++;
                }
                copying = false; dialog.Close(); ShowLibrary(); Notify($"Created {created} independent virtual machine(s) from {vm.Name}.");
            }
            catch (OperationCanceledException) { message.Text = $"Copying canceled. {created} completed machine(s) remain in the library."; }
            finally { copying = false; close.IsEnabled = true; cancel.IsVisible = false; create.IsVisible = false; }
        }, ex => message.Text = ex.Message, "primary");
        dialog.Content = new Border { Padding = new Thickness(24), Child = Stack(Heading("Create from blueprint", 24), Heading(vm.Name, 17), Field("Machine name", name), Field("Copies in this batch", count),
            message, Muted("ISOs stay shared read-only. VM IDs and MAC addresses are regenerated. Fixed port forwards, TCP serial endpoints and extra QEMU arguments are cleared. Guest OS users, hostname and installed software are copied as-is."), Row(close, cancel, create)) };
        dialog.Closing += (_, e) => { if (copying) { e.Cancel = true; message.Text = "Cancel copying first, then wait for the current disk operation to stop."; } };
        Chrome.Frame(dialog); await dialog.ShowDialog(this);
        if (created > 0) ShowLibrary();
    }
}
