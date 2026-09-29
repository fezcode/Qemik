using System.Diagnostics;
using Qemik.Core;
using Xunit;

namespace Qemik.Tests;

public sealed class ChildProcessJobTests
{
    [Fact]
    public void GuestProcessesEndWhenQemikGoesAway()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Job objects are Windows-only.");
        var job = new ChildProcessJob();
        var start = new ProcessStartInfo("ping.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-n"); start.ArgumentList.Add("60"); start.ArgumentList.Add("127.0.0.1");
        using var child = Process.Start(start)!;
        try
        {
            job.Add(child);
            Assert.False(child.WaitForExit(500));
            // Windows closes Qemik's job handle however its process ends: normal exit, End task or a crash.
            job.Dispose();
            Assert.True(child.WaitForExit(5000), "A guest outlived the Qemik process that started it.");
        }
        finally { if (!child.HasExited) child.Kill(); }
    }
}
