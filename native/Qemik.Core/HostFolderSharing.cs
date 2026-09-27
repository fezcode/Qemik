using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Qemik.Core;

public sealed record HostFolderShare(string Name, string Path, string Access, string? Address = null);

public interface IHostFolderSharing
{
    string Account { get; }
    Task<IReadOnlyList<HostFolderShare>> ListAsync(string vmId);
    Task CreateAsync(string vmId, string name, string path, bool readOnly);
    Task RemoveAsync(string vmId, HostFolderShare share);
}

public sealed class WindowsHostFolderSharing : IHostFolderSharing
{
    public string Account => Environment.UserDomainName + "\\" + Environment.UserName;
    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
    private static string Marker(string vmId) { if (!Guid.TryParseExact(vmId, "N", out _)) throw new ArgumentException("Invalid machine ID."); return "Qemik shared folder " + vmId; }
    public static string ShareName(string vmId, string name)
    {
        Marker(vmId);
        if (!Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$")) throw new ArgumentException("Use 1–40 letters, numbers, underscores or hyphens for the share name.");
        return "Qemik_" + vmId[..8] + "_" + name;
    }
    public static string GuestAddress(string shareName) => "smb://10.0.2.2/" + Uri.EscapeDataString(shareName);
    public static string BuildCreateScript(string vmId, string name, string path, bool readOnly, string account)
    {
        var share = ShareName(vmId, name);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)) throw new ArgumentException("Choose a local Windows folder.");
        if (string.IsNullOrWhiteSpace(account)) throw new ArgumentException("Choose a Windows account.");
        return $$"""
            $ErrorActionPreference = 'Stop'
            $folder = Get-Item -LiteralPath {{Literal(path)}} -Force
            if (!$folder.PSIsContainer) { throw 'Select a folder.' }
            if (Get-SmbShare -Name {{Literal(share)}} -ErrorAction SilentlyContinue) { throw 'This share name already exists. Choose another name.' }
            New-SmbShare -Name {{Literal(share)}} -Path $folder.FullName -Description {{Literal(Marker(vmId))}} -{{(readOnly ? "ReadAccess" : "ChangeAccess")}} {{Literal(account)}} -EncryptData $true -FolderEnumerationMode AccessBased -CachingMode None -ErrorAction Stop | Out-Null
            """;
    }
    public static string BuildRemoveScript(string vmId, HostFolderShare share)
    {
        Marker(vmId);
        if (!share.Name.StartsWith("Qemik_" + vmId[..8] + "_", StringComparison.Ordinal) || !Regex.IsMatch(share.Name, "^[A-Za-z0-9_-]+$")) throw new ArgumentException("This share does not belong to this machine.");
        return $$"""
            $ErrorActionPreference = 'Stop'
            $share = Get-SmbShare -Name {{Literal(share.Name)}} -ErrorAction Stop
            if ($share.Description -cne {{Literal(Marker(vmId))}} -or $share.Path -ine {{Literal(share.Path)}}) { throw 'The share changed outside Qemik. Refresh before removing it.' }
            $share | Remove-SmbShare -Force -Confirm:$false -ErrorAction Stop
            """;
    }
    public async Task<IReadOnlyList<HostFolderShare>> ListAsync(string vmId)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
            $items = @(Get-SmbShare -ErrorAction Stop | Where-Object { $_.Description -ceq {{Literal(Marker(vmId))}} } | ForEach-Object {
                $rights = @(Get-SmbShareAccess -Name $_.Name -ErrorAction Stop | Where-Object { $_.AccessControlType -eq 'Allow' } | ForEach-Object { $_.AccountName + ': ' + $_.AccessRight })
                [pscustomobject]@{ Name = $_.Name; Path = $_.Path; Access = ($rights -join ', ') }
            })
            ConvertTo-Json -InputObject $items -Compress
            """;
        var json = await ProcessRunner.RunAsync(PowerShell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Encode(script)], standardOutputOnly: true);
        return JsonSerializer.Deserialize<HostFolderShare[]>(json) ?? [];
    }
    public Task CreateAsync(string vmId, string name, string path, bool readOnly)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Choose an existing Windows folder.");
        return RunElevatedAsync(BuildCreateScript(vmId, name, path, readOnly, Account));
    }
    public Task RemoveAsync(string vmId, HostFolderShare share) => RunElevatedAsync(BuildRemoveScript(vmId, share));
    private static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    private static async Task RunElevatedAsync(string script)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows SMB sharing requires Windows.");
        var resultPath = Path.Combine(Path.GetTempPath(), "Qemik-share-" + Guid.NewGuid().ToString("N") + ".json");
        using (new FileStream(resultPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        var wrapped = $$"""
            try {
            {{script}}
            [IO.File]::WriteAllText({{Literal(resultPath)}}, '{"Success":true}')
            exit 0
            } catch {
            [IO.File]::WriteAllText({{Literal(resultPath)}}, (@{ Success = $false; Error = $_.Exception.Message } | ConvertTo-Json -Compress))
            exit 1
            }
            """;
        try
        {
            var info = new ProcessStartInfo(PowerShell) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Encode(wrapped) }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new IOException("Windows could not open share setup.");
            await process.WaitForExitAsync();
            var text = await File.ReadAllTextAsync(resultPath);
            if (string.IsNullOrWhiteSpace(text)) throw new IOException("Windows share setup ended without a result. Refresh the share list before retrying.");
            using var result = JsonDocument.Parse(text);
            if (process.ExitCode != 0 || !result.RootElement.GetProperty("Success").GetBoolean()) throw new InvalidOperationException(result.RootElement.TryGetProperty("Error", out var error) ? error.GetString() : "Windows share setup failed.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new InvalidOperationException("Windows administrator prompt was canceled."); }
        finally { File.Delete(resultPath); }
    }
}
