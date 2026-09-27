using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Qemik.Core;

namespace Qemik.Desktop;

// QEMU retains ownership of its SDL/OpenGL window and graphics context. Avalonia
// hosts a container HWND; no GPU pixels pass through the CPU framebuffer viewer.
public sealed partial class NativeGpuSurface : NativeControlHost
{
    private nint container, child, previousParent;
    private int originalStyle;
    private readonly DispatcherTimer sizing = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer focus = new() { Interval = TimeSpan.FromMilliseconds(25) };
    public bool IsAttached => child != 0 && IsWindow(child);
    public bool IsForeground => IsAttached && GetAncestor(child, 2) == GetForegroundWindow();
    public NativeGpuSurface()
    {
        sizing.Tick += (_, _) => Fit();
        focus.Tick += (_, _) =>
        {
            if (!IsForeground) { ReleaseKeyboard(); return; }
            if (!IsForeground || (GetAsyncKeyState(1) & 0x8000) == 0 || !GetCursorPos(out var point)) return;
            var hit = WindowFromPoint(point);
            if (hit == child || IsChild(child, hit)) FocusGuest();
        };
    }
    public void FocusGuest()
    {
        if (!IsForeground) return;
        // SDL's foreground-window check rejects an embedded child. Keep key
        // focus on our top-level window and deliver physical keys through QMP.
        capturing = true; SetFocus(GetAncestor(child, 2));
    }
    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent); container = handle.Handle; return handle;
    }
    public async Task AttachAsync(Process process, CancellationToken ct, QmpClient? input = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native GPU hosting requires Windows.");
        if (IsAttached) { DetachKeyboard(); AttachKeyboard(input); Fit(); FocusGuest(); return; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        nint window;
        while ((window = NativeDisplay.FindWindow(process)) == 0 || container == 0)
        {
            if (process.HasExited) throw new IOException("The GPU display process stopped. Check the machine log.");
            await Task.Delay(100, timeout.Token);
        }
        timeout.Token.ThrowIfCancellationRequested();
        originalStyle = GetWindowLong(window, -16);
        // WS_CHILD and no top-level frame. SetParent does not adjust these itself.
        var embeddedStyle = (originalStyle & ~unchecked((int)0x80CF0000)) | 0x40000000;
        ShowWindow(window, 0);
        SetWindowLong(window, -16, embeddedStyle);
        Marshal.SetLastPInvokeError(0);
        previousParent = SetParent(window, container);
        var error = Marshal.GetLastPInvokeError();
        if (previousParent == 0 && error != 0)
        {
            SetWindowLong(window, -16, originalStyle); ShowWindow(window, 5);
            throw new Win32Exception(error, "Could not attach the GPU display. The native QEMU window is still available.");
        }
        child = window; AttachKeyboard(input); Fit(true); ShowWindow(child, 5); sizing.Start(); focus.Start(); FocusGuest();
    }
    private void Fit(bool force = false)
    {
        if (!IsAttached || container == 0 || !GetClientRect(container, out var rect)) return;
        if (rect.Right <= 0 || rect.Bottom <= 0) return;
        // The native host accounts for display DPI. SDL handles rendering/input in
        // this pixel-sized area; guest resolution is independent of its window size.
        GetClientRect(child, out var current);
        if (force || current.Right != rect.Right || current.Bottom != rect.Bottom || GetParent(child) != container)
            SetWindowPos(child, 0, 0, 0, rect.Right, rect.Bottom, 0x0034); // NOZORDER, NOACTIVATE, FRAMECHANGED
    }
    public void Detach()
    {
        sizing.Stop(); focus.Stop(); DetachKeyboard(); var window = child; child = 0;
        if (window == 0 || !IsWindow(window)) return;
        // Detach BEFORE the Avalonia parent is destroyed, otherwise Windows would
        // destroy QEMU's child window too. Keep it hidden for a later Open action.
        ShowWindow(window, 0); SetParent(window, previousParent);
        SetWindowLong(window, -16, originalStyle);
        SetWindowPos(window, 0, 0, 0, 0, 0, 0x0037); // preserve position/size, refresh frame
    }
    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Detach(); container = 0; base.DestroyNativeControlCore(control);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint window);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint window, nint parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
