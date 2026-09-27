using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Qemik.Core;
using Xunit;

namespace Qemik.Tests;
public sealed class LinuxCatalogTests
{
    private static string FedoraJson() => JsonSerializer.Serialize(new[] { "45 Beta", "44", "43" }.SelectMany(version => new[] { "Workstation", "KDE" }.Select(edition => new
    {
        version, arch = "x86_64", subvariant = edition,
        link = $"https://download.fedoraproject.org/pub/fedora/linux/releases/{version}/{edition}/x86_64/iso/Fedora-{edition}-Live-{version}-1.7.x86_64.iso",
        sha256 = ImageTests.Fixture().Sha256, size = "44"
    })));
    [Fact]
    public void StableFedoraSelectionExcludesBetaAndOlderImages()
    {
        var images = OsImages.ParseFedora(FedoraJson()); Assert.Equal(2, images.Count);
        Assert.All(images, image => { Assert.Equal("44", image.Version); Assert.DoesNotContain("LTS", image.DisplayName); OsImages.Validate(image); });
        Assert.Throws<InvalidDataException>(() => OsImages.Validate(images[0] with { Url = new Uri("https://download.fedoraproject.org.evil.example/test.iso") }));
        Assert.Throws<InvalidDataException>(() => OsImages.Validate(images[0] with { ChecksumUrl = new Uri("https://example.com/checksum") }));
    }
    [Fact]
    public void MintAndSuseParsersPinStableVersionsAndExactChecksums()
    {
        Assert.Equal("22.3", OsImages.MintVersion("<title>Download Linux Mint 22.3 - Linux Mint</title> Linux Mint 23 Beta"));
        Assert.Throws<InvalidDataException>(() => OsImages.MintVersion("<title>Download Linux Mint 23 Beta - Linux Mint</title>"));
        var file = "openSUSE-Tumbleweed-DVD-x86_64-Snapshot20260923-Media.iso"; var sums = ImageTests.Fixture().Sha256 + "  " + file + "\n";
        Assert.Equal((file, "20260923"), OsImages.SuseSnapshot(sums, "DVD"));
        Assert.Equal(ImageTests.Fixture().Sha256, OsImages.ChecksumFor(sums, file));
        Assert.Throws<InvalidDataException>(() => OsImages.ChecksumFor(sums, "openSUSE-Tumbleweed-DVD-x86_64-Current.iso"));
        Assert.Throws<InvalidDataException>(() => OsImages.ChecksumFor(sums + sums, file));
    }
    [Fact]
    public async Task OnePublisherOutageDoesNotHideOtherDistributions()
    {
        using var service = new OsImages(new Handler(request => request.RequestUri!.AbsoluteUri == "https://fedoraproject.org/releases.json"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FedoraJson()) }
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var images = await service.RefreshAsync(); Assert.Equal(2, images.Count); Assert.All(images, i => Assert.StartsWith("fedora-", i.Id));
        Assert.Equal(4, service.RefreshWarnings.Count);
    }
    [Theory]
    [InlineData("https://mirror.example/download.iso")]
    [InlineData("http://mirror.example/download.iso")]
    public async Task PublisherMirrorRedirectsPreserveResumeAndVerifyBytes(string mirror)
    {
        var bytes = Encoding.UTF8.GetBytes("a small fixture standing in for the ISO bytes");
        var image = ImageTests.Fixture(); var root = CoreTests.TestDirectory(); var path = OsImages.ImagePath(root, image);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path + ".partial", bytes[..7]); var requests = 0;
        using var service = new OsImages(new Handler(request =>
        {
            requests++; Assert.Equal(7, request.Headers.Range!.Ranges.Single().From); Assert.Equal("https", request.RequestUri!.Scheme);
            if (request.RequestUri.Host == "releases.ubuntu.com") return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(mirror) } };
            Assert.Equal("mirror.example", request.RequestUri.Host);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[7..]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(7, bytes.Length - 1, bytes.Length); return response;
        }));
        Assert.Equal(path, await service.DownloadAsync(image, root, new Progress<ImageProgress>()));
        Assert.Equal(2, requests); Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }
    [Fact]
    public async Task UnsafeMirrorRedirectIsRejectedBeforeRequest()
    {
        var count = 0;
        using var service = new OsImages(new Handler(_ => { count++; return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://127.0.0.1/private") } }; }));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(ImageTests.Fixture(), CoreTests.TestDirectory(), new Progress<ImageProgress>())); Assert.Equal(1, count);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(handler(request));
    }
}
