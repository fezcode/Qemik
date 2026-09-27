using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Qemik.Core;

public sealed class QmpClient : IDisposable
{
    private readonly TcpClient socket;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly SemaphoreSlim gate = new(1);
    private int sequence;
    private QmpClient(TcpClient client)
    {
        socket = client;
        reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 4096, true);
        writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\r\n" };
    }
    public static async Task<QmpClient> ConnectAsync(int port, CancellationToken ct)
    {
        var socket = new TcpClient();
        try
        {
            await socket.ConnectAsync("127.0.0.1", port, ct);
            var client = new QmpClient(socket);
            try
            {
                var greeting = await client.reader.ReadLineAsync(ct) ?? throw new IOException("QEMU closed its control channel.");
                using var json = JsonDocument.Parse(greeting);
                if (!json.RootElement.TryGetProperty("QMP", out _)) throw new IOException("Invalid QMP greeting.");
                await client.ExecuteAsync("qmp_capabilities", ct);
                return client;
            }
            catch { client.Dispose(); throw; }
        }
        catch { socket.Dispose(); throw; }
    }
    public Task<JsonElement> ExecuteAsync(string command, CancellationToken ct = default) => ExecuteAsync(command, null, ct);
    public async Task<JsonElement> ExecuteAsync(string command, object? arguments, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await gate.WaitAsync(timeout.Token);
        try
        {
            var id = ++sequence;
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { execute = command, arguments = arguments ?? new { }, id }).AsMemory(), timeout.Token);
            while (true)
            {
                var line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("QEMU disconnected.");
                using var json = JsonDocument.Parse(line); var root = json.RootElement;
                if (!root.TryGetProperty("id", out var replyId) || replyId.GetInt32() != id) continue;
                if (root.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetProperty("desc").GetString());
                return root.GetProperty("return").Clone();
            }
        }
        finally { gate.Release(); }
    }
    public void Dispose() { writer.Dispose(); reader.Dispose(); socket.Dispose(); }
}
