using Qemik.Core;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Qemik.Tests;
public sealed class ImageTests
{
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("a small fixture standing in for the ISO bytes");
    public static OsImage Fixture() => new("ubuntu-desktop-lts", "Ubuntu Desktop", "26.04.1", "x86_64", "ubuntu-26.04.1-desktop-amd64.iso", new Uri("https://releases.ubuntu.com/26.04.1/ubuntu-26.04.1-desktop-amd64.iso"), Convert.ToHexString(SHA256.HashData(Bytes)), Bytes.Length, new Uri("https://releases.ubuntu.com/26.04.1/SHA256SUMS"));
    [Fact]
    public void CatalogChoosesNewestStableLtsAndExactChecksum()
    {
        Assert.Equal("26.04.1", OsImages.LatestLts("Ubuntu 24.04.5 LTS Ubuntu 26.04.1 LTS Ubuntu 26.10 Beta Ubuntu 22.04.5 LTS"));
        var image = Fixture();
        Assert.Equal(image.FileName, OsImages.FindImage($"{image.Sha256} *{image.FileName}\n", image.Version, "desktop").FileName);
        Assert.Throws<InvalidDataException>(() => OsImages.FindImage($"{image.Sha256} *other.iso\n", image.Version, "desktop"));
        Assert.Throws<InvalidDataException>(() => OsImages.Validate(image with { Url = new Uri("https://example.com/evil.iso") }));
        Assert.Throws<InvalidDataException>(() => OsImages.ImagePath("C:\\cache", image with { FileName = "../escape.iso" }));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResumesOrRestartsWhenServerIgnoresRange(bool acceptsRange)
    {
        var image = Fixture(); var root = CoreTests.TestDirectory(); var final = OsImages.ImagePath(root, image); Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        await File.WriteAllBytesAsync(final + ".partial", Bytes[..7]);
        using var service = new OsImages(new Handler(request =>
        {
            Assert.Equal(7, request.Headers.Range!.Ranges.Single().From);
            var response = new HttpResponseMessage(acceptsRange ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(acceptsRange ? Bytes[7..] : Bytes) };
            if (acceptsRange) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(7, Bytes.Length - 1, Bytes.Length);
            return response;
        }));
        var path = await service.DownloadAsync(image, root, new Progress<ImageProgress>());
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(path)); Assert.False(File.Exists(path + ".partial"));
    }
    [Fact]
    public async Task CorruptDownloadIsNeverPromotedOrAttached()
    {
        var image = Fixture(); var root = CoreTests.TestDirectory();
        using var service = new OsImages(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[Bytes.Length]) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(image, root, new Progress<ImageProgress>()));
        Assert.False(File.Exists(OsImages.ImagePath(root, image))); Assert.False(File.Exists(OsImages.ImagePath(root, image) + ".partial"));
    }
    [Fact]
    public async Task CachedImagesAreReverifiedAndInstallerBecomesFirstOpticalDrive()
    {
        var image = Fixture(); var root = CoreTests.TestDirectory(); var path = OsImages.ImagePath(root, image); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, Bytes);
        using var service = new OsImages(new Handler(_ => throw new InvalidOperationException("Cached download should not fetch again.")));
        Assert.Equal(path, await service.DownloadAsync(image, root, new Progress<ImageProgress>()));
        var vm = new VmConfig { Drives = [new DriveConfig { Path = "system.qcow2" }, new DriveConfig { Path = "old.iso", CdRom = true, Interface = "ide", Format = "raw" }] };
        InstallMedia.Attach(vm, image, path); InstallMedia.Attach(vm, image, path);
        Assert.Equal(3, vm.Drives.Count); Assert.Equal(path, vm.Drives[0].Path); Assert.True(vm.Drives[0].ReadOnly); Assert.Equal("dc", vm.BootOrder);
        vm.Architecture = "aarch64"; Assert.Throws<InvalidOperationException>(() => InstallMedia.Attach(vm, image, path));
        await File.WriteAllBytesAsync(path, new byte[Bytes.Length]);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(image, root, new Progress<ImageProgress>()));
    }
    [Fact]
    public void DeletionKeepsSharedIsosFirmwareAndBackingFiles()
    {
        var root = CoreTests.TestDirectory(); var prefs = CoreTests.Prefs(root);
        string FileAt(string name) { var path = Path.Combine(root, name); File.WriteAllText(path, name); return path; }
        var disk = FileAt("system.qcow2"); var iso = FileAt("shared.iso"); var firmware = FileAt("firmware.fd"); var backing = FileAt("base.qcow2");
        var vm = new VmConfig { FirmwareCode = firmware, Drives = [new() { Path = disk }, new() { Path = iso, CdRom = true }, new() { Path = firmware }, new() { Path = backing }] };
        var other = new VmConfig { Drives = [new() { Path = iso, CdRom = true }] };
        Directory.CreateDirectory(AppPaths.VmDirectory(prefs, vm.Id)); File.WriteAllText(Path.Combine(AppPaths.VmDirectory(prefs, vm.Id), "qemu.log"), "log");
        var plan = VmRemoval.Review(vm, prefs, [vm, other], [backing]); Assert.Equal(2, plan.Files.Count); Assert.Equal(3, plan.Kept.Count);
        VmRemoval.DeleteReviewedFiles(plan, vm, prefs, [vm, other], [backing]);
        Assert.False(File.Exists(disk)); Assert.True(File.Exists(iso)); Assert.True(File.Exists(firmware)); Assert.True(File.Exists(backing)); Assert.False(Directory.Exists(plan.RuntimeDirectory));
    }
    [Fact]
    public void DeletionRechecksChangesAndNewSharedReferencesBeforeTouchingFiles()
    {
        var root = CoreTests.TestDirectory(); var prefs = CoreTests.Prefs(root); var disk = Path.Combine(root, "disk.qcow2"); File.WriteAllText(disk, "original");
        var vm = new VmConfig { Drives = [new() { Path = disk }] }; var plan = VmRemoval.Review(vm, prefs, [vm]);
        var other = new VmConfig { Drives = [new() { Path = disk }] };
        Assert.Throws<IOException>(() => VmRemoval.DeleteReviewedFiles(plan, vm, prefs, [vm, other])); Assert.True(File.Exists(disk));
        File.WriteAllText(disk, "changed contents"); Assert.Throws<IOException>(() => VmRemoval.DeleteReviewedFiles(plan, vm, prefs, [vm])); Assert.True(File.Exists(disk));
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(handler(request));
    }
}
