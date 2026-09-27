using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Qemik.Core;

// The library file lock remains authoritative, including for CLI operations.
// This channel only asks its owner to show its window; it cannot execute commands.
public sealed class LibraryActivation : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task listener;
    private static string PipeName(string root)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        return "qemik-activate-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
    }
    public LibraryActivation(string root, Action activate)
    {
        listener = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName(root), PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(stop.Token);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    await pipe.WriteAsync(BitConverter.GetBytes(Environment.ProcessId), timeout.Token);
                    var request = new byte[1]; await pipe.ReadExactlyAsync(request, timeout.Token);
                    if (request[0] != 1) continue;
                    activate();
                    await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { if (!stop.IsCancellationRequested) await Task.Delay(100, stop.Token).ConfigureAwait(false); }
            }
        });
    }
    public static async Task<bool> NotifyAsync(string root, Action<int>? grantForeground = null)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var pipe = new NamedPipeClientStream(".", PipeName(root), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var pid = new byte[4]; await pipe.ReadExactlyAsync(pid, timeout.Token).ConfigureAwait(false);
            grantForeground?.Invoke(BitConverter.ToInt32(pid));
            await pipe.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
            var ack = new byte[1]; await pipe.ReadExactlyAsync(ack, timeout.Token).ConfigureAwait(false);
            return ack[0] == 1;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }
    public void Dispose()
    {
        stop.Cancel();
        try { listener.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        stop.Dispose();
    }
}
