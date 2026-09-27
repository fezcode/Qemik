namespace Qemik.Core;

// A forwarded RFB request is not proof that the guest actually changed mode.
public sealed class GuestAutoResize
{
    private (int, int) target, actual;
    private long? lastSent;
    private int attempts;
    public bool NeedsSetup => attempts >= 6 && actual != target;
    public string Status { get; private set; } = "Waiting for the guest display…";
    public void Reset() { target = default; actual = default; lastSent = null; attempts = 0; }
    public bool ShouldRequest((int Width, int Height) desired, (int Width, int Height) current, long now)
    {
        if (desired != target || current != actual) { attempts = 0; lastSent = null; }
        target = desired; actual = current;
        if (current == desired) { Status = $"Auto-fit applied · {current.Width} × {current.Height}"; return false; }
        Status = NeedsSetup ? $"Guest stayed at {current.Width} × {current.Height}. Use Auto-fit setup for Ubuntu." : $"Waiting for {desired.Width} × {desired.Height} · guest is {current.Width} × {current.Height}";
        if (NeedsSetup || lastSent is { } previous && now - previous < 3000) return false;
        lastSent = now; attempts++; return true;
    }
}
