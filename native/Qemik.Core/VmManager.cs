using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Qemik.Core;

public sealed class VmSession : IDisposable
{
    public required Process Process { get; init; }
    public QmpClient? Qmp { get; set; }
    public string State { get; set; } = "Starting";
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    public string? Error { get; set; }
    public int? GuestPort { get; init; }
    public int? ClipboardPort { get; init; }
    public bool ClipboardChannel { get; init; }
    internal VmConfig? StartedConfiguration { get; init; }
    public string? DisplayBackend => StartedConfiguration?.Display;
    public string LaunchCommand { get; init; } = "";
    public bool Active => !Process.HasExited;
    public void Dispose() { Qmp?.Dispose(); Process.Dispose(); }
}

public sealed partial class VmManager : IDisposable
{
    private readonly ConcurrentDictionary<string, VmSession> sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();
    // Never disposed: Windows closes it when Qemik's process ends, which stops every guest.
    private static readonly ChildProcessJob Guests = new();
    public event Action? Changed;
    public VmSession? Session(string id) => sessions.GetValueOrDefault(id);
    public bool IsRunning(string id) => Session(id)?.Active == true;
    public bool HasRunning => sessions.Values.Any(s => s.Active);
    public string State(string id) => Session(id) is { } s ? (s.Active ? s.State : s.Process.ExitCode == 0 ? "Stopped" : "Failed") : "Stopped";

    public async Task StartAsync(VmConfig vm, Preferences prefs)
    {
        var gate = gates.GetOrAdd(vm.Id, _ => new SemaphoreSlim(1));
        await gate.WaitAsync();
        try
        {
            if (IsRunning(vm.Id)) throw new InvalidOperationException("This virtual machine is already running.");
            if (vm.IsBlueprint) throw new InvalidOperationException("Create a virtual machine from this blueprint before starting it.");
            QemuCommand.Validate(vm);
            var exe = Path.Combine(prefs.QemuDirectory, $"qemu-system-{vm.Architecture}.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("Install QEMU or choose its installation folder in QEMU Engine.", exe);
            foreach (var drive in vm.Drives) if (!File.Exists(drive.Path)) throw new FileNotFoundException("A drive image is missing.", drive.Path);
            foreach (var path in new[] { vm.Kernel, vm.Initrd }) if (path.Length > 0 && !File.Exists(path)) throw new FileNotFoundException("Boot file is missing.", path);
            var dir = AppPaths.VmDirectory(prefs, vm.Id); Directory.CreateDirectory(dir);
            if (vm.Uefi)
            {
                if (!File.Exists(vm.FirmwareCode)) throw new FileNotFoundException("Select a UEFI firmware code file in Boot settings.", vm.FirmwareCode);
                if (vm.FirmwareVarsTemplate.Length > 0 && !File.Exists(Path.Combine(dir, "nvram.fd")))
                    File.Copy(vm.FirmwareVarsTemplate, Path.Combine(dir, "nvram.fd"), false);
            }
            if (vm.SharedFolder.Length > 0)
            {
                if (!Directory.Exists(vm.SharedFolder)) throw new DirectoryNotFoundException("The shared folder does not exist.");
                var devices = await ProcessRunner.RunAsync(exe, ["-device", "help"]);
                if (!devices.Contains("virtio-9p-", StringComparison.Ordinal)) throw new NotSupportedException("This QEMU build has no VirtFS/9p device. Clear the VirtFS folder and use Shared folders instead.");
            }
            if (vm.Accelerator == "whpx")
            {
                var accel = await ProcessRunner.RunAsync(exe, ["-accel", "help"]);
                if (!accel.Contains("whpx", StringComparison.Ordinal)) throw new NotSupportedException("This QEMU build has no WHPX support. Select TCG.");
            }
            // The OS chooses a free loopback port. QEMU bind failures are surfaced through its log.
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            int? guestPort = null;
            if (vm.Display == "qemik")
            {
                var displayListener = new TcpListener(IPAddress.Loopback, 0); displayListener.Start();
                guestPort = ((IPEndPoint)displayListener.LocalEndpoint).Port; displayListener.Stop();
            }
            int? clipboardPort = null;
            if (vm.SharedClipboard && vm.Display == "sdl")
            {
                var clipboardListener = new TcpListener(IPAddress.Loopback, 0); clipboardListener.Start();
                clipboardPort = ((IPEndPoint)clipboardListener.LocalEndpoint).Port; clipboardListener.Stop();
            }
            var command = QemuCommand.Build(vm, prefs, port, guestPort, clipboardPort);
            await File.WriteAllTextAsync(Path.Combine(dir, "launch.ps1"), command.Preview);
            var logPath = Path.Combine(dir, "qemu.log");
            await File.WriteAllTextAsync(logPath, $"Qemik launch — {DateTimeOffset.Now:O}\n{command.Preview}\n\n");
            var logGate = new object();
            var process = new Process { StartInfo = command.StartInfo(), EnableRaisingEvents = true };
            process.StartInfo.WorkingDirectory = dir;
            void Append(string line)
            {
                lock (logGate)
                {
                    try
                    {
                        if (new FileInfo(logPath).Length > 8 * 1024 * 1024) File.Move(logPath, logPath + ".previous", true);
                        File.AppendAllText(logPath, line + Environment.NewLine);
                    }
                    catch (IOException) { }
                }
            }
            void Log(object sender, DataReceivedEventArgs e) { if (e.Data is not null) Append(e.Data); }
            process.OutputDataReceived += Log; process.ErrorDataReceived += Log;
            process.Start();
            try { Guests.Add(process); }
            catch (System.ComponentModel.Win32Exception ex) { Append($"Qemik: {ex.Message} This guest may keep running if Qemik ends unexpectedly."); }
            var session = new VmSession { Process = process, GuestPort = guestPort, ClipboardPort = clipboardPort, ClipboardChannel = vm.SharedClipboard, StartedConfiguration = vm.Clone(), LaunchCommand = command.Preview };
            if (sessions.TryRemove(vm.Id, out var previous)) previous.Dispose();
            sessions[vm.Id] = session;
            process.Exited += (_, _) => { session.State = "Stopped"; Changed?.Invoke(); };
            process.BeginOutputReadLine(); process.BeginErrorReadLine(); Changed?.Invoke();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (!timeout.IsCancellationRequested)
                {
                    if (process.HasExited)
                    {
                        await process.WaitForExitAsync();
                        throw new InvalidOperationException($"QEMU exited with code {process.ExitCode}. Open Logs for details.\n" + await ReadLogAsync(vm, prefs));
                    }
                    try { session.Qmp = await QmpClient.ConnectAsync(port, timeout.Token); break; }
                    catch (SocketException) { await Task.Delay(150, timeout.Token); }
                }
                if (session.Qmp is null) throw new TimeoutException("QEMU did not open its control channel.");
                session.State = "Running"; Changed?.Invoke();
            }
            catch (Exception ex)
            {
                session.Error = ex.Message;
                if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
                session.State = "Failed"; Changed?.Invoke(); throw;
            }
        }
        finally { gate.Release(); }
    }
    public async Task ControlAsync(string id, string command)
    {
        if (!new[] { "stop", "cont", "system_powerdown", "system_reset", "quit" }.Contains(command)) throw new ArgumentException("Unsupported lifecycle command.");
        var session = Session(id) ?? throw new InvalidOperationException("This virtual machine is stopped.");
        if (session.Qmp is null || !session.Active) throw new InvalidOperationException("QEMU's control channel is unavailable.");
        await session.Qmp.ExecuteAsync(command);
        session.State = command switch { "stop" => "Paused", "cont" => "Running", "system_powerdown" => "Shutting down", _ => session.State };
        Changed?.Invoke();
    }
    public async Task ForceStopAsync(string id)
    {
        var s = Session(id); if (s?.Active != true) return;
        s.Process.Kill(true); await s.Process.WaitForExitAsync(); Changed?.Invoke();
    }
    public static async Task<string> ReadLogAsync(VmConfig vm, Preferences prefs)
    {
        var path = Path.Combine(AppPaths.VmDirectory(prefs, vm.Id), "qemu.log");
        if (!File.Exists(path)) return "No output yet. Start this virtual machine to create a log.";
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > 65536) file.Seek(-65536, SeekOrigin.End);
        using var reader = new StreamReader(file); return await reader.ReadToEndAsync();
    }
    public async Task<string> SnapshotAsync(VmConfig vm, Preferences prefs, DriveConfig drive, string action, string name = "")
    {
        var gate = gates.GetOrAdd(vm.Id, _ => new SemaphoreSlim(1)); await gate.WaitAsync();
        try
        {
            if (IsRunning(vm.Id)) throw new InvalidOperationException("Stop the VM before managing disk snapshots.");
            if (drive.CdRom || drive.ReadOnly || drive.Format != "qcow2") throw new InvalidOperationException("Snapshots require a writable QCOW2 disk.");
            if (!new[] { "-l", "-c", "-a", "-d" }.Contains(action)) throw new ArgumentException("Invalid snapshot action.");
            if (action != "-l" && (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.StartsWith('-'))) throw new InvalidDataException("Enter a snapshot name (1–80 characters, not starting with a hyphen).");
            return await ProcessRunner.RunAsync(Path.Combine(prefs.QemuDirectory, "qemu-img.exe"), action == "-l" ? ["snapshot", "-l", drive.Path] : ["snapshot", action, name, drive.Path], timeoutSeconds: 120);
        }
        finally { gate.Release(); }
    }
    public static async Task CreateDiskAsync(Preferences prefs, string path, int gib, string format)
    {
        if (gib is < 1 or > 65536 || format is not ("qcow2" or "raw")) throw new InvalidDataException("Choose a QCOW2/raw disk of 1–65536 GiB.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // Reserve the destination first: qemu-img create would otherwise overwrite an existing disk.
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        try { await ProcessRunner.RunAsync(Path.Combine(prefs.QemuDirectory, "qemu-img.exe"), ["create", "-f", format, path, $"{gib}G"], timeoutSeconds: 120); }
        catch { if (File.Exists(path) && new FileInfo(path).Length == 0) File.Delete(path); throw; }
    }
    public void Dispose() { foreach (var s in sessions.Values) s.Dispose(); }
}
