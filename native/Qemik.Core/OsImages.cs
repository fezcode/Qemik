using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Qemik.Core;

public sealed record OsImage(string Id, string Name, string Version, string Architecture, string FileName, Uri Url, string Sha256, long Size, Uri ChecksumUrl)
{
    public string DisplayName => $"{Name} {Version}" + (Id.StartsWith("ubuntu-", StringComparison.Ordinal) ? " LTS" : "");
    public string PublisherName => Id.Split('-')[0] switch { "ubuntu" => "Canonical", "fedora" => "Fedora Project", "mint" => "Linux Mint", "opensuse" => "openSUSE", "debian" => "Debian", _ => "Publisher" };
    public string Description => Id switch { "ubuntu-server-lts" => "Server installer", "debian-netinst" or "opensuse-net" => "Network installer · internet required in guest", "opensuse-dvd" => "Offline installer · desktop or server", _ => "Desktop installer" };
    public string Website => Id.Split('-')[0] switch { "ubuntu" => "https://ubuntu.com/download", "fedora" => "https://fedoraproject.org/", "mint" => "https://linuxmint.com/download.php", "opensuse" => "https://get.opensuse.org/tumbleweed/", "debian" => "https://www.debian.org/distrib/", _ => Url.AbsoluteUri };
}
public sealed record ImageProgress(long Downloaded, long Total, string Stage);

public sealed partial class OsImages : IDisposable
{
    public const string Publisher = "https://releases.ubuntu.com/";
    private readonly HttpClient http;
    public OsImages(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Qemik/0.1.0");
    }
    public static string LatestLts(string html)
    {
        var versions = Regex.Matches(html, @"Ubuntu\s+(\d{2}\.\d{2}(?:\.\d+)?)\s+LTS", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value).Distinct().OrderByDescending(v => System.Version.Parse(v)).ToArray();
        return versions.FirstOrDefault() ?? throw new InvalidDataException("Canonical did not list a stable Ubuntu LTS release.");
    }
    public static (string FileName, string Hash) FindImage(string checksums, string version, string edition)
    {
        if (edition is not ("desktop" or "live-server")) throw new ArgumentException("Unknown Ubuntu edition.");
        var name = $"ubuntu-{version}-{edition}-amd64.iso";
        var entries = Regex.Matches(checksums, @"(?m)^([a-fA-F0-9]{64})[ \t]+\*?([^\r\n]+)\r?$");
        var match = entries.Cast<Match>().SingleOrDefault(m => m.Groups[2].Value == name);
        return match is null ? throw new InvalidDataException($"No published SHA-256 was found for {name}.") : (name, match.Groups[1].Value.ToUpperInvariant());
    }
    private async Task<List<OsImage>> RefreshUbuntuAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(60));
        ct = deadline.Token;
        var version = LatestLts(await GetTextAsync(new Uri(Publisher), ct));
        var checksumsUrl = new Uri($"{Publisher}{version}/SHA256SUMS");
        var checksums = await GetTextAsync(checksumsUrl, ct);
        var result = new List<OsImage>();
        foreach (var edition in new[] { "desktop", "live-server" })
        {
            var (file, hash) = FindImage(checksums, version, edition);
            var url = new Uri($"{Publisher}{version}/{file}");
            var size = await GetSizeAsync(url, ct);
            var image = new OsImage(edition == "desktop" ? "ubuntu-desktop-lts" : "ubuntu-server-lts", edition == "desktop" ? "Ubuntu Desktop" : "Ubuntu Server", version, "x86_64", file, url, hash, size, checksumsUrl);
            Validate(image); result.Add(image);
        }
        return result;
    }
    public static void Validate(OsImage image)
    {
        if (string.IsNullOrEmpty(image.FileName) || !Regex.IsMatch(image.FileName, @"^[A-Za-z0-9][A-Za-z0-9._-]+\.iso$") || !Regex.IsMatch(image.Sha256 ?? "", "^[0-9a-fA-F]{64}$") || image.Size <= 0 || image.Size > 32L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Invalid image metadata.");
        if (image.Architecture != "x86_64" || !TrustedImage(image))
            throw new InvalidDataException("The image is not from its expected publisher release directory.");
    }
    public static string ImagePath(string directory, OsImage image)
    {
        Validate(image); return Path.Combine(directory, image.Sha256[..16].ToLowerInvariant(), image.FileName);
    }
    public static async Task VerifyAsync(string path, OsImage image, CancellationToken ct = default)
    {
        Validate(image);
        if (new FileInfo(path).Length != image.Size) throw new InvalidDataException("The ISO size does not match the publisher metadata.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        if (!string.Equals(digest, image.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ISO SHA-256 verification failed. This image will not be attached.");
    }
    public async Task<string> DownloadAsync(OsImage image, string directory, IProgress<ImageProgress> progress, CancellationToken ct = default)
    {
        var final = ImagePath(directory, image); Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        // One writer across app/CLI instances, while allowing a paused download to be resumed.
        await using var operationLock = new FileStream(final + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        if (File.Exists(final))
        {
            progress.Report(new(image.Size, image.Size, "Verifying cached ISO")); await VerifyAsync(final, image, ct); return final;
        }
        var partial = final + ".partial";
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (existing > image.Size) { File.Delete(partial); existing = 0; }
        var disk = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(final))!);
        if (disk.AvailableFreeSpace < image.Size - existing + 256L * 1024 * 1024) throw new IOException("Not enough free space for this ISO. Choose another download folder or free some space.");
        if (existing < image.Size)
        {
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); headerTimeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var response = await SendAsync(image.Url, HttpMethod.Get, existing > 0 ? existing : null, headerTimeout.Token); response.EnsureSuccessStatusCode();
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.From != existing || range.Length != image.Size || range.To != image.Size - 1) throw new InvalidDataException("The download server returned an unexpected byte range.");
            }
            else if (response.StatusCode == HttpStatusCode.OK) existing = 0;
            else throw new InvalidDataException("Unexpected response while downloading the ISO.");
            if (response.Content.Headers.ContentLength is long length && length != image.Size - existing) throw new InvalidDataException("The download length differs from the published ISO size.");
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(partial, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1024 * 1024, true);
            var buffer = new byte[1024 * 1024]; var received = existing; var lastReport = Environment.TickCount64;
            progress.Report(new(received, image.Size, "Downloading"));
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct); idle.CancelAfter(TimeSpan.FromSeconds(90));
                var count = await source.ReadAsync(buffer, idle.Token); if (count == 0) break;
                received += count;
                if (received > image.Size) throw new InvalidDataException("The download exceeded the expected ISO size.");
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
                if (Environment.TickCount64 - lastReport > 200) { progress.Report(new(received, image.Size, "Downloading")); lastReport = Environment.TickCount64; }
            }
            if (received != image.Size) throw new IOException("Download interrupted. Retry to resume the partial ISO.");
        }
        progress.Report(new(image.Size, image.Size, "Verifying SHA-256"));
        try { await VerifyAsync(partial, image, ct); }
        catch (InvalidDataException) { File.Delete(partial); throw; }
        File.Move(partial, final);
        await File.WriteAllTextAsync(final + ".json", JsonSerializer.Serialize(image, new JsonSerializerOptions { WriteIndented = true }), ct);
        progress.Report(new(image.Size, image.Size, "Verified — ready to attach")); return final;
    }
    public void Dispose() => http.Dispose();
}
