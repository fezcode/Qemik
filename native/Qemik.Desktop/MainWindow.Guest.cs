using Qemik.Core;
using Avalonia.Controls;
using static Qemik.Desktop.Ui;

namespace Qemik.Desktop;
public sealed partial class MainWindow
{
    private readonly Dictionary<string, GuestWindow> guestWindows = [];
    private Button ForceShutdownButton(string id)
    {
        var button = WithIcon(AsyncButton("Force shutdown", async () =>
        {
            await manager.ForceStopAsync(id);
            if (guestWindows.TryGetValue(id, out var window)) window.Close();
        }, Error, "danger"), "Force shutdown", Icons.Stop);
        ToolTip.SetTip(button, "Immediately stops QEMU and closes its guest window. Unsaved guest work is lost.");
        return button;
    }
    private async Task StartVm(VmConfig vm)
    {
        await manager.StartAsync(vm, prefs);
        if (vm.Display == "qemik" || (OperatingSystem.IsWindows() && vm.Display == "sdl")) OpenGuest(vm);
    }
    private void OpenGuest(VmConfig vm)
    {
        if (manager.Session(vm.Id) is { Active: true, DisplayBackend: "gtk" } native)
        {
            if (!NativeDisplay.Activate(native.Process)) Notify("The native display is still opening. Try Open again in a moment.");
            return;
        }
        if (guestWindows.TryGetValue(vm.Id, out var existing))
        {
            if (existing.UsesNativeGpu == (OperatingSystem.IsWindows() && manager.Session(vm.Id)?.DisplayBackend == "sdl"))
            { existing.Show(); existing.Activate(); _ = existing.ShowSessionAsync(vm); return; }
            existing.Close();
        }
        var window = new GuestWindow(vm, manager, () =>
        {
            var saved = store.List().Single(v => v.Id == vm.Id); saved.SharedClipboard = true; store.Save(saved);
            return Task.CompletedTask;
        }, owner => EditVm(vm, owner)); guestWindows.Add(vm.Id, window);
        window.Closed += (_, _) => guestWindows.Remove(vm.Id);
        window.Show(); window.Activate();
    }
}
