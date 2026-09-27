using Qemik.Core;

namespace Qemik.Desktop;
public sealed partial class MainWindow
{
    private readonly Dictionary<string, GuestWindow> guestWindows = [];
    private async Task StartVm(VmConfig vm)
    {
        await manager.StartAsync(vm, prefs);
        if (vm.Display == "qemik") OpenGuest(vm);
    }
    private void OpenGuest(VmConfig vm)
    {
        if (guestWindows.TryGetValue(vm.Id, out var existing)) { existing.Show(); existing.Activate(); _ = existing.ShowSessionAsync(vm); return; }
        var window = new GuestWindow(vm, manager); guestWindows.Add(vm.Id, window);
        window.Closed += (_, _) => guestWindows.Remove(vm.Id);
        window.Show(); window.Activate();
    }
}
