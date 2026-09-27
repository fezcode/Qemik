using System.Diagnostics;

namespace Qemik.Core;

public static class ProcessRunner
{
    public static async Task<string> RunAsync(string exe, IEnumerable<string> args, CancellationToken cancellation = default, int timeoutSeconds = 30)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var p = new Process { StartInfo = new QemuCommand(exe, args.ToArray()).StartInfo() };
        p.Start();
        var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
        try { await p.WaitForExitAsync(timeout.Token); }
        catch { try { p.Kill(true); } catch (InvalidOperationException) { } throw; }
        var output = (await stdout) + (await stderr);
        if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} exited with code {p.ExitCode}.\n{output}");
        return output.Trim();
    }
}
