using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Qemik.Core;

// SPICE agent protocol over the VM's private virtio-serial channel. Unlike the
// VNC clipboard, this connection is independent of the accelerated display.
public sealed class GuestAgentClipboard : IDisposable
{
    public const int TextLimit = 512 * 1024;
    private const uint ByDemand = 1 << 5, Selection = 1 << 6;
    private readonly TcpClient socket = new() { NoDelay = true };
    private readonly SemaphoreSlim writing = new(1);
    private NetworkStream stream = null!;
    private int chunkRemaining;
    private volatile uint capabilities;
    private volatile string? offeredText;
    private bool requested;
    private volatile bool enabled;
    public bool Enabled { get => enabled; set => enabled = value; }
    public bool Disconnected { get; private set; }
    public bool Ready => (capabilities & ByDemand) != 0;
    public int Generation { get; private set; }
    public Func<string, Task>? Received { get; set; }
    private int SelectionBytes => (capabilities & Selection) != 0 ? 4 : 0;
    private static uint U32(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt32LittleEndian(data);
    private static void Put(Span<byte> data, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data, value);
    public static async Task<GuestAgentClipboard> ConnectAsync(int port, CancellationToken ct)
    {
        var client = new GuestAgentClipboard();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await client.socket.ConnectAsync("127.0.0.1", port, timeout.Token); client.stream = client.socket.GetStream();
            await client.AnnounceAsync(true, timeout.Token); return client;
        }
        catch { client.Dispose(); throw; }
    }
    private Task AnnounceAsync(bool request, CancellationToken ct)
    {
        var payload = new byte[8]; Put(payload, request ? 1u : 0u); Put(payload.AsSpan(4), ByDemand | Selection);
        return SendAsync(6, payload, ct);
    }
    public async Task<bool> OfferTextAsync(string text, CancellationToken ct)
    {
        if (!Enabled || !Ready) return false;
        if (Encoding.UTF8.GetByteCount(text) > TextLimit) throw new InvalidOperationException("Clipboard text exceeds 512 KiB.");
        offeredText = text;
        var payload = new byte[SelectionBytes + 4]; Put(payload.AsSpan(SelectionBytes), 1);
        await SendAsync(7, payload, ct); return true;
    }
    private async Task SendAsync(uint type, byte[] payload, CancellationToken ct)
    {
        var message = new byte[20 + payload.Length]; Put(message, 1); Put(message.AsSpan(4), type); Put(message.AsSpan(16), (uint)payload.Length); payload.CopyTo(message, 20);
        await writing.WaitAsync(ct);
        try
        {
            for (var offset = 0; offset < message.Length;)
            {
                var size = Math.Min(1024, message.Length - offset); var header = new byte[8]; Put(header, 1); Put(header.AsSpan(4), (uint)size);
                await stream.WriteAsync(header, ct); await stream.WriteAsync(message.AsMemory(offset, size), ct); offset += size;
            }
        }
        finally { writing.Release(); }
    }
    private async Task<byte[]> ReadBytesAsync(int count, CancellationToken ct)
    {
        var result = new byte[count]; var offset = 0;
        while (offset < count)
        {
            if (chunkRemaining == 0)
            {
                var header = new byte[8]; await stream.ReadExactlyAsync(header, ct);
                var port = U32(header); var length = U32(header.AsSpan(4));
                if (length is 0 or > 65536 || port is not (1 or 2)) throw new IOException("Invalid guest agent chunk.");
                if (port == 2) { var ignored = new byte[length]; await stream.ReadExactlyAsync(ignored, ct); continue; }
                chunkRemaining = (int)length;
            }
            var size = Math.Min(count - offset, chunkRemaining);
            await stream.ReadExactlyAsync(result.AsMemory(offset, size), ct); offset += size; chunkRemaining -= size;
        }
        return result;
    }
    public async Task ReadAsync(CancellationToken ct)
    {
        using var retryCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var retry = RetryHandshakeAsync(retryCancellation.Token);
        try { await ReadMessagesAsync(ct); }
        finally
        {
            Disconnected = true; capabilities = 0; offeredText = null;
            retryCancellation.Cancel();
            try { await retry; } catch (OperationCanceledException) { }
        }
    }
    private async Task RetryHandshakeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            // The serial socket can connect before Linux opens its virtio port.
            // QEMU may discard that initial announcement during boot.
            if (!Ready) await AnnounceAsync(true, ct);
        }
    }
    private async Task ReadMessagesAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var header = await ReadBytesAsync(20, ct); var type = U32(header.AsSpan(4)); var size = U32(header.AsSpan(16));
            if (U32(header) != 1 || size > TextLimit + 8) throw new IOException("Invalid or oversized guest agent message.");
            var payload = await ReadBytesAsync((int)size, ct);
            if (type == 6)
            {
                if (payload.Length < 8 || payload.Length % 4 != 0) throw new IOException("Invalid guest agent capabilities.");
                capabilities = U32(payload.AsSpan(4));
                Generation++;
                if (U32(payload) != 0) await AnnounceAsync(false, ct);
                // Offer again after guest login/reconnect even if the host clipboard has not changed.
                offeredText = null; continue;
            }
            if (type is not (4 or 7 or 8 or 9)) continue;
            var skip = SelectionBytes;
            if (payload.Length < skip || (skip != 0 && payload[0] != 0)) continue; // No PRIMARY/selection clipboard.
            var body = payload.AsMemory(skip);
            if (type == 8)
            {
                var text = Enabled && body.Length == 4 && U32(body.Span) == 1 ? offeredText : null;
                var bytes = text is null ? [] : Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"));
                var reply = new byte[skip + 4 + bytes.Length]; Put(reply.AsSpan(skip), text is null ? 0u : 1u); bytes.CopyTo(reply, skip + 4);
                await SendAsync(4, reply, ct); continue;
            }
            if (!Enabled || !Ready) { requested = false; continue; }
            if (type == 7)
            {
                if (body.Length % 4 != 0) throw new IOException("Invalid clipboard offer.");
                offeredText = null; requested = false;
                for (var i = 0; i < body.Length; i += 4)
                {
                    if (U32(body.Span[i..]) != 1) continue;
                    var request = new byte[skip + 4]; Put(request.AsSpan(skip), 1); requested = true; await SendAsync(8, request, ct); break;
                }
            }
            else if (type == 4 && requested)
            {
                requested = false;
                if (body.Length < 4 || U32(body.Span) != 1) continue;
                var text = new UTF8Encoding(false, true).GetString(body.Span[4..]).TrimEnd('\0');
                if (Received is { } receive) await receive(text);
            }
            else if (type == 9) { requested = false; offeredText = null; }
        }
    }
    public void Dispose() { socket.Dispose(); }
}
