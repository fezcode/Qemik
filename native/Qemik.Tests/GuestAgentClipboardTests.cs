using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Qemik.Core;
using Xunit;

namespace Qemik.Tests;
public sealed class GuestAgentClipboardTests
{
    private static byte[] Numbers(params uint[] values) { var bytes = new byte[values.Length * 4]; for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]); return bytes; }
    private static async Task Send(NetworkStream stream, uint type, byte[] body, CancellationToken ct, int chunkSize = 7)
    {
        var message = Numbers(1, type, 0, 0, (uint)body.Length).Concat(body).ToArray();
        for (var offset = 0; offset < message.Length; offset += chunkSize)
        {
            var chunk = message.AsMemory(offset, Math.Min(chunkSize, message.Length - offset));
            await stream.WriteAsync(Numbers(1, (uint)chunk.Length), ct); await stream.WriteAsync(chunk, ct);
        }
    }
    private static async Task<(uint Type, byte[] Body)> Receive(NetworkStream stream, CancellationToken ct)
    {
        using var message = new MemoryStream();
        do
        {
            var header = new byte[8]; await stream.ReadExactlyAsync(header, ct); Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(header));
            var bytes = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4))]; await stream.ReadExactlyAsync(bytes, ct); message.Write(bytes);
        } while (message.Length < 20 || message.Length < 20 + BinaryPrimitives.ReadUInt32LittleEndian(message.GetBuffer().AsSpan(16)));
        var all = message.ToArray(); return (BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(4)), all[20..]);
    }
    [Fact]
    public async Task UnicodeClipboardNegotiatesFragmentsAndTransfersBothDirectionsOnDemand()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var ct = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = await GuestAgentClipboard.ConnectAsync(((IPEndPoint)listener.LocalEndpoint).Port, ct);
        using var peer = await listener.AcceptTcpClientAsync(ct); var stream = peer.GetStream();
        var announce = await Receive(stream, ct); Assert.Equal(6u, announce.Type); Assert.Equal(Numbers(1, 96), announce.Body);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Enabled = true; client.Received = text => { received.TrySetResult(text); return Task.CompletedTask; };
        var reading = client.ReadAsync(ct);
        await Send(stream, 6, Numbers(1, 96), ct); Assert.Equal(6u, (await Receive(stream, ct)).Type);
        Assert.True(client.Ready);
        var hostText = "Türkçe ✓\r\n" + new string('ş', 3000);
        Assert.True(await client.OfferTextAsync(hostText, ct));
        var offer = await Receive(stream, ct); Assert.Equal(7u, offer.Type); Assert.Equal(Numbers(0, 1), offer.Body);
        await Send(stream, 8, Numbers(0, 1), ct);
        var hostData = await Receive(stream, ct); Assert.Equal(4u, hostData.Type); Assert.Equal(hostText.Replace("\r\n", "\n"), Encoding.UTF8.GetString(hostData.Body[8..]));
        await Send(stream, 7, Numbers(0, 1), ct);
        var request = await Receive(stream, ct); Assert.Equal(8u, request.Type); Assert.Equal(Numbers(0, 1), request.Body);
        await Send(stream, 4, Numbers(0, 1).Concat(Encoding.UTF8.GetBytes("Guest → Windows ✓")).ToArray(), ct);
        Assert.Equal("Guest → Windows ✓", await received.Task.WaitAsync(ct));
        client.Enabled = false;
        Assert.False(await client.OfferTextAsync("disabled", ct));
        await Send(stream, 8, Numbers(0, 1), ct); Assert.Equal(Numbers(0, 0), (await Receive(stream, ct)).Body);
        client.Dispose(); await Assert.ThrowsAnyAsync<Exception>(() => reading);
        Assert.False(client.Ready); Assert.True(client.Disconnected);
    }
    [Fact]
    public async Task HandshakeRetriesWhenGuestPortOpensAfterHostConnects()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var ct = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = await GuestAgentClipboard.ConnectAsync(((IPEndPoint)listener.LocalEndpoint).Port, ct);
        using var peer = await listener.AcceptTcpClientAsync(ct); var stream = peer.GetStream();
        await Receive(stream, ct); // QEMU discarded this before the guest opened the port.
        var reading = client.ReadAsync(ct);
        Assert.Equal(6u, (await Receive(stream, ct)).Type);
        await Send(stream, 6, Numbers(1, 96), ct);
        Assert.Equal(6u, (await Receive(stream, ct)).Type); Assert.True(client.Ready);
        client.Dispose(); await Assert.ThrowsAnyAsync<Exception>(() => reading);
    }
    [Fact]
    public async Task OversizedAgentMessageIsRejectedBeforeAllocatingPayload()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var ct = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = await GuestAgentClipboard.ConnectAsync(((IPEndPoint)listener.LocalEndpoint).Port, ct);
        using var peer = await listener.AcceptTcpClientAsync(ct); var stream = peer.GetStream(); await Receive(stream, ct);
        await stream.WriteAsync(Numbers(1, 20).Concat(Numbers(1, 4, 0, 0, GuestAgentClipboard.TextLimit + 9)).ToArray(), ct);
        await Assert.ThrowsAsync<IOException>(() => client.ReadAsync(ct));
    }
}
