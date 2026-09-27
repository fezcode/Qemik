using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Qemik.Core;

public sealed partial class OsImages
{
    public IReadOnlyList<string> RefreshWarnings { get; private set; } = [];
    public async Task<List<OsImage>> RefreshAsync(CancellationToken ct = default)
    {
        // Independent publishers must not make one outage hide every other distribution.
        var providers = new (string Name, Func<CancellationToken, Task<List<OsImage>>> Fetch)[]
        {
            ("Ubuntu", RefreshUbuntuAsync), ("Fedora", RefreshFedoraAsync), ("Linux Mint", RefreshMintAsync),
            ("openSUSE", RefreshSuseAsync), ("Debian", RefreshDebianAsync)
        };
        var results = await Task.WhenAll(providers.Select(async provider =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(75));
            try { return (Images: await provider.Fetch(timeout.Token), Error: (string?)null); }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException or FormatException)
            { return (Images: new List<OsImage>(), Error: provider.Name + ": " + ex.Message); }
        }));
        ct.ThrowIfCancellationRequested();
        RefreshWarnings = results.Where(r => r.Error is not null).Select(r => r.Error!).ToArray();
        var images = results.SelectMany(r => r.Images).ToList();
        if (images.Count == 0) throw new IOException("Release checks failed. " + string.Join("; ", RefreshWarnings));
        return images;
    }
    public static List<OsImage> ParseFedora(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<OsImage>();
        foreach (var edition in new[] { "Workstation", "KDE" })
        {
            var candidates = document.RootElement.EnumerateArray().Where(e =>
                e.GetProperty("arch").GetString() == "x86_64" && e.GetProperty("subvariant").GetString() == edition &&
                int.TryParse(e.GetProperty("version").GetString(), out _) && e.GetProperty("link").GetString()!.EndsWith(".iso", StringComparison.Ordinal))
                .OrderByDescending(e => int.Parse(e.GetProperty("version").GetString()!, CultureInfo.InvariantCulture)).ToArray();
            if (candidates.Length == 0) throw new InvalidDataException("No stable Fedora " + edition + " ISO is listed.");
            var entry = candidates[0]; var url = new Uri(entry.GetProperty("link").GetString()!);
            var image = new OsImage("fedora-" + edition.ToLowerInvariant(), "Fedora " + edition, entry.GetProperty("version").GetString()!, "x86_64", Path.GetFileName(url.AbsolutePath), url,
                entry.GetProperty("sha256").GetString()!, long.Parse(entry.GetProperty("size").ToString(), CultureInfo.InvariantCulture), new Uri("https://fedoraproject.org/releases.json"));
            Validate(image); result.Add(image);
        }
        return result;
    }
    private async Task<List<OsImage>> RefreshFedoraAsync(CancellationToken ct) => ParseFedora(await GetTextAsync(new Uri("https://fedoraproject.org/releases.json"), ct));

    public static string MintVersion(string html)
    {
        var match = Regex.Match(html, @"<title>Download Linux Mint (\d+(?:\.\d+)?) - Linux Mint</title>", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : throw new InvalidDataException("No stable Linux Mint download version is listed.");
    }
    private async Task<List<OsImage>> RefreshMintAsync(CancellationToken ct)
    {
        var version = MintVersion(await GetTextAsync(new Uri("https://linuxmint.com/download.php"), ct));
        var checksumUrl = new Uri($"https://mirrors.kernel.org/linuxmint/stable/{version}/sha256sum.txt");
        var sums = await GetTextAsync(checksumUrl, ct); var result = new List<OsImage>();
        foreach (var edition in new[] { "cinnamon", "mate", "xfce" })
        {
            var file = $"linuxmint-{version}-{edition}-64bit.iso";
            var url = new Uri($"https://pub.linuxmint.io/stable/{version}/{file}");
            var title = edition == "mate" ? "MATE" : edition == "xfce" ? "Xfce" : "Cinnamon";
            var image = new OsImage("mint-" + edition, "Linux Mint " + title, version, "x86_64", file, url, ChecksumFor(sums, file), await GetSizeAsync(url, ct), checksumUrl);
            Validate(image); result.Add(image);
        }
        return result;
    }
    public static string ChecksumFor(string text, string file)
    {
        var matches = Regex.Matches(text, @"(?m)^([a-fA-F0-9]{64})[ \t]+\*?([^\r\n]+)\r?$")
            .Where(m => m.Groups[2].Value == file).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("No unique SHA-256 checksum for " + file);
        return matches[0].Groups[1].Value.ToUpperInvariant();
    }
    public static (string File, string Version) SuseSnapshot(string sums, string edition)
    {
        if (edition is not ("DVD" or "NET")) throw new ArgumentException("Unknown openSUSE edition.");
        var match = Regex.Match(sums, @"(?m)^[a-fA-F0-9]{64}[ \t]+\*?(openSUSE-Tumbleweed-" + edition + @"-x86_64-Snapshot(\d{8})-Media\.iso)\r?$");
        return match.Success ? (match.Groups[1].Value, match.Groups[2].Value) : throw new InvalidDataException("No versioned openSUSE snapshot checksum is listed.");
    }
    private async Task<List<OsImage>> RefreshSuseAsync(CancellationToken ct)
    {
        var result = new List<OsImage>();
        foreach (var edition in new[] { "DVD", "NET" })
        {
            var sums = await GetTextAsync(new Uri($"https://download.opensuse.org/tumbleweed/iso/openSUSE-Tumbleweed-{edition}-x86_64-Current.iso.sha256"), ct);
            var (file, version) = SuseSnapshot(sums, edition);
            // Pin a snapshot, not the mutable Current alias, so paused downloads stay consistent.
            var url = new Uri("https://download.opensuse.org/tumbleweed/iso/" + file);
            var image = new OsImage("opensuse-" + edition.ToLowerInvariant(), "openSUSE Tumbleweed " + (edition == "DVD" ? "DVD" : "Network"), version, "x86_64", file, url, ChecksumFor(sums, file), await GetSizeAsync(url, ct), new Uri(url.AbsoluteUri + ".sha256"));
            Validate(image); result.Add(image);
        }
        return result;
    }
    private async Task<List<OsImage>> RefreshDebianAsync(CancellationToken ct)
    {
        var current = new Uri("https://cdimage.debian.org/debian-cd/current/amd64/iso-cd/SHA256SUMS");
        var sums = await GetTextAsync(current, ct);
        var match = Regex.Match(sums, @"(?m)^[a-fA-F0-9]{64}[ \t]+\*?(debian-(\d+\.\d+\.\d+)-amd64-netinst\.iso)\r?$");
        if (!match.Success) throw new InvalidDataException("No current Debian net installer is listed.");
        var file = match.Groups[1].Value; var version = match.Groups[2].Value;
        var url = new Uri($"https://cdimage.debian.org/debian-cd/{version}/amd64/iso-cd/{file}");
        var image = new OsImage("debian-netinst", "Debian", version, "x86_64", file, url, ChecksumFor(sums, file), await GetSizeAsync(url, ct), new Uri(url, "SHA256SUMS"));
        Validate(image); return [image];
    }
    private static bool TrustedImage(OsImage image)
    {
        if (image.Url is null || image.ChecksumUrl is null || !image.Url.IsAbsoluteUri || !image.ChecksumUrl.IsAbsoluteUri || image.Url.Scheme != "https" || image.ChecksumUrl.Scheme != "https") return false;
        var url = image.Url.AbsoluteUri; var sums = image.ChecksumUrl.AbsoluteUri; var version = image.Version; var file = image.FileName;
        return image.Id switch
        {
            "ubuntu-desktop-lts" or "ubuntu-server-lts" => Regex.IsMatch(version, @"^\d{2}\.\d{2}(?:\.\d+)?$") && file == $"ubuntu-{version}-{(image.Id == "ubuntu-desktop-lts" ? "desktop" : "live-server")}-amd64.iso" && url == $"{Publisher}{version}/{file}" && sums == $"{Publisher}{version}/SHA256SUMS",
            "fedora-workstation" or "fedora-kde" => Regex.IsMatch(version, @"^\d+$") && Regex.IsMatch(file, @"^Fedora-[A-Za-z-]+-" + Regex.Escape(version) + @"-[\d.]+\.x86_64\.iso$") && url == $"https://download.fedoraproject.org/pub/fedora/linux/releases/{version}/{(image.Id == "fedora-workstation" ? "Workstation" : "KDE")}/x86_64/iso/{file}" && sums == "https://fedoraproject.org/releases.json",
            "mint-cinnamon" or "mint-mate" or "mint-xfce" => Regex.IsMatch(version, @"^\d+(?:\.\d+)?$") && file == $"linuxmint-{version}-{image.Id[5..]}-64bit.iso" && url == $"https://pub.linuxmint.io/stable/{version}/{file}" && sums == $"https://mirrors.kernel.org/linuxmint/stable/{version}/sha256sum.txt",
            "opensuse-dvd" or "opensuse-net" => Regex.IsMatch(version, @"^\d{8}$") && file == $"openSUSE-Tumbleweed-{image.Id[9..].ToUpperInvariant()}-x86_64-Snapshot{version}-Media.iso" && url == $"https://download.opensuse.org/tumbleweed/iso/{file}" && sums == url + ".sha256",
            "debian-netinst" => Regex.IsMatch(version, @"^\d+\.\d+\.\d+$") && file == $"debian-{version}-amd64-netinst.iso" && url == $"https://cdimage.debian.org/debian-cd/{version}/amd64/iso-cd/{file}" && sums == $"https://cdimage.debian.org/debian-cd/{version}/amd64/iso-cd/SHA256SUMS",
            _ => false
        };
    }
    private async Task<string> GetTextAsync(Uri uri, CancellationToken ct)
    {
        using var response = await SendAsync(uri, HttpMethod.Get, null, ct); response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
    private async Task<long> GetSizeAsync(Uri uri, CancellationToken ct)
    {
        using var response = await SendAsync(uri, HttpMethod.Head, null, ct); response.EnsureSuccessStatusCode();
        return response.Content.Headers.ContentLength is > 0 and var size ? size : throw new InvalidDataException("The publisher did not provide the ISO size.");
    }
    private async Task<HttpResponseMessage> SendAsync(Uri uri, HttpMethod method, long? rangeStart, CancellationToken ct)
    {
        for (var redirect = 0; redirect < 8; redirect++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.IsLoopback || IPAddress.TryParse(uri.Host, out _))
                throw new InvalidDataException("The publisher returned an invalid HTTPS mirror URL.");
            using var request = new HttpRequestMessage(method, uri);
            if (rangeStart.HasValue) request.Headers.Range = new RangeHeaderValue(rangeStart, null);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location; response.Dispose();
            if (location is null) throw new InvalidDataException("The publisher returned an empty redirect.");
            uri = new Uri(uri, location);
            // Some publisher mirror selectors advertise HTTP; require that mirror's HTTPS endpoint.
            if (uri.Scheme == "http") uri = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
        }
        throw new HttpRequestException("Too many publisher download redirects.");
    }
}
