using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Qemik.Core;
using System.Threading.Channels;

namespace Qemik.Desktop;

public sealed partial class NativeGpuSurface
{
    private TopLevel? keyboardWindow;
    private bool capturing;
    private readonly HashSet<int> pressedKeys = [];
    private Channel<(int Code, bool Down)>? keys;
    public event Action<string>? KeyboardError;

    private void AttachKeyboard(QmpClient? qmp)
    {
        if (qmp is null || TopLevel.GetTopLevel(this) is not { } top) return;
        keyboardWindow = top;
        keys = Channel.CreateUnbounded<(int, bool)>(new() { SingleReader = true, SingleWriter = true });
        var pending = keys;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var key in pending.Reader.ReadAllAsync())
                    await qmp.ExecuteAsync("input-send-event", new { events = new[] {
                        new { type = "key", data = new { down = key.Down, key = new { type = "number", data = key.Code } } }
                    } });
            }
            catch (Exception ex)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => KeyboardError?.Invoke(ex.Message));
            }
        });
        Win32Properties.AddWndProcHookCallback(top, KeyboardMessage);
        top.AddHandler(InputElement.PointerPressedEvent, ToolbarPointerPressed, RoutingStrategies.Tunnel);
    }
    private void ToolbarPointerPressed(object? sender, PointerPressedEventArgs e) => ReleaseKeyboard();
    private nint KeyboardMessage(nint hwnd, uint message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0008 || (message == 0x0006 && ((long)wParam & 0xffff) == 0)) ReleaseKeyboard(); // focus/activation lost
        if (!capturing || !IsForeground || keys is null) return 0;
        if (message is 0x0100 or 0x0101 or 0x0104 or 0x0105)
        {
            var bits = (long)lParam;
            var code = (int)((bits >> 16) & 0x7f) | ((bits & 0x01000000) != 0 ? 0x80 : 0);
            if ((int)wParam == 0x13) code = 0xc6; // Pause uses E1 rather than E0.
            if ((int)wParam == 0x2c) code = 0xb7; // Print Screen.
            if (code == 0) return 0;
            var down = message is 0x0100 or 0x0104;
            // The guest handles repeat while a key is held.
            if (down ? pressedKeys.Add(code) : pressedKeys.Remove(code)) keys.Writer.TryWrite((code, down));
            handled = true;
        }
        else if (message is 0x0102 or 0x0106) handled = true; // Don't also type into the host UI.
        return 0;
    }
    private void ReleaseKeyboard()
    {
        capturing = false;
        foreach (var code in pressedKeys) keys?.Writer.TryWrite((code, false));
        pressedKeys.Clear();
    }
    private void DetachKeyboard()
    {
        ReleaseKeyboard();
        if (keyboardWindow is { } top)
        {
            Win32Properties.RemoveWndProcHookCallback(top, KeyboardMessage);
            top.RemoveHandler(InputElement.PointerPressedEvent, ToolbarPointerPressed);
        }
        keyboardWindow = null; keys?.Writer.TryComplete(); keys = null;
    }
}
