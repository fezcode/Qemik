using Qemik.Core;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Qemik.Tests;

public sealed class CoreTests
{
    public static string TestDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Qemik.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path;
    }
    public static Preferences Prefs(string? root = null) => new() { QemuDirectory = @"C:\QEMU", LibraryDirectory = root ?? @"C:\VMs" };
    [Fact]
    public void PersistentLibraryRoundTripsHardwareAndPreservesDisksOnRemoval()
    {
        var root = TestDirectory(); var disk = Path.Combine(root, "disk.qcow2"); File.WriteAllText(disk, "important data");
        var vm = VmConfig.Template("Linux"); vm.Drives.Add(new() { Path = disk }); vm.PortForwards.Add(new());
        using (var store = new LibraryStore(root)) { store.Save(vm); store.SavePreferences(Prefs(root)); }
        using var reopened = new LibraryStore(root);
        var restored = Assert.Single(reopened.List()); Assert.Equal(disk, restored.Drives[0].Path); Assert.Equal(2222, restored.PortForwards[0].HostPort);
        Assert.Equal(root, reopened.LoadPreferences().LibraryDirectory);
        reopened.Remove(vm.Id); Assert.Empty(reopened.List()); Assert.Equal("important data", File.ReadAllText(disk));
    }
    [Fact]
    public void CommandsKeepPathsAsSingleArgumentsAndEscapeQemuCommas()
    {
        var vm = new VmConfig { Name = "Linux's $test; & echo", Network = "user" };
        vm.Drives.Add(new() { Path = @"D:\Virtual machines\work,files.qcow2" }); vm.PortForwards.Add(new());
        var command = QemuCommand.Build(vm, Prefs(), 45678);
        Assert.Contains(@"file=D:\Virtual machines\work,,files.qcow2,format=qcow2,if=virtio,id=drive0,media=disk,readonly=off,cache=writeback,discard=unmap", command.Arguments);
        Assert.Contains("user,id=net0,restrict=off,hostfwd=tcp:127.0.0.1:2222-:22", command.Arguments);
        Assert.Contains("tcp:127.0.0.1:45678,server=on,wait=off", command.Arguments);
        Assert.False(command.StartInfo().UseShellExecute); Assert.Contains("guest=" + vm.Name, command.StartInfo().ArgumentList);
        Assert.Contains("Linux''s $test; & echo", command.Preview);
    }
    [Theory]
    [InlineData("-qmp")]
    [InlineData("-readconfig")]
    [InlineData("-daemonize")]
    [InlineData("-mon")]
    public void ManagedControlArgumentsCannotBeOverridden(string option) => Assert.Throws<InvalidDataException>(() => QemuCommand.Validate(new VmConfig { ExtraArguments = option + "\nvalue" }));
    [Fact]
    public void InvalidHardwareAndDuplicateForwardsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => QemuCommand.Validate(new VmConfig { MemoryMiB = 1 }));
        Assert.Throws<InvalidDataException>(() => QemuCommand.Validate(new VmConfig { Architecture = "../../escape" }));
        Assert.Throws<InvalidDataException>(() => QemuCommand.Validate(new VmConfig { Machine = "q35,accel=kvm" }));
        Assert.Throws<InvalidDataException>(() => QemuCommand.Validate(new VmConfig { PortForwards = [new(), new()] }));
        Assert.Throws<InvalidDataException>(() => QemuCommand.Validate(new VmConfig { Usb = false }));
        Assert.Throws<InvalidDataException>(() => QemuCommand.Validate(new VmConfig { Architecture = "aarch64", Accelerator = "whpx" }));
        Assert.Throws<InvalidDataException>(() => AppPaths.VmDirectory(Prefs(), "../outside"));
    }
    [Fact]
    public void FirmwareVariablesArePrivatePerMachineAndSerialIsLocal()
    {
        var vm = new VmConfig { Uefi = true, FirmwareCode = "code.fd", FirmwareVarsTemplate = "vars.fd", Serial = "tcp", Display = "vnc" };
        var command = QemuCommand.Build(vm, Prefs());
        Assert.Contains(command.Arguments, a => a.Contains(vm.Id) && a.Contains("nvram.fd"));
        Assert.DoesNotContain(command.Arguments, a => a.Contains("file=vars.fd"));
        Assert.Contains("127.0.0.1:1", command.Arguments); Assert.Contains("tcp:127.0.0.1:4444,server=on,wait=off", command.Arguments);
    }
    [Fact]
    public void InstallerSelectionAndChecksumAreBoundToExactPublishedFile()
    {
        var selected = QemuInstallation.SelectLatestFile("<a href=\"qemu-w64-setup-20260301.exe\">old</a><a href=\"evil.exe\">bad</a><a href=\"qemu-w64-setup-20260401.exe\">new</a>");
        Assert.Equal("qemu-w64-setup-20260401.exe", selected);
        var hash = new string('a', 128);
        Assert.Equal(hash.ToUpperInvariant(), QemuInstallation.ReadChecksum(hash + " *" + selected, selected));
        Assert.Throws<InvalidDataException>(() => QemuInstallation.ReadChecksum(hash + "  other.exe", selected));
    }
    [Fact]
    public async Task DownloadPromotesOnlyVerifiedBytesAndRemovesBadPartial()
    {
        var bytes = Encoding.UTF8.GetBytes("fixture installer bytes"); var file = "qemu-w64-setup-20260101.exe";
        using var service = new QemuInstallation(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var release = new InstallerRelease(file, new Uri(QemuInstallation.SourceUrl + file), Convert.ToHexString(SHA512.HashData(bytes)), bytes.Length);
        var root = TestDirectory();
        var path = await service.DownloadAsync(release, root, new Progress<TransferProgress>(), default);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release with { Sha512 = new string('0', 128) }, root, new Progress<TransferProgress>(), default));
        Assert.False(File.Exists(path + ".partial")); Assert.Equal(bytes, File.ReadAllBytes(path));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release with { Url = new Uri("https://example.com/evil.exe") }, root, new Progress<TransferProgress>(), default));
    }
    [Fact]
    public async Task QmpNegotiatesSkipsEventsAndSurfacesCommandErrors()
    {
        var server = new TcpListener(IPAddress.Loopback, 0); server.Start(); var port = ((IPEndPoint)server.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var fake = Task.Run(async () =>
        {
            using var socket = await server.AcceptTcpClientAsync(deadline.Token);
            using var reader = new StreamReader(socket.GetStream()); using var writer = new StreamWriter(socket.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
            await writer.WriteLineAsync("{\"QMP\":{\"version\":{}}}");
            for (var n = 0; n < 3; n++)
            {
                using var request = JsonDocument.Parse((await reader.ReadLineAsync(deadline.Token))!);
                var id = request.RootElement.GetProperty("id").GetInt32();
                await writer.WriteLineAsync("{\"event\":\"RESUME\"}");
                if (n == 2) await writer.WriteLineAsync(JsonSerializer.Serialize(new { id, error = new { desc = "device unavailable" } }));
                else await writer.WriteLineAsync(JsonSerializer.Serialize(new { id, @return = new { status = "running" } }));
            }
        });
        try
        {
            using var client = await QmpClient.ConnectAsync(port, deadline.Token);
            var result = await client.ExecuteAsync("query-status", deadline.Token); Assert.Equal("running", result.GetProperty("status").GetString());
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync("bad-command", deadline.Token)); Assert.Contains("unavailable", error.Message);
            await fake;
        }
        finally { server.Stop(); }
    }
    [Fact]
    public async Task DiskCreationNeverOverwritesAnExistingFile()
    {
        var path = Path.Combine(TestDirectory(), "precious.qcow2"); await File.WriteAllTextAsync(path, "keep");
        await Assert.ThrowsAsync<IOException>(() => VmManager.CreateDiskAsync(Prefs(), path, 64, "qcow2")); Assert.Equal("keep", await File.ReadAllTextAsync(path));
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handle(request));
    }
}
