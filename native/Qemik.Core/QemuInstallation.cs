using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Qemik.Core;

public sealed record InstallationInfo(string Directory, string Version, string[] Architectures, string Accelerators);
public sealed record InstallerRelease(string FileName, Uri Url, string Sha512, long? Size);
public sealed record TransferProgress(long Downloaded, long? Total, string Stage);

public sealed class QemuInstallation : IDisposable
{
    public const string SourceUrl = "https://qemu.weilnetz.de/w64/";
    private readonly HttpClient http;
    public QemuInstallation(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Qemik/0.1.0");
    }
    public static IEnumerable<string> CandidateDirectories(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "qemu");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "qemu");
        if (OperatingSystem.IsWindows())
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\QEMU");
                if (key?.GetValue("InstallLocation") is string path && !string.IsNullOrWhiteSpace(path)) yield return path;
                using var qemu = root.OpenSubKey(@"SOFTWARE\QEMU");
                if (qemu?.GetValue("Install_Dir") is string other && !string.IsNullOrWhiteSpace(other)) yield return other;
            }
        }
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(path)) yield return path.Trim('"');
    }
    public static async Task<InstallationInfo?> DetectAsync(string configured)
    {
        foreach (var dir in CandidateDirectories(configured).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var exe = Path.Combine(dir, "qemu-system-x86_64.exe");
            if (!File.Exists(exe)) continue;
            var version = await ProcessRunner.RunAsync(exe, ["--version"]);
            var accelerators = await ProcessRunner.RunAsync(exe, ["-accel", "help"]);
            return new(dir, version, Directory.GetFiles(dir, "qemu-system-*.exe").Select(Path.GetFileNameWithoutExtension).Select(s => s![12..]).Order().ToArray(), accelerators);
        }
        return null;
    }
    public static string SelectLatestFile(string html)
    {
        var files = Regex.Matches(html, "href=\"(qemu-w64-setup-[0-9]{8}\\.exe)\"", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value).OrderDescending(StringComparer.Ordinal).ToArray();
        return files.FirstOrDefault() ?? throw new InvalidDataException("The publisher did not list a Windows x64 installer.");
    }
    public static string ReadChecksum(string text, string fileName)
    {
        var match = Regex.Match(text.Trim(), @"^([a-fA-F0-9]{128})\s+\*?(.+)$");
        if (!match.Success || match.Groups[2].Value.Trim() != fileName) throw new InvalidDataException("The publisher checksum does not match the selected installer.");
        return match.Groups[1].Value.ToUpperInvariant();
    }
    public async Task<InstallerRelease> ResolveAsync(CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(45)); ct = deadline.Token;
        var file = SelectLatestFile(await http.GetStringAsync(SourceUrl, ct));
        var checksum = ReadChecksum(await http.GetStringAsync(SourceUrl + Path.ChangeExtension(file, ".sha512"), ct), file);
        using var request = new HttpRequestMessage(HttpMethod.Head, SourceUrl + file);
        using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        return new(file, new Uri(SourceUrl + file), checksum, response.Content.Headers.ContentLength);
    }
    public async Task<string> DownloadAsync(InstallerRelease release, string cache, IProgress<TransferProgress> progress, CancellationToken ct)
    {
        if (release.Url.AbsoluteUri != SourceUrl + release.FileName || !Regex.IsMatch(release.FileName, "^qemu-w64-setup-[0-9]{8}\\.exe$")) throw new InvalidDataException("Untrusted installer URL.");
        Directory.CreateDirectory(cache);
        var final = Path.Combine(cache, release.FileName); var partial = final + ".partial";
        try
        {
            using var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.Size;
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            {
                var buffer = new byte[65536]; long downloaded = 0; int count;
                while ((count = await source.ReadAsync(buffer, ct)) > 0)
                { await output.WriteAsync(buffer.AsMemory(0, count), ct); downloaded += count; progress.Report(new(downloaded, total, "Downloading")); }
                if (total.HasValue && downloaded != total.Value) throw new InvalidDataException("Installer download is incomplete.");
            }
            progress.Report(new(new FileInfo(partial).Length, total, "Verifying SHA-512"));
            await VerifyAsync(partial, release.Sha512, ct);
            File.Move(partial, final, true);
            return final;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
    public static async Task VerifyAsync(string path, string expected, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA512.HashDataAsync(stream, ct));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-512 verification failed. The installer will not run.");
    }
    public static async Task<int> InstallAsync(string path, string expectedHash)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64) throw new PlatformNotSupportedException("The managed installer supports Windows x64. Select an existing QEMU installation on other hosts.");
        await VerifyAsync(path, expectedHash);
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }) ?? throw new InvalidOperationException("Could not open QEMU Setup.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
    public void Dispose() => http.Dispose();
}
