using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Qemik.Core;

public sealed partial class GuestDisplayClient
{
    private const uint TextFormat = 1, Caps = 1u << 24, Request = 1u << 25, Peek = 1u << 26, Notify = 1u << 27, Provide = 1u << 28;
    public const int ClipboardLimit = 512 * 1024;
    private volatile string? clipboardText;
    private volatile bool extendedClipboard;
    public bool ClipboardEnabled { get; set; }
    public Func<string, Task>? ClipboardReceived { get; set; }
    public async Task SendClipboardAsync(string text, CancellationToken ct = default)
    {
        if (!ClipboardEnabled) return;
        if (Encoding.UTF8.GetByteCount(text) >= ClipboardLimit) throw new InvalidOperationException("Clipboard text must be smaller than 512 KiB.");
        if (!extendedClipboard) throw new InvalidOperationException("Waiting for QEMU's Unicode clipboard support.");
        clipboardText = text;
        await SendClipboardPacketAsync(Notify | TextFormat, [], ct);
    }
    private Task SendClipboardPacketAsync(uint flags, byte[] payload, CancellationToken ct)
    {
        var packet = new byte[12 + payload.Length]; packet[0] = 6;
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4), -(4 + payload.Length));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), flags); payload.CopyTo(packet, 12);
        return SendAsync(packet, ct);
    }
    private async Task ReceiveClipboardAsync(int size, CancellationToken ct)
    {
        var data = await ReadAsync(Math.Abs(size), ct);
        if (size >= 0)
        {
            if (ClipboardEnabled && ClipboardReceived is { } receive) await receive(Encoding.Latin1.GetString(data));
            return;
        }
        if (data.Length < 4) throw new IOException("Invalid clipboard header.");
        var flags = BinaryPrimitives.ReadUInt32BigEndian(data);
        if ((flags & Caps) != 0)
        {
            extendedClipboard = (flags & TextFormat) != 0;
            await SendClipboardPacketAsync(Caps | TextFormat | Request | Notify | Provide, new byte[4], ct);
            return;
        }
        if (!ClipboardEnabled) return;
        if ((flags & Peek) != 0) await SendClipboardPacketAsync(Notify | (clipboardText is null ? 0 : TextFormat), [], ct);
        if ((flags & Notify) != 0)
        {
            clipboardText = null;
            if ((flags & TextFormat) != 0) await SendClipboardPacketAsync(Request | TextFormat, [], ct);
        }
        if ((flags & Request) != 0 && (flags & TextFormat) != 0 && clipboardText is { } text)
        {
            var bytes = Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\0");
            if (bytes.Length > ClipboardLimit) return;
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, true))
            {
                var length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
                await zlib.WriteAsync(length, ct); await zlib.WriteAsync(bytes, ct);
            }
            await SendClipboardPacketAsync(Provide | TextFormat, output.ToArray(), ct);
        }
        if ((flags & Provide) != 0 && (flags & 0xffff) == TextFormat)
        {
            using var input = new MemoryStream(data, 4, data.Length - 4);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            var length = new byte[4]; await zlib.ReadExactlyAsync(length, ct); var count = I32(length);
            if (count is < 0 or > ClipboardLimit) throw new IOException("Guest clipboard exceeds 512 KiB.");
            var receivedText = new byte[count]; await zlib.ReadExactlyAsync(receivedText, ct);
            if (ClipboardReceived is { } receive) await receive(new UTF8Encoding(false, true).GetString(receivedText).TrimEnd('\0'));
        }
    }
}
