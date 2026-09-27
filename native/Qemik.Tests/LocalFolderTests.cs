using System.Net;
using System.Text;
using System.Xml.Linq;
using Qemik.Core;
using Xunit;

namespace Qemik.Tests;

public sealed class LocalFolderTests
{
    [Fact]
    public void ResizeRetriesUnappliedRequestsAndRecoversAfterBootOrLogin()
    {
        var state = new GuestAutoResize();
        Assert.True(state.ShouldRequest((1440, 900), (640, 480), 0));
        Assert.False(state.ShouldRequest((1440, 900), (640, 480), 500));
        Assert.True(state.ShouldRequest((1440, 900), (640, 480), 3000));
        for (var time = 6000; time <= 15000; time += 3000) Assert.True(state.ShouldRequest((1440, 900), (640, 480), time));
        Assert.False(state.ShouldRequest((1440, 900), (640, 480), 18000)); Assert.True(state.NeedsSetup);
        Assert.True(state.ShouldRequest((1440, 900), (1920, 1080), 19000)); // Desktop has just started.
        Assert.False(state.ShouldRequest((1440, 900), (1440, 900), 19500)); Assert.False(state.NeedsSetup); Assert.Contains("applied", state.Status);
        Assert.True(state.ShouldRequest((3840, 2160), (1440, 900), 20000));
        state.Reset(); Assert.True(state.ShouldRequest((3840, 2160), (1440, 900), 20500));
    }
    [Fact]
    public async Task LocalWebDavSupportsPasswordFreeFilesAndPreservesPermissionsAndBoundaries()
    {
        var root = CoreTests.TestDirectory(); var folder = Path.Combine(root, "shared"); Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "Türkçe & notes.txt"); await File.WriteAllTextAsync(source, "original");
        var id = Guid.NewGuid().ToString("N"); var config = Path.Combine(root, "config"); string guestAddress;
        using var http = new HttpClient();
        await using (var shares = new LocalFolderSharing(config))
        {
            await shares.CreateAsync(id, "Files", folder, false);
            var share = Assert.Single(await shares.ListAsync(id)); guestAddress = share.Address!;
            var url = guestAddress.Replace("dav://10.0.2.2", "http://127.0.0.1");
            using var options = await http.SendAsync(new HttpRequestMessage(HttpMethod.Options, url)); Assert.Equal(HttpStatusCode.OK, options.StatusCode); Assert.Empty(options.Headers.WwwAuthenticate);
            using var list = new HttpRequestMessage(new HttpMethod("PROPFIND"), url) { Content = new StringContent("<propfind xmlns='DAV:'><prop><displayname/><getcontentlength/><resourcetype/><unknown/></prop></propfind>", Encoding.UTF8, "application/xml") }; list.Headers.Add("Depth", "1");
            using var listing = await http.SendAsync(list); Assert.Equal((HttpStatusCode)207, listing.StatusCode);
            var xml = XDocument.Parse(await listing.Content.ReadAsStringAsync()); Assert.Contains(xml.Descendants(XName.Get("displayname", "DAV:")), e => e.Value == "Türkçe & notes.txt"); Assert.Contains("404 Not Found", xml.ToString());
            Assert.Equal("original", await http.GetStringAsync(url + Uri.EscapeDataString("Türkçe & notes.txt")));
            using var put = await http.PutAsync(url + "new.txt", new StringContent("new")); Assert.Equal(HttpStatusCode.Created, put.StatusCode);
            using var mkcol = await http.SendAsync(new HttpRequestMessage(new HttpMethod("MKCOL"), url + "folder")); Assert.Equal(HttpStatusCode.Created, mkcol.StatusCode);
            using var move = new HttpRequestMessage(new HttpMethod("MOVE"), url + "new.txt"); move.Headers.Add("Destination", url + "folder/moved.txt");
            using var moved = await http.SendAsync(move); Assert.Equal(HttpStatusCode.Created, moved.StatusCode); Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(folder, "folder/moved.txt")));
            using var copy = new HttpRequestMessage(new HttpMethod("COPY"), url + "folder/"); copy.Headers.Add("Destination", url + "copied/");
            using var copied = await http.SendAsync(copy); Assert.Equal(HttpStatusCode.Created, copied.StatusCode); Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(folder, "copied/moved.txt")));
            using var rootDelete = await http.DeleteAsync(url); Assert.Equal(HttpStatusCode.Forbidden, rootDelete.StatusCode);
            using var traversal = await http.GetAsync(url + "..%5coutside.txt"); Assert.Equal(HttpStatusCode.Forbidden, traversal.StatusCode);
            using var ads = await http.PutAsync(url + "new.txt:secret", new StringContent("no")); Assert.Equal(HttpStatusCode.Forbidden, ads.StatusCode);
            using var badHost = new HttpRequestMessage(HttpMethod.Get, url + "folder/moved.txt"); badHost.Headers.Host = "untrusted.example";
            using var badHostResult = await http.SendAsync(badHost); Assert.Equal(HttpStatusCode.Forbidden, badHostResult.StatusCode);
            using var unknown = await http.GetAsync(new Uri(new Uri(url), "/unknown/Files/file")); Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            await shares.CreateAsync(id, "ReadOnly", folder, true); var readOnly = (await shares.ListAsync(id)).Single(s => s.Name == "ReadOnly"); var readUrl = readOnly.Address!.Replace("dav://10.0.2.2", "http://127.0.0.1");
            using var denied = await http.PutAsync(readUrl + "blocked.txt", new StringContent("no")); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.False(File.Exists(Path.Combine(folder, "blocked.txt")));
            foreach (var method in new[] { "DELETE", "MOVE", "COPY", "MKCOL" })
            {
                using var deniedRequest = new HttpRequestMessage(new HttpMethod(method), readUrl + "folder/moved.txt");
                using var deniedResult = await http.SendAsync(deniedRequest); Assert.Equal(HttpStatusCode.Forbidden, deniedResult.StatusCode);
            }
            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, url + "folder/moved.txt"); rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1, 2);
            using var rangeResult = await http.SendAsync(rangeRequest); Assert.Equal(HttpStatusCode.PartialContent, rangeResult.StatusCode); Assert.Equal("ew", await rangeResult.Content.ReadAsStringAsync());
            using var condition = new HttpRequestMessage(HttpMethod.Put, url + "folder/moved.txt") { Content = new StringContent("must not overwrite") }; condition.Headers.TryAddWithoutValidation("If-None-Match", "*");
            using var conditionResult = await http.SendAsync(condition); Assert.Equal(HttpStatusCode.PreconditionFailed, conditionResult.StatusCode);
            await shares.RemoveAsync(id, readOnly);
            using var revoked = await http.GetAsync(readUrl + "folder/moved.txt"); Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode); Assert.True(File.Exists(source));
            using var deleted = await http.DeleteAsync(url + "copied/"); Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode); Assert.False(Directory.Exists(Path.Combine(folder, "copied")));
        }
        await using (var reopened = new LocalFolderSharing(config)) Assert.Equal(guestAddress, Assert.Single(await reopened.ListAsync(id)).Address);
    }
}
