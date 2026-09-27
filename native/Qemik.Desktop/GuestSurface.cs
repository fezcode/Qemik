using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Qemik.Core;
using System.Runtime.InteropServices;

namespace Qemik.Desktop;

public sealed class GuestSurface : Control, IDisposable
{
    private WriteableBitmap? bitmap;
    private GuestDisplayClient? client;
    private readonly HashSet<uint> keys = [];
    private long lastMove;
    private int pointerX, pointerY;
    public event Action? ReleaseRequested;
    public event Action<Exception>? InputError;
    public GuestSurface() { Focusable = true; ClipToBounds = true; }
    public void Connect(GuestDisplayClient display) => client = display;
    public void Present(GuestFrame frame)
    {
        var dirty = frame.Dirty ?? new GuestRegion(0, 0, frame.Width, frame.Height);
        if (bitmap?.PixelSize != new PixelSize(frame.Width, frame.Height))
        {
            bitmap?.Dispose(); bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            dirty = new GuestRegion(0, 0, frame.Width, frame.Height);
        }
        if (dirty.Width == 0 || dirty.Height == 0) return;
        using (var buffer = bitmap.Lock())
            for (var y = dirty.Y; y < dirty.Y + dirty.Height; y++)
            {
                var offset = (y * frame.Width + dirty.X) * 4;
                // RFB's fourth byte is padding, not alpha. Touch only changed pixels.
                for (var i = offset + 3; i < offset + dirty.Width * 4; i += 4) frame.Pixels[i] = 255;
                Marshal.Copy(frame.Pixels, offset, buffer.Address + y * buffer.RowBytes + dirty.X * 4, dirty.Width * 4);
            }
        InvalidateVisual();
    }
    public static Rect Fit(Size bounds, int width, int height)
    {
        var scale = Math.Min(bounds.Width / width, bounds.Height / height);
        return new Rect((bounds.Width - width * scale) / 2, (bounds.Height - height * scale) / 2, width * scale, height * scale);
    }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        if (bitmap is not null) context.DrawImage(bitmap, new Rect(bitmap.Size), Fit(Bounds.Size, bitmap.PixelSize.Width, bitmap.PixelSize.Height));
    }
    private async void Input(Func<GuestDisplayClient, Task> action)
    {
        if (client is not { } display) return;
        try { await action(display); } catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { InputError?.Invoke(ex); }
    }
    private bool MapPointer(Point point)
    {
        if (client is null) return false;
        var rect = Fit(Bounds.Size, client.Width, client.Height);
        if (rect.Width <= 0 || rect.Height <= 0) return false;
        pointerX = Math.Clamp((int)((point.X - rect.X) * client.Width / rect.Width), 0, client.Width - 1);
        pointerY = Math.Clamp((int)((point.Y - rect.Y) * client.Height / rect.Height), 0, client.Height - 1); return true;
    }
    private static byte Buttons(PointerPointProperties p) => (byte)((p.IsLeftButtonPressed ? 1 : 0) | (p.IsMiddleButtonPressed ? 2 : 0) | (p.IsRightButtonPressed ? 4 : 0));
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); Focus(); e.Pointer.Capture(this);
        if (MapPointer(e.GetPosition(this))) Input(c => c.PointerAsync(pointerX, pointerY, Buttons(e.GetCurrentPoint(this).Properties))); e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (MapPointer(e.GetPosition(this))) Input(c => c.PointerAsync(pointerX, pointerY, Buttons(e.GetCurrentPoint(this).Properties)));
        e.Pointer.Capture(null); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Environment.TickCount64 - lastMove < 16) return; lastMove = Environment.TickCount64;
        if (MapPointer(e.GetPosition(this))) Input(c => c.PointerAsync(pointerX, pointerY, Buttons(e.GetCurrentPoint(this).Properties)));
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (MapPointer(e.GetPosition(this)))
        {
            var buttons = Buttons(e.GetCurrentPoint(this).Properties); var wheel = e.Delta.Y > 0 ? 8 : 16;
            Input(async c => { await c.PointerAsync(pointerX, pointerY, (byte)(buttons | wheel)); await c.PointerAsync(pointerX, pointerY, buttons); });
        }
        e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.G && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        { ReleaseInput(); ReleaseRequested?.Invoke(); e.Handled = true; return; }
        var symbol = KeySymbol(e.PhysicalKey, e.Key);
        if (symbol != 0) { keys.Add(symbol); Input(c => c.KeyAsync(symbol, true)); }
        e.Handled = true;
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        var symbol = KeySymbol(e.PhysicalKey, e.Key); if (keys.Remove(symbol)) Input(c => c.KeyAsync(symbol, false)); e.Handled = true;
    }
    protected override void OnLostFocus(FocusChangedEventArgs e) { base.OnLostFocus(e); ReleaseInput(); }
    public void ReleaseInput()
    {
        var pressed = keys.ToArray(); keys.Clear();
        Input(async c => { foreach (var key in pressed) await c.KeyAsync(key, false); await c.PointerAsync(pointerX, pointerY, 0); });
    }
    public static uint KeySymbol(PhysicalKey physical, Key fallback = Key.None)
    {
        var name = physical == PhysicalKey.None ? fallback.ToString() : physical.ToString();
        if (name.Length == 1 && name[0] is >= 'A' and <= 'Z') return char.ToLowerInvariant(name[0]);
        if (name.StartsWith("Digit", StringComparison.Ordinal) && name.Length == 6) return name[5];
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) return name[1];
        if (name.StartsWith('F') && int.TryParse(name[1..], out var function) && function is >= 1 and <= 24) return (uint)(0xffbe + function - 1);
        if (name.StartsWith("NumPad", StringComparison.Ordinal) && name.Length == 7 && char.IsDigit(name[6])) return (uint)(0xffb0 + name[6] - '0');
        return name switch
        {
            "Escape" => 0xff1b, "Enter" or "Return" => 0xff0d, "Tab" => 0xff09, "Backspace" or "Back" => 0xff08,
            "Delete" => 0xffff, "Insert" => 0xff63, "Home" => 0xff50, "End" => 0xff57, "PageUp" => 0xff55, "PageDown" => 0xff56,
            "ArrowLeft" or "Left" => 0xff51, "ArrowUp" or "Up" => 0xff52, "ArrowRight" or "Right" => 0xff53, "ArrowDown" or "Down" => 0xff54,
            "ControlLeft" or "LeftCtrl" => 0xffe3, "ControlRight" or "RightCtrl" => 0xffe4, "ShiftLeft" or "LeftShift" => 0xffe1, "ShiftRight" or "RightShift" => 0xffe2,
            "AltLeft" or "LeftAlt" => 0xffe9, "AltRight" or "RightAlt" => 0xffea, "MetaLeft" or "LWin" => 0xffeb, "MetaRight" or "RWin" => 0xffec,
            "CapsLock" => 0xffe5, "NumLock" => 0xff7f, "ScrollLock" => 0xff14, "PrintScreen" => 0xff61, "Pause" => 0xff13, "ContextMenu" => 0xff67,
            "Space" => 32, "Backquote" => 96, "Minus" => 45, "Equal" => 61, "BracketLeft" => 91, "BracketRight" => 93,
            "Backslash" or "IntlBackslash" => 92, "Semicolon" => 59, "Quote" => 39, "Comma" => 44, "Period" => 46, "Slash" => 47,
            "NumPadEnter" => 0xff8d, "NumPadAdd" => 0xffab, "NumPadSubtract" => 0xffad, "NumPadMultiply" => 0xffaa, "NumPadDivide" => 0xffaf, "NumPadDecimal" => 0xffae,
            _ => 0
        };
    }
    public void Dispose() { client = null; bitmap?.Dispose(); bitmap = null; }
}
