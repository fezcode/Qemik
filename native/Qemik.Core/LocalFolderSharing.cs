using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Qemik.Core;

// App-managed WebDAV, reachable only through the host loopback interface (10.0.2.2 in QEMU user networking).
public sealed partial class LocalFolderSharing(string root) : IHostFolderSharing, IAsyncDisposable
{
    public string Account => "No password required";
    public sealed record Folder(string VmId, string Name, string Path, bool ReadOnly, string Token);
    private sealed record Configuration(int Port, List<Folder> Folders);
    private readonly SemaphoreSlim gate = new(1);
    private readonly SemaphoreSlim files = new(1);
    private readonly List<Folder> folders = [];
    private WebApplication? server;
    private bool loaded;
    private int port;
    private readonly string toolsToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private string ConfigPath => System.IO.Path.Combine(root, "local-folders.json");
    public string GuestAddress(Folder folder) => $"dav://10.0.2.2:{port}/{folder.Token}/{Uri.EscapeDataString(folder.Name)}/";
    public async Task<string> DisplaySetupCommandAsync()
    {
        await gate.WaitAsync();
        try
        {
            await LoadAsync(); await StartServerAsync();
            var address = $"http://10.0.2.2:{port}/{toolsToken}/display-fit.py";
            return "python3 -c \"import urllib.request,tempfile,subprocess,os; f=tempfile.mkstemp(suffix='.py'); os.write(f[0],urllib.request.urlopen('" + address + "').read()); os.close(f[0]); subprocess.run(['python3',f[1],'--install'],check=1); os.unlink(f[1])\"";
        }
        finally { gate.Release(); }
    }
    public async Task StartAsync()
    {
        await gate.WaitAsync();
        try { await LoadAsync(); if (folders.Count > 0) await StartServerAsync(); }
        finally { gate.Release(); }
    }
    private async Task LoadAsync()
    {
        if (loaded) return;
        if (File.Exists(ConfigPath))
        {
            var config = JsonSerializer.Deserialize<Configuration>(await File.ReadAllTextAsync(ConfigPath)) ?? throw new IOException("Invalid saved shared folders.");
            port = config.Port;
            if (port is < 1024 or > 65535) throw new IOException("Invalid saved sharing port.");
            foreach (var folder in config.Folders)
            {
                WindowsHostFolderSharing.ShareName(folder.VmId, folder.Name);
                if (!System.Text.RegularExpressions.Regex.IsMatch(folder.Token, "^[0-9a-f]{64}$") || !System.IO.Path.IsPathFullyQualified(folder.Path)) throw new IOException("Invalid saved shared folder.");
            }
            folders.AddRange(config.Folders);
        }
        loaded = true;
    }
    private Task SaveAsync()
    {
        Directory.CreateDirectory(root);
        var temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Configuration(port, folders)));
        File.Move(temp, ConfigPath, true);
        return Task.CompletedTask;
    }
    private async Task StartServerAsync()
    {
        if (server is not null) return;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => { options.Listen(IPAddress.Loopback, port); options.Limits.MaxRequestBodySize = 64L * 1024 * 1024 * 1024; });
        var app = builder.Build(); app.Run(HandleAsync);
        try
        {
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            port = new Uri(address).Port; server = app;
        }
        catch { await app.DisposeAsync(); throw new IOException("The local sharing port is unavailable. Close any other Qemik using this library, then try again."); }
    }
    public async Task<IReadOnlyList<HostFolderShare>> ListAsync(string vmId)
    {
        await StartAsync(); await gate.WaitAsync();
        try { return folders.Where(f => f.VmId == vmId).Select(f => new HostFolderShare(f.Name, f.Path, f.ReadOnly ? "Read-only · No password" : "Read and write · No password", GuestAddress(f))).ToArray(); }
        finally { gate.Release(); }
    }
    public async Task CreateAsync(string vmId, string name, string path, bool readOnly)
    {
        WindowsHostFolderSharing.ShareName(vmId, name);
        var full = System.IO.Path.GetFullPath(path);
        if (!Directory.Exists(full) || full.StartsWith("\\\\", StringComparison.Ordinal)) throw new IOException("Choose an existing local Windows folder.");
        EnsureNoLinks(full);
        await gate.WaitAsync();
        try
        {
            await LoadAsync();
            if (folders.Any(f => f.VmId == vmId && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new IOException("Choose another share name; this one is already in use.");
            await StartServerAsync();
            var folder = new Folder(vmId, name, full, readOnly, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
            folders.Add(folder);
            try { await SaveAsync(); } catch { folders.Remove(folder); throw; }
        }
        finally { gate.Release(); }
    }
    public async Task RemoveAsync(string vmId, HostFolderShare share)
    {
        await files.WaitAsync(); await gate.WaitAsync();
        try
        {
            await LoadAsync();
            var folder = folders.SingleOrDefault(f => f.VmId == vmId && f.Name == share.Name && f.Path == share.Path) ?? throw new IOException("This share changed. Refresh and try again.");
            folders.Remove(folder);
            try { await SaveAsync(); } catch { folders.Add(folder); throw; }
        }
        finally { gate.Release(); files.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        var current = server; server = null;
        if (current is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await current.StopAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        finally { await current.DisposeAsync().ConfigureAwait(false); }
    }
}
