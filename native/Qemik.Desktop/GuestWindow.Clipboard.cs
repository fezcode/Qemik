using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Qemik.Core;
using static Qemik.Desktop.Ui;

namespace Qemik.Desktop;

public sealed partial class GuestWindow
{
    private void RefreshClipboardControls()
    {
        shareClipboard.IsVisible = ClipboardChannelAvailable;
        clipboardSetup.IsVisible = !ClipboardChannelAvailable || UsesNativeGpu;
    }
    private async Task ConnectAgentClipboardAsync()
    {
        agentClipboard?.Dispose(); agentClipboard = null; lastClipboard = null; agentGeneration = 0;
        if (manager.Session(vm.Id)?.ClipboardPort is not { } port) return;
        var agent = await GuestAgentClipboard.ConnectAsync(port, closing.Token); agentClipboard = agent;
        agent.Enabled = shareClipboard.IsChecked == true && ClipboardWindowActive;
        agent.Received = async text => await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (closing.IsCancellationRequested || agentClipboard != agent || !agent.Enabled || !ClipboardWindowActive || Clipboard is null) return;
            lastClipboard = text; await Clipboard.SetTextAsync(text);
        });
        _ = Task.Run(async () =>
        {
            try { await agent.ReadAsync(closing.Token); }
            catch (Exception ex) when (!closing.IsCancellationRequested)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { if (agentClipboard == agent) integration.Text = "Clipboard disconnected: " + ex.Message + " Use Reconnect."; });
            }
            catch (Exception) when (closing.IsCancellationRequested) { }
        });
    }
    private async Task SyncAgentClipboardAsync()
    {
        if (agentClipboard is not { } agent) return;
        if (agent.Disconnected) return;
        agent.Enabled = shareClipboard.IsChecked == true && ClipboardWindowActive;
        if (!agent.Enabled || syncingClipboard || Clipboard is null) return;
        if (!agent.Ready) { integration.Text = "GPU auto-resize active · Waiting for clipboard guest agent; see Set up clipboard"; return; }
        syncingClipboard = true;
        try
        {
            if (agent.Generation != agentGeneration) { lastClipboard = null; agentGeneration = agent.Generation; }
            var before = lastClipboard; var text = await Clipboard.TryGetTextAsync();
            if (before != lastClipboard || agentClipboard != agent || !agent.Enabled || !ClipboardWindowActive) return;
            if (text is not null && text != lastClipboard && await agent.OfferTextAsync(text, closing.Token)) lastClipboard = text;
            integration.Text = "GPU auto-resize active · Text clipboard connected";
        }
        catch (Exception ex) when (!closing.IsCancellationRequested) { integration.Text = ex.Message; }
        catch (Exception) when (closing.IsCancellationRequested) { }
        finally { syncingClipboard = false; }
    }
    private async Task ShowClipboardSetupAsync()
    {
        var dialog = new Window { Title = "Set up clipboard — " + vm.Name, Width = 610, Height = 460, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var message = Muted(UsesNativeGpu && manager.Session(vm.Id)?.ClipboardPort is not null
            ? "The GPU clipboard channel is present. Install the guest agent below if the toolbar is still waiting. Text is synchronized in both directions while this guest window is active."
            : vm.SharedClipboard ? "Clipboard is enabled in the saved settings. Fully shut down Ubuntu and start it again to activate the channel." : "This machine started without its clipboard channel. Enable it for the next start, then fully shut down Ubuntu and start it again. Reconnect and an in-guest reboot cannot add the channel.");
        var enable = AsyncButton("Enable for next start", async () =>
        {
            if (enableClipboardForNextStart is null) return;
            await enableClipboardForNextStart(); vm.SharedClipboard = true;
            message.Text = "Saved. Fully shut down Ubuntu and start it again. Your current session has not been interrupted.";
            dialog.Close();
            status.Text = "Clipboard enabled for the next start. Fully shut down the guest, then start it again.";
        }, ex => message.Text = ex.Message, "primary");
        enable.IsEnabled = enableClipboardForNextStart is not null && !vm.SharedClipboard;
        dialog.Content = new Border { Padding = new Thickness(24), Child = Stack(Heading("Set up text clipboard", 24), message,
            Heading("Inside Ubuntu", 16), Muted("Install the guest service, then log out and back in:"),
            Text("sudo apt update\nsudo apt install spice-vdagent", 15),
            Muted("Once the channel is active, Share text clipboard appears in the toolbar. Guest desktop/Wayland support varies. Images and files are not supported."),
            enableClipboardForNextStart is null ? Muted("In the main window: Options → Edit settings → Sharing → Share text clipboard with the guest.") : new TextBlock(),
            Row(Button("Close", dialog.Close), enable)) };
        Chrome.Frame(dialog); await dialog.ShowDialog(this);
    }
}
