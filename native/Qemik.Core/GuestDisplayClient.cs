using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Qemik.Core;

public readonly record struct GuestRegion(int X, int Y, int Width, int Height);
// Pixels are borrowed until the presentation callback completes. Copy them if retaining a frame.
public sealed record GuestFrame(int Width, int Height, byte[] Pixels, GuestRegion? Dirty = null);

// RFB 3.8, restricted to the local QEMU session. Raw BGRX pixels and desktop resizing.
public sealed partial class GuestDisplayClient : IDisposable
{
    private readonly TcpClient socket = new() { NoDelay = true };
    private readonly SemaphoreSlim sendGate = new(1);
    private NetworkStream stream = null!;
    private byte[] pixels = [];
    public int Width { get; private set; }
    public int Height { get; private set; }
    private sealed record Screen(uint Id, uint Flags);
    private volatile Screen? screen;
    public bool SupportsResize => screen is not null;
    public string ResizeStatus { get; private set; } = "Waiting for guest resize support";
    private GuestDisplayClient() { }
    private static ushort U16(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt16BigEndian(bytes);
    private static int I32(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadInt32BigEndian(bytes);
    private async Task<byte[]> ReadAsync(int count, CancellationToken ct)
    {
        var bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, ct); return bytes;
    }
    public static async Task<GuestDisplayClient> ConnectAsync(int port, CancellationToken ct = default)
    {
        var client = new GuestDisplayClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await client.socket.ConnectAsync("127.0.0.1", port, timeout.Token); client.stream = client.socket.GetStream();
            var version = Encoding.ASCII.GetString(await client.ReadAsync(12, timeout.Token));
            if (version != "RFB 003.008\n") throw new IOException("The local guest display did not offer RFB 3.8.");
            await client.SendAsync(Encoding.ASCII.GetBytes(version), timeout.Token);
            var count = (await client.ReadAsync(1, timeout.Token))[0];
            if (count == 0 || !(await client.ReadAsync(count, timeout.Token)).Contains((byte)1)) throw new IOException("The local guest display did not offer the expected session authentication.");
            await client.SendAsync([1], timeout.Token);
            if (I32(await client.ReadAsync(4, timeout.Token)) != 0) throw new IOException("The guest display rejected the connection.");
            await client.SendAsync([1], timeout.Token); // Shared session, reconnecting never disconnects another viewer.
            var init = await client.ReadAsync(24, timeout.Token);
            client.Resize(U16(init), U16(init.AsSpan(2)));
            var nameSize = I32(init.AsSpan(20));
            if (nameSize is < 0 or > 65536) throw new IOException("Invalid guest display name.");
            await client.ReadAsync(nameSize, timeout.Token);
            await client.SendAsync([0, 0, 0, 0, 32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0], timeout.Token);
            await client.SendAsync([2, 0, 0, 4, 0, 0, 0, 0, 255, 255, 255, 33, 255, 255, 254, 204, 192, 161, 229, 206], timeout.Token); // Raw, desktop resize, extended clipboard.
            return client;
        }
        catch { client.Dispose(); throw; }
    }
    private void Resize(int width, int height)
    {
        if (width is < 1 or > 8192 || height is < 1 or > 8192 || (long)width * height > 16777216) throw new IOException("Unsupported guest display dimensions.");
        var resized = new byte[width * height * 4];
        for (var row = 0; row < Math.Min(height, Height); row++) Buffer.BlockCopy(pixels, row * Width * 4, resized, row * width * 4, Math.Min(width, Width) * 4);
        Width = width; Height = height; pixels = resized;
    }
    public async Task ReadFramesAsync(Func<GuestFrame, Task> present, CancellationToken ct)
    {
        await RequestFrameAsync(false, ct);
        while (!ct.IsCancellationRequested)
        {
            var type = (await ReadAsync(1, ct))[0];
            switch (type)
            {
                case 0:
                    var header = await ReadAsync(3, ct); var count = U16(header.AsSpan(1)); var resized = false; var legacyResize = false;
                    var left = Width; var top = Height; var right = 0; var bottom = 0;
                    for (var i = 0; i < count; i++)
                    {
                        var rect = await ReadAsync(12, ct); int x = U16(rect), y = U16(rect.AsSpan(2)), w = U16(rect.AsSpan(4)), h = U16(rect.AsSpan(6));
                        var encoding = I32(rect.AsSpan(8));
                        if (encoding == -223) { Resize(w, h); resized = true; legacyResize = true; continue; }
                        if (encoding == -308)
                        {
                            var layoutHeader = await ReadAsync(4, ct);
                            var layout = await ReadAsync(layoutHeader[0] * 16, ct);
                            if (x == 1 && y != 0)
                            {
                                ResizeStatus = y == 4 ? "Resize requested; waiting for the guest" : "Guest rejected resize; use Ubuntu Display settings";
                                continue;
                            }
                            screen = layoutHeader[0] == 1 ? new Screen(BinaryPrimitives.ReadUInt32BigEndian(layout), BinaryPrimitives.ReadUInt32BigEndian(layout.AsSpan(12))) : null;
                            ResizeStatus = SupportsResize ? "Guest resize available" : "Automatic resize needs a single guest screen";
                            if (w != Width || h != Height) { Resize(w, h); resized = true; }
                            continue;
                        }
                        if (encoding != 0 || x + w > Width || y + h > Height) throw new IOException("Invalid guest framebuffer rectangle.");
                        if (w == Width && x == 0) await stream.ReadExactlyAsync(pixels.AsMemory(y * Width * 4, h * Width * 4), ct);
                        else for (var row = 0; row < h; row++) await stream.ReadExactlyAsync(pixels.AsMemory(((y + row) * Width + x) * 4, w * 4), ct);
                        if (w > 0 && h > 0) { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x + w); bottom = Math.Max(bottom, y + h); }
                    }
                    // Awaiting presentation provides backpressure and keeps the borrowed buffer stable.
                    if (count > 0) await present(new GuestFrame(Width, Height, pixels, resized ? null : new GuestRegion(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top))));
                    await Task.Delay(16, ct);
                    // ExtendedDesktopSize is also sent for full requests; repeating full requests here loops forever.
                    await RequestFrameAsync(!legacyResize, ct); break;
                case 1:
                    var palette = await ReadAsync(5, ct); await ReadAsync(U16(palette.AsSpan(3)) * 6, ct); break;
                case 2: break; // Bell.
                case 3:
                    var clipboard = await ReadAsync(7, ct); var size = I32(clipboard.AsSpan(3));
                    if (size is < -1048576 or > 1048576) throw new IOException("Unsupported guest clipboard message.");
                    await ReceiveClipboardAsync(size, ct); break;
                default: throw new IOException("Unsupported guest display message.");
            }
        }
    }
    public Task ResizeAsync(int width, int height, CancellationToken ct = default)
    {
        var target = screen ?? throw new InvalidOperationException("This guest display has not advertised resize support.");
        if (width is < 320 or > 3840 || height is < 200 or > 2160) throw new ArgumentOutOfRangeException(nameof(width), "Choose a resolution between 320×200 and 3840×2160.");
        var message = new byte[24]; message[0] = 251; message[6] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), (ushort)width); BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), (ushort)height);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(8), target.Id);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(16), (ushort)width); BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(18), (ushort)height);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(20), target.Flags);
        return SendAsync(message, ct);
    }
    private Task RequestFrameAsync(bool incremental, CancellationToken ct)
    {
        var message = new byte[10]; message[0] = 3; message[1] = incremental ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(6), (ushort)Width); BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(8), (ushort)Height);
        return SendAsync(message, ct);
    }
    public Task KeyAsync(uint keysym, bool down, CancellationToken ct = default)
    {
        var message = new byte[8]; message[0] = 4; message[1] = down ? (byte)1 : (byte)0; BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), keysym); return SendAsync(message, ct);
    }
    public Task PointerAsync(int x, int y, byte buttons, CancellationToken ct = default)
    {
        var message = new byte[6]; message[0] = 5; message[1] = buttons;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), (ushort)Math.Clamp(x, 0, Width - 1));
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), (ushort)Math.Clamp(y, 0, Height - 1)); return SendAsync(message, ct);
    }
    private async Task SendAsync(byte[] bytes, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await sendGate.WaitAsync(timeout.Token);
        try { await stream.WriteAsync(bytes, timeout.Token); }
        finally { sendGate.Release(); }
    }
    public void Dispose() => socket.Dispose();
}
