using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Qemik.Core;
using Xunit;

namespace Qemik.Tests;
public sealed class GuestDisplayTests
{
    private static byte[] ExtendedSize(int width, int height, int reason = 0, int result = 0)
    {
        var packet = new byte[36]; packet[0] = 0; packet[3] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)reason); BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), (ushort)result);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8), (ushort)width); BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), (ushort)height);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12), -308); packet[16] = 1; packet[23] = 7;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(28), (ushort)width); BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(30), (ushort)height); packet[35] = 9;
        return packet;
    }
    [Fact]
    public async Task ResizeNegotiatesScreenIdentityAndWaitsForGuestWithoutFullRequestLoop()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = timeout.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(ct); var stream = peer.GetStream(); await Handshake(stream, ct); await Read(stream, 10, ct);
            await stream.WriteAsync(ExtendedSize(2, 1), ct);
            var resize = await Read(stream, 24, ct); Assert.Equal(251, resize[0]); Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(resize.AsSpan(8))); Assert.Equal(9u, BinaryPrimitives.ReadUInt32BigEndian(resize.AsSpan(20)));
            Assert.Equal(800, BinaryPrimitives.ReadUInt16BigEndian(resize.AsSpan(2)));
            Assert.Equal(1, (await Read(stream, 10, ct))[1]);
            await stream.WriteAsync(ExtendedSize(2, 1, 1, 4), ct); Assert.Equal(1, (await Read(stream, 10, ct))[1]);
            await stream.WriteAsync(ExtendedSize(800, 600), ct); Assert.Equal(1, (await Read(stream, 10, ct))[1]);
        }, ct);
        try
        {
            using var display = await GuestDisplayClient.ConnectAsync(((IPEndPoint)listener.LocalEndpoint).Port, ct); var frames = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => display.ResizeAsync(800, 600, ct));
            await Assert.ThrowsAnyAsync<IOException>(() => display.ReadFramesAsync(async frame =>
            {
                frames++;
                if (frames == 1) { Assert.True(display.SupportsResize); await display.ResizeAsync(800, 600, ct); }
                if (frames == 2) { Assert.Equal(2, frame.Width); Assert.Contains("waiting", display.ResizeStatus); }
                if (frames == 3) Assert.Equal(800, frame.Width);
            }, ct));
            await server; Assert.Equal(3, frames);
        }
        finally { listener.Stop(); }
    }
    private static byte[] ClipboardPacket(uint flags, byte[]? data = null)
    {
        data ??= []; var packet = new byte[12 + data.Length]; packet[0] = 3;
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4), -(4 + data.Length)); BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), flags); data.CopyTo(packet, 12); return packet;
    }
    [Fact]
    public async Task UnicodeClipboardNegotiatesBothDirections()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = timeout.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(ct); var stream = peer.GetStream(); await Handshake(stream, ct); await Read(stream, 10, ct);
            await stream.WriteAsync(ClipboardPacket(0x1b000001, new byte[4]), ct); await Read(stream, 16, ct);
            await stream.WriteAsync(ExtendedSize(2, 1), ct);
            var notify = await Read(stream, 12, ct); Assert.Equal(6, notify[0]); Assert.Equal(0x08000001u, BinaryPrimitives.ReadUInt32BigEndian(notify.AsSpan(8))); await Read(stream, 10, ct);
            await stream.WriteAsync(ClipboardPacket(0x02000001), ct);
            var header = await Read(stream, 12, ct); var payload = await Read(stream, -BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4)) - 4, ct);
            using (var compressed = new System.IO.Compression.ZLibStream(new MemoryStream(payload), System.IO.Compression.CompressionMode.Decompress))
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(await Read(compressed, 4, ct));
                Assert.Equal("Türkçe 🦴\0", Encoding.UTF8.GetString(await Read(compressed, length, ct)));
            }
            await stream.WriteAsync(ClipboardPacket(0x08000001), ct);
            var request = await Read(stream, 12, ct); Assert.Equal(0x02000001u, BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(8)));
            await stream.WriteAsync(ClipboardPacket(0x10000001, payload), ct);
        }, ct);
        try
        {
            using var display = await GuestDisplayClient.ConnectAsync(((IPEndPoint)listener.LocalEndpoint).Port, ct); display.ClipboardEnabled = true;
            string? received = null; display.ClipboardReceived = text => { received = text; return Task.CompletedTask; };
            await Assert.ThrowsAnyAsync<IOException>(() => display.ReadFramesAsync(_ => display.SendClipboardAsync("Türkçe 🦴", ct), ct));
            await server; Assert.Equal("Türkçe 🦴", received);
        }
        finally { listener.Stop(); }
    }
    [Fact]
    public void AcceleratedDisplayAudioAndClipboardBuildExplicitBackends()
    {
        var vm = new VmConfig { Display = "qemik", Video = "virtio-vga-gl", Audio = "intel-hda", SharedClipboard = true };
        var args = QemuCommand.Build(vm, CoreTests.Prefs()).Arguments;
        Assert.Contains("egl-headless", args); Assert.Contains("hda-output,audiodev=audio0", args); Assert.Contains("dsound,id=audio0,in.voices=0", args);
        Assert.Contains("qemu-vdagent,id=clipboard-agent,name=vdagent,clipboard=on,mouse=off", args);
    }
    [Fact]
    public async Task IncrementalFramesReuseBufferAndReportOnlyChangedPixels()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = timeout.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(ct); var stream = peer.GetStream(); await Handshake(stream, ct); await Read(stream, 10, ct);
            await stream.WriteAsync(new byte[] { 0, 0, 0, 1, 0, 0, 0, 0, 0, 2, 0, 1, 0, 0, 0, 0, 10, 20, 30, 0, 40, 50, 60, 0 }, ct); await Read(stream, 10, ct);
            await stream.WriteAsync(new byte[] { 0, 0, 0, 1, 0, 1, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 90, 80, 70, 0 }, ct); await Read(stream, 10, ct);
        }, ct);
        try
        {
            using var display = await GuestDisplayClient.ConnectAsync(((IPEndPoint)listener.LocalEndpoint).Port, ct); byte[]? first = null; var frames = 0;
            await Assert.ThrowsAnyAsync<IOException>(() => display.ReadFramesAsync(frame =>
            {
                if (++frames == 1) first = frame.Pixels;
                else { Assert.Same(first, frame.Pixels); Assert.Equal(new GuestRegion(1, 0, 1, 1), frame.Dirty); Assert.Equal(new byte[] { 10, 20, 30, 0, 90, 80, 70, 0 }, frame.Pixels); }
                return Task.CompletedTask;
            }, ct));
            await server; Assert.Equal(2, frames);
        }
        finally { listener.Stop(); }
    }
    private static async Task<byte[]> Read(Stream stream, int count, CancellationToken ct) { var data = new byte[count]; await stream.ReadExactlyAsync(data, ct); return data; }
    private static async Task Handshake(NetworkStream stream, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"), ct);
        Assert.Equal("RFB 003.008\n", Encoding.ASCII.GetString(await Read(stream, 12, ct)));
        await stream.WriteAsync(new byte[] { 1, 1 }, ct); Assert.Equal(1, (await Read(stream, 1, ct))[0]);
        await stream.WriteAsync(new byte[4], ct); Assert.Equal(1, (await Read(stream, 1, ct))[0]);
        var init = new byte[24]; init[1] = 2; init[3] = 1; await stream.WriteAsync(init, ct);
        var format = await Read(stream, 20, ct); Assert.Equal(32, format[4]); Assert.Equal(16, format[14]);
        var encodings = await Read(stream, 20, ct); Assert.Equal(-223, BinaryPrimitives.ReadInt32BigEndian(encodings.AsSpan(8))); Assert.Equal(-308, BinaryPrimitives.ReadInt32BigEndian(encodings.AsSpan(12)));
    }
    [Fact]
    public async Task FramebufferHandlesFragmentationResizeAndInputMessages()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = timeout.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(ct); var stream = peer.GetStream(); await Handshake(stream, ct);
            var request = await Read(stream, 10, ct); Assert.Equal(3, request[0]); Assert.Equal(0, request[1]);
            byte[] update = [0, 0, 0, 1, 0, 0, 0, 0, 0, 2, 0, 1, 0, 0, 0, 0, 10, 20, 30, 0, 40, 50, 60, 0];
            foreach (var value in update) await stream.WriteAsync(new byte[] { value }, ct);
            var key = await Read(stream, 8, ct); Assert.Equal(4, key[0]); Assert.Equal(1, key[1]); Assert.Equal(0xff0dU, BinaryPrimitives.ReadUInt32BigEndian(key.AsSpan(4)));
            var pointer = await Read(stream, 6, ct); Assert.Equal(5, pointer[0]); Assert.Equal(1, pointer[1]); Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(pointer.AsSpan(2)));
            await Read(stream, 10, ct);
            await stream.WriteAsync(new byte[] { 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 2, 255, 255, 255, 33 }, ct);
            var full = await Read(stream, 10, ct); Assert.Equal(0, full[1]); Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(full.AsSpan(8)));
        }, ct);
        try
        {
            using var display = await GuestDisplayClient.ConnectAsync(((IPEndPoint)listener.LocalEndpoint).Port, ct); var frames = 0;
            await Assert.ThrowsAnyAsync<IOException>(() => display.ReadFramesAsync(async frame =>
            {
                frames++;
                if (frames == 1) { Assert.Equal(new byte[] { 10, 20, 30, 0, 40, 50, 60, 0 }, frame.Pixels); await display.KeyAsync(0xff0d, true, ct); await display.PointerAsync(999, 99, 1, ct); }
                else { Assert.Equal(1, frame.Width); Assert.Equal(2, frame.Height); Assert.Equal(new byte[] { 10, 20, 30, 0, 0, 0, 0, 0 }, frame.Pixels); }
            }, ct));
            await server; Assert.Equal(2, frames);
        }
        finally { listener.Stop(); }
    }
    [Fact]
    public async Task RejectsOutOfBoundsFramebufferUpdates()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = timeout.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(ct); var stream = peer.GetStream(); await Handshake(stream, ct); await Read(stream, 10, ct);
            await stream.WriteAsync(new byte[] { 0, 0, 0, 1, 0, 0, 0, 0, 255, 255, 0, 1, 0, 0, 0, 0 }, ct);
        }, ct);
        try
        {
            using var display = await GuestDisplayClient.ConnectAsync(((IPEndPoint)listener.LocalEndpoint).Port, ct);
            await Assert.ThrowsAsync<IOException>(() => display.ReadFramesAsync(_ => Task.CompletedTask, ct)); await server;
        }
        finally { listener.Stop(); }
    }
    [Fact]
    public void GuestDisplayUsesAssignedLoopbackPortWithoutAnExternalWindow()
    {
        var vm = new VmConfig { Display = "qemik" }; var command = QemuCommand.Build(vm, CoreTests.Prefs(), 62000, 62001);
        Assert.Contains("127.0.0.1:56101", command.Arguments); Assert.DoesNotContain("sdl", command.Arguments); Assert.DoesNotContain("qemik", command.Arguments);
    }
}
