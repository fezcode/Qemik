using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qemik.Core;
using Qemik.Desktop;
using Xunit;

namespace Qemik.Tests;

public sealed class SharingBlueprintTests
{
    [Fact]
    public void ResolutionCapReducesPhysicalPixelWorkWithoutRemoving4kOption()
    {
        Assert.Equal((1920, 1080), GuestSurface.DesiredResolution(new Avalonia.Size(1920, 1080), 2, 1920, 1080));
        Assert.Equal((2560, 1440), GuestSurface.DesiredResolution(new Avalonia.Size(1920, 1080), 2, 2560, 1440));
        Assert.Equal((3840, 2160), GuestSurface.DesiredResolution(new Avalonia.Size(1920, 1080), 2));
        Assert.Equal((1920, 540), GuestSurface.DesiredResolution(new Avalonia.Size(5120, 1440), 1, 1920, 1080));
    }
    [Fact]
    public async Task MachineReadableOutputExcludesPowerShellDiagnosticsButPreservesFailures()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        static string[] Args(string script) => ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))];
        var output = await ProcessRunner.RunAsync(shell, Args("[Console]::Out.WriteLine('[]'); [Console]::Error.WriteLine('#< CLIXML'); [Console]::Error.WriteLine('<Objs>progress</Objs>')"), standardOutputOnly: true);
        Assert.Empty(JsonSerializer.Deserialize<HostFolderShare[]>(output)!);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessRunner.RunAsync(shell, Args("[Console]::Error.WriteLine('share access denied'); exit 1"), standardOutputOnly: true));
        Assert.Contains("share access denied", error.Message);
    }
    [Fact]
    public async Task WindowsSharedFolderListingReturnsStructuredRecords()
    {
        if (Environment.GetEnvironmentVariable("QEMIK_SHARE_LIST_VM") is not { } id) Assert.Skip("Opt in with QEMIK_SHARE_LIST_VM for a read-only Windows SMB listing check.");
        else
        {
            var shares = await new WindowsHostFolderSharing().ListAsync(id);
            Assert.All(shares, share => Assert.StartsWith("Qemik_" + id[..8] + "_", share.Name));
        }
    }
    [Fact]
    public void EnablePlaybackPreservesSavedMachineAndUsesPlaybackOnlyDefaults()
    {
        using var store = new LibraryStore(CoreTests.TestDirectory());
        var vm = new VmConfig { Name = "Installed Ubuntu", AudioCapture = true, MemoryMiB = 8192, SharedClipboard = true, Drives = [new() { Path = "installed.qcow2" }] };
        store.Save(vm); store.EnableAudioPlayback(vm.Id);
        var saved = Assert.Single(store.List());
        vm.Audio = "intel-hda"; vm.AudioCapture = false;
        Assert.Equal(JsonSerializer.Serialize(vm), JsonSerializer.Serialize(saved));
        foreach (var guest in new[] { "Linux", "Windows" })
        {
            var template = VmConfig.Template(guest); Assert.Equal("intel-hda", template.Audio); Assert.False(template.AudioCapture);
            template.SetArchitecture("aarch64"); Assert.Equal("none", template.Audio);
        }
    }
    [Theory]
    [InlineData(1920, 1080, 2, 3840, 2160)]
    [InlineData(3840, 2160, 1, 3840, 2160)]
    [InlineData(5120, 1440, 1, 3840, 1080)]
    [InlineData(800, 600, 1.5, 1200, 900)]
    public void FitResolutionUsesPhysicalPixelsAndCapsAt4k(double width, double height, double scale, int expectedWidth, int expectedHeight)
        => Assert.Equal((expectedWidth, expectedHeight), GuestSurface.DesiredResolution(new Avalonia.Size(width, height), scale));

    [Fact]
    public async Task ShareScriptsKeepPathsLiteralAndLimitAccessToSelectedAccount()
    {
        var id = Guid.NewGuid().ToString("N"); var path = @"D:\Folder O'Brien $(Write-Output injected); test";
        var create = WindowsHostFolderSharing.BuildCreateScript(id, "Files", path, true, @"HOST\user");
        Assert.Contains("-ReadAccess 'HOST\\user'", create); Assert.DoesNotContain("Everyone", create); Assert.DoesNotContain("ChangeAccess", create);
        Assert.Contains("-EncryptData $true", create); Assert.DoesNotContain("Set-NetFirewall", create);
        var writable = WindowsHostFolderSharing.BuildCreateScript(id, "Files", path, false, @"HOST\user"); Assert.Contains("-ChangeAccess", writable);
        Assert.Throws<ArgumentException>(() => WindowsHostFolderSharing.ShareName(id, "unsafe'; command"));
        Assert.Throws<ArgumentException>(() => WindowsHostFolderSharing.BuildRemoveScript(id, new("C$", @"C:\", "")));
        var remove = WindowsHostFolderSharing.BuildRemoveScript(id, new(WindowsHostFolderSharing.ShareName(id, "Files"), path, ""));
        Assert.Contains("Description -cne", remove); Assert.Contains("$share.Path -ine", remove); Assert.DoesNotContain("Remove-Item", remove);
        if (!OperatingSystem.IsWindows()) return;
        var file = Path.Combine(CoreTests.TestDirectory(), "share-script.ps1"); await File.WriteAllTextAsync(file, create);
        var inspect = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $tokens=$null; $errors=$null; $ast=[Management.Automation.Language.Parser]::ParseFile('" + file.Replace("'", "''") + "',[ref]$tokens,[ref]$errors); if($errors.Count){throw ($errors | Out-String)}; $ast.FindAll({param($n) $n -is [Management.Automation.Language.CommandAst]},$true) | ForEach-Object {$_.GetCommandName()}; $ast.FindAll({param($n) $n -is [Management.Automation.Language.StringConstantExpressionAst] -and $n.Value.StartsWith('D:\\Folder')},$true) | ForEach-Object {$_.Value}";
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var result = await ProcessRunner.RunAsync(shell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(inspect))]);
        Assert.Contains(path, result); Assert.DoesNotContain("\nWrite-Output\n", result);
    }

    [Fact]
    public async Task BlueprintCopiesFlattenDisksAndKeepIndependentIdentityAndFirmware()
    {
        if (Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR") is null) Assert.Skip("Opt in with QEMIK_QEMU_DIR to validate independent blueprint copies.");
        var root = CoreTests.TestDirectory(); var prefs = CoreTests.Prefs(root); prefs.QemuDirectory = Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR")!;
        using var store = new LibraryStore(Path.Combine(root, "library")); using var manager = new VmManager();
        var imageTool = Path.Combine(prefs.QemuDirectory, "qemu-img.exe"); var ioTool = Path.Combine(prefs.QemuDirectory, "qemu-io.exe");
        var baseDisk = Path.Combine(root, "base.qcow2"); await VmManager.CreateDiskAsync(prefs, baseDisk, 1, "qcow2");
        await ProcessRunner.RunAsync(ioTool, ["-f", "qcow2", "-c", "write -P 0x5a 0 4096", baseDisk]);
        var sourceDisk = Path.Combine(root, "blueprint.qcow2"); await ProcessRunner.RunAsync(imageTool, ["create", "-f", "qcow2", "-F", "qcow2", "-b", baseDisk, sourceDisk]);
        var source = new VmConfig { Name = "Installed system", Uefi = true, Display = "vnc", Serial = "tcp", PortForwards = [new()], ExtraArguments = "-no-hpet", Drives = [new() { Path = sourceDisk }, new() { Path = "installer.iso", CdRom = true, Format = "raw", Interface = "ide" }] };
        store.Save(source); await manager.SetBlueprintAsync(source, true, store); Assert.True(store.List().Single().IsBlueprint);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(source, prefs));
        var sourceFolder = AppPaths.VmDirectory(prefs, source.Id); Directory.CreateDirectory(sourceFolder); await File.WriteAllBytesAsync(Path.Combine(sourceFolder, "nvram.fd"), [1, 2, 3, 4]);
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(sourceDisk));
        var first = await manager.CloneBlueprintAsync(source, prefs, "First"); var second = await manager.CloneBlueprintAsync(source, prefs, "Second");
        Assert.False(first.IsBlueprint); Assert.NotEqual(source.Id, first.Id); Assert.NotEqual(first.Id, second.Id); Assert.NotEqual(source.MacAddress, first.MacAddress); Assert.NotEqual(first.MacAddress, second.MacAddress);
        Assert.Empty(first.PortForwards); Assert.Empty(first.ExtraArguments); Assert.Equal("none", first.Serial); Assert.Equal("qemik", first.Display);
        Assert.Equal(source.Drives[1].Path, first.Drives[1].Path); Assert.True(first.Drives[1].ReadOnly);
        foreach (var clone in new[] { first, second })
        {
            using var info = JsonDocument.Parse(await ProcessRunner.RunAsync(imageTool, ["info", "--output=json", clone.Drives[0].Path]));
            Assert.False(info.RootElement.TryGetProperty("backing-filename", out _));
            await ProcessRunner.RunAsync(imageTool, ["compare", "-f", "qcow2", "-F", "qcow2", sourceDisk, clone.Drives[0].Path]);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(Path.Combine(AppPaths.VmDirectory(prefs, clone.Id), "nvram.fd")));
        }
        await ProcessRunner.RunAsync(ioTool, ["-f", "qcow2", "-c", "write -P 0xb7 0 4096", first.Drives[0].Path]);
        Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(sourceDisk)));
        await ProcessRunner.RunAsync(ioTool, ["-f", "qcow2", "-c", "read -P 0x5a 0 4096", second.Drives[0].Path]);
        var beforeFailure = Directory.GetDirectories(root).Order().ToArray(); var broken = source.Clone(); broken.Drives[0].Path = Path.Combine(root, "missing.qcow2");
        await Assert.ThrowsAsync<FileNotFoundException>(() => manager.CloneBlueprintAsync(broken, prefs, "Failure"));
        Assert.Equal(beforeFailure, Directory.GetDirectories(root).Order().ToArray());
    }
}
