using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Qemik.Core;
using static Qemik.Desktop.Ui;

namespace Qemik.Desktop;

public sealed partial class SettingsWindow : Window
{
    private readonly VmConfig vm;
    private readonly Preferences prefs;
    private readonly ContentControl body = new();
    private readonly StackPanel navigation = new() { Spacing = 4 };
    private readonly TextBlock message = Muted("Changes apply the next time this machine starts.");
    private string section = "General";
    private bool diskBusy;
    public bool IsReadOnly { get; }
    private readonly string? launchCommand;
    public static readonly string[] Sections = ["General", "System", "Boot", "Drives", "Display", "Network", "Sound", "USB & input", "Sharing", "Serial", "Advanced"];
    public SettingsWindow(VmConfig vm, Preferences prefs, VmManager manager, bool readOnly = false, string? launchCommand = null)
    {
        IsReadOnly = readOnly || manager.IsRunning(vm.Id); this.vm = IsReadOnly ? vm.Clone() : vm; this.prefs = prefs; this.launchCommand = launchCommand;
        Title = (IsReadOnly ? "Current settings (read-only) — " : "Settings — ") + vm.Name; Width = 1020; Height = 800; MinWidth = 900; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var sidebar = Stack(Row(new Brand { Width = 30, Height = 30 }, Heading("Machine settings", 14)), navigation); sidebar.Spacing = 28;
        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 20 };
        content.Children.Add(Stack(Heading(vm.Name, 25), Muted(IsReadOnly ? "Running configuration · Read-only" : "Every machine has its own personality.")));
        Grid.SetRow(body, 1); content.Children.Add(body);
        var save = Button("Save settings", () =>
        {
            try
            {
                if (diskBusy) throw new InvalidOperationException("Wait for disk creation to finish.");
                if (manager.IsRunning(vm.Id)) throw new InvalidOperationException("Stop this machine before saving settings.");
                QemuCommand.Validate(vm); Close(true);
            }
            catch (Exception ex) { Error(ex); }
        }, "primary");
        var actions = IsReadOnly ? Row(Button("Close", () => Close(false))) : Row(Button("Cancel", () => { if (!diskBusy) Close(false); }), save); actions.HorizontalAlignment = HorizontalAlignment.Right;
        if (IsReadOnly) message.Text = "Settings captured when this window opened, including current mounted media. Shut down the VM to edit hardware.";
        var footer = Stack(message, actions); Grid.SetRow(footer, 2); content.Children.Add(footer);
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("212,*") };
        layout.Children.Add(new Border { Background = Brush.Parse("#151715"), BorderBrush = Brush.Parse("#30362C"), BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(16, 24, 0, 18), Child = Scroll(sidebar) });
        content.Margin = new Thickness(28, 24, 18, 22); Grid.SetColumn(content, 1); layout.Children.Add(content); Content = layout; Chrome.Frame(this);
        Closing += (_, e) => { if (diskBusy) { e.Cancel = true; message.Text = "Wait for disk creation to finish."; } };
        ShowSection("General");
    }
    private void Error(Exception ex) { message.Text = ex.Message; message.Foreground = Brush.Parse("#F7B4A9"); }
    public void ShowSection(string selected)
    {
        section = selected; navigation.Children.Clear();
        foreach (var name in Sections)
        {
            if (name is "General" or "System" or "Sharing") { var label = Eyebrow(name == "General" ? "IDENTITY" : name == "System" ? "HARDWARE" : "INTEGRATION"); label.FontSize = 9; label.Margin = new Thickness(12, name == "General" ? 0 : 14, 0, 5); navigation.Children.Add(label); }
            var icon = name switch { "System" => Icons.Cpu, "Drives" or "Boot" => Icons.Disk, "Display" or "General" => Icons.Monitor, "Network" or "Sharing" => Icons.Network, _ => Icons.Settings };
            var b = NavigationButton(name, icon, () => ShowSection(name)); if (name == section) b.Classes.Add("active"); navigation.Children.Add(b);
        }
        Control panel = IsReadOnly ? ReadOnlySection(section) : section switch
        {
            "General" => General(), "System" => SystemSettings(), "Boot" => Boot(), "Drives" => Drives(), "Display" => Display(), "Network" => Network(), "Sound" => Sound(), "USB & input" => Usb(), "Sharing" => Sharing(), "Serial" => Serial(), _ => Advanced()
        };
        body.Content = Scroll(Stack(Heading(section, 22), panel));
    }
    private Control General() => Stack(
        Field("Name", Input(vm.Name, s => vm.Name = s)),
        Field("Guest system", Select(vm.Guest, ["Linux", "Windows", "Other"], s => vm.Guest = s)),
        Field("Notes", Input(vm.Description, s => vm.Description = s, true)),
        Card(Stack(Eyebrow("IDENTITY"), Muted("Machine ID\n" + vm.Id), Muted("Created " + vm.Created.ToLocalTime().ToString("g")), Muted("Files\n" + AppPaths.VmDirectory(prefs, vm.Id)))));
    private Control SystemSettings() => Stack(
        Field("Architecture", Select(vm.Architecture, ["x86_64", "i386", "aarch64", "arm", "riscv64", "ppc"], s =>
        {
            if (vm.Architecture == s) return;
            vm.SetArchitecture(s);
            message.Text = "Architecture changed. Review firmware, drive buses and devices for compatibility."; ShowSection(section);
        }), "Switching architecture resets the machine, CPU, display card and accelerator defaults."),
        Columns(Field("Machine type", Input(vm.Machine, s => vm.Machine = s)), Field("CPU model", Input(vm.Cpu, s => vm.Cpu = s))),
        Muted("Inspect valid machine and CPU names in QEMU Engine → Host & engine details. Defaults suit common builds; custom boards may need different devices."),
        Field("Acceleration", Select(vm.Accelerator, ["tcg", "whpx"], s => { vm.Accelerator = s; vm.Cpu = "max"; ShowSection(section); }), "TCG emulates in software and modern desktop installers can take a long time. WHPX uses Windows Hypervisor Platform; it must be enabled on your host."),
        Field("Memory (MiB)", Number(vm.MemoryMiB, 128, 1048576, n => vm.MemoryMiB = n)),
        Columns(Field("CPU cores", Number(vm.Cores, 1, 256, n => vm.Cores = n)), Field("Threads per core", Number(vm.Threads, 1, 8, n => vm.Threads = n))),
        Field("CPU feature overrides", Input(vm.CpuFeatures, s => vm.CpuFeatures = s), "Optional QEMU features, for example +sse4.2,-avx. Support depends on the selected CPU and accelerator."));
    private Control Boot() => Stack(
        Check("Use UEFI firmware (pflash)", vm.Uefi, b => vm.Uefi = b),
        Button("Find bundled UEFI firmware", () =>
        {
            var share = Path.Combine(prefs.QemuDirectory, "share");
            var code = Path.Combine(share, "edk2-" + vm.Architecture + "-code.fd");
            var varsArch = vm.Architecture switch { "x86_64" => "i386", "aarch64" => "arm", _ => vm.Architecture };
            var vars = Path.Combine(share, "edk2-" + varsArch + "-vars.fd");
            if (!File.Exists(code)) { message.Text = "No matching bundled EDK2 firmware found. Browse to firmware for this architecture."; return; }
            vm.FirmwareCode = code; vm.FirmwareVarsTemplate = File.Exists(vars) ? vars : ""; vm.Uefi = true; ShowSection(section);
        }),
        FileField("Firmware code image", vm.FirmwareCode, s => vm.FirmwareCode = s, "Choose firmware matching the guest architecture. QEMU's share folder may contain edk2 images."),
        FileField("Firmware variables template", vm.FirmwareVarsTemplate, s => vm.FirmwareVarsTemplate = s, "Optional. Copied into this machine's private nvram.fd on its first UEFI start; later boots preserve it."),
        Field("Boot order", Select(vm.BootOrder, ["dc", "cd", "c", "d", "n", "cdn"], s => vm.BootOrder = s), "c = disk, d = CD/DVD, n = network. Use dc when installing from ISO."),
        Check("Show boot menu", vm.BootMenu, b => vm.BootMenu = b),
        Check("Use local time for the hardware clock", vm.RtcLocaltime, b => vm.RtcLocaltime = b),
        Heading("Direct kernel boot", 17),
        FileField("Kernel image", vm.Kernel, s => vm.Kernel = s),
        FileField("Initial RAM disk", vm.Initrd, s => vm.Initrd = s),
        Field("Kernel command line", Input(vm.KernelArguments, s => vm.KernelArguments = s, true)),
        Muted("UEFI is not Secure Boot or a TPM. Windows 11 requirements need additional guest-specific configuration."));
    private Control Drives()
    {
        var panel = Stack(Muted("Attach existing images, create a new disk, or mount an installation ISO. Removing a drive here only detaches it; the image stays on disk."));
        foreach (var d in vm.Drives.ToArray())
        {
            panel.Children.Add(Card(Stack(
                Columns(Heading(d.CdRom ? "CD / DVD" : "Virtual disk", 17), Button("Detach", () => { vm.Drives.Remove(d); ShowSection(section); }), "*,Auto"),
                FileField("Image path", d.Path, s => d.Path = s),
                Columns(Field("Format", Select(d.Format, ["qcow2", "raw", "vhdx", "vmdk", "vdi"], s => d.Format = s)), Field("Interface", Select(d.Interface, d.CdRom ? ["ide", "scsi"] : ["virtio", "ide", "scsi", "usb"], s => d.Interface = s))),
                Field("Cache mode", Select(d.Cache, ["writeback", "writethrough", "none", "directsync", "unsafe"], s => d.Cache = s), "Unsafe cache can lose guest data on host failure."),
                Check("Read-only", d.CdRom || d.ReadOnly, b => d.ReadOnly = b || d.CdRom),
                Check("Enable discard / TRIM", d.Discard, b => d.Discard = b))));
        }
        panel.Children.Add(Row(AsyncButton("Attach disk image", async () =>
        {
            var path = await PickFile("Choose a disk image"); if (path is null) return;
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            vm.Drives.Add(new DriveConfig { Path = path, Interface = vm.Guest == "Windows" && vm.Architecture is "x86_64" or "i386" ? "ide" : "virtio", Format = new[] { "qcow2", "vhdx", "vmdk", "vdi" }.Contains(ext) ? ext : "raw" }); ShowSection(section);
        }, Error), AsyncButton("Attach ISO", async () =>
        {
            var path = await PickFile("Choose a boot ISO"); if (path is null) return;
            vm.Drives.Add(new DriveConfig { Path = path, Format = "raw", Interface = vm.Architecture is "x86_64" or "i386" ? "ide" : "scsi", CdRom = true, ReadOnly = true, Discard = false }); ShowSection(section);
        }, Error)));
        var size = 64; var format = "qcow2";
        var target = Path.Combine(AppPaths.VmDirectory(prefs, vm.Id), $"disk-{vm.Drives.Count}.qcow2");
        var targetBox = Input(target, s => target = s);
        panel.Children.Add(Card(Stack(Heading("Create a new disk", 18), Field("Destination", targetBox), Columns(Field("Capacity (GiB)", Number(size, 1, 65536, n => size = n)), Field("Format", Select(format, ["qcow2", "raw"], s => { format = s; targetBox.Text = Path.ChangeExtension(target, s); }))),
            Muted("QCOW2 grows as the guest writes data. Existing files are never overwritten. A disk created here stays on disk even if you cancel settings."),
            AsyncButton("Create & attach disk", async () =>
            {
                diskBusy = true;
                try { await VmManager.CreateDiskAsync(prefs, target, size, format); vm.Drives.Add(new DriveConfig { Path = target, Format = format, Interface = vm.Guest == "Windows" && vm.Architecture is "x86_64" or "i386" ? "ide" : "virtio" }); message.Text = "Disk created and attached."; ShowSection(section); }
                finally { diskBusy = false; }
            }, Error, "primary"))));
        return panel;
    }
    private Control Display() => Stack(
        Field("Display backend", Select(vm.Display, ["qemik", "sdl", "gtk", "vnc", "none"], s => vm.Display = s), "qemik opens a dedicated guest window with input, scaling and power controls. SDL uses a GPU-capable surface inside Qemik on Windows. GTK uses an external QEMU window; vnc needs a separate viewer."),
        Field("Emulated video card", Select(vm.Video, ["virtio-vga", "virtio-vga-gl", "VGA", "qxl", "virtio-gpu-pci", "virtio-gpu-gl-pci", "none"], s => vm.Video = s), "virtio-vga is 2D. virtio-vga-gl enables experimental VirGL 3D with an OpenGL display backend. Requires a compatible QEMU build and host graphics driver; revert to virtio-vga if it fails."),
        GraphicsProfiles.SupportsNativeGpu(vm) ? Card(Stack(Heading("Host GPU acceleration", 17),
            Muted("Run Linux graphics through your Windows GPU using VirGL inside the Qemik guest window. This avoids the integrated viewer's framebuffer copies. Modern Ubuntu includes the guest driver."),
            Row(Button("Use host GPU (custom window)", () => { GraphicsProfiles.UseNativeGpu(vm); ShowSection(section); message.Text = "GPU display selected. Save and start the VM to open its custom guest window."; }, "primary"),
                Button("Use integrated 2D display", () => { GraphicsProfiles.UseIntegratedDisplay(vm); ShowSection(section); message.Text = "Integrated 2D display selected. Save and start the VM to apply it."; })),
            Muted("GPU mode includes the Qemik toolbar, fullscreen, power controls, mounted disks, shared folders and current settings. Closing the guest window keeps the VM running; Open reconnects it. Window resizing is forwarded to the guest automatically. Text clipboard uses spice-vdagent; enable Sharing and install the guest agent. Click the guest display or Capture keyboard to type."))) : Muted("VirGL guest support depends on its operating system and graphics driver."),
        Muted("VirGL accelerates guest OpenGL through a virtual GPU. It does not expose the physical card for CUDA or GPU passthrough. On Windows, the integrated egl-headless path can fail even when native SDL acceleration works."),
        Check("Start in full screen (SDL / GTK)", vm.Fullscreen, b => vm.Fullscreen = b),
        Field("VNC display number", Number(vm.VncDisplay, 0, 99, n => vm.VncDisplay = n), "Display 1 uses TCP port 5901. Bound to 127.0.0.1, accessible only from this computer. Use a different display for each running VNC VM."),
        Card(Muted("Enable Fit resolution in the Qemik guest window to request automatic resizing up to 3840×2160 (4K), accounting for Windows display scaling. The guest graphics driver must accept the request. You can also use Ubuntu Settings → Displays. Lower resolutions reduce rendering cost.")));
    private Control Network()
    {
        var panel = Stack(Field("Network mode", Select(vm.Network, ["user", "tap", "none"], s => vm.Network = s), "User = shared NAT with no administrator setup. TAP uses an existing adapter. None disconnects the guest."),
            Field("Network device", Input(vm.NetworkCard, s => vm.NetworkCard = s), "virtio-net-pci needs guest drivers; e1000e is compatible with many x86 guests."),
            Field("MAC address", Input(vm.MacAddress, s => vm.MacAddress = s)),
            Field("TAP adapter name", Input(vm.TapInterface, s => vm.TapInterface = s), "Only used in TAP mode. Create and configure the Windows TAP adapter separately."),
            Check("Isolate guest network (user mode)", vm.IsolateNetwork, b => vm.IsolateNetwork = b),
            Heading("Port forwarding", 18), Muted("User-mode networking only. Host listeners bind to 127.0.0.1. For SSH, forward host 2222 to guest 22."));
        foreach (var f in vm.PortForwards.ToArray())
        {
            panel.Children.Add(Card(Stack(Columns(Field("Protocol", Select(f.Protocol, ["tcp", "udp"], s => f.Protocol = s)), Button("Remove rule", () => { vm.PortForwards.Remove(f); ShowSection(section); })),
                Columns(Field("Host port", Number(f.HostPort, 1024, 65535, n => f.HostPort = n)), Field("Guest port", Number(f.GuestPort, 1, 65535, n => f.GuestPort = n))))));
        }
        panel.Children.Add(Button("＋  Add forwarding rule", () => { vm.PortForwards.Add(new PortForward()); ShowSection(section); })); return panel;
    }
    private Control Sound() => Stack(Field("Audio device", Select(vm.Audio, ["none", "intel-hda", "AC97"], s => vm.Audio = s), "Choose intel-hda for Ubuntu playback through the Windows default output. Fully shut down and start the VM after changing hardware."), Check("Enable microphone input", vm.AudioCapture, b => vm.AudioCapture = b), Card(Muted("Microphone input needs a working Windows capture device. Playback works without enabling it. Select none for headless guests or unsupported machine types.")));
    private Control Usb() => Stack(Check("Enable USB 3 controller (qemu-xhci)", vm.Usb, b => vm.Usb = b), Check("Use an absolute pointing tablet", vm.Tablet, b => vm.Tablet = b),
        Field("USB host devices", Input(vm.UsbDevices, s => vm.UsbDevices = s, true), "One hexadecimal vendor:product ID per line, for example 1234:5678. Requires a QEMU build with usb-host and suitable Windows USB drivers. Attached devices become available to the guest."),
        Muted("The emulated keyboard is provided by the machine/display backend. Host USB passthrough is separate from the virtual tablet."));
    private Control Sharing()
    {
        var path = Input(vm.SharedFolder, s => vm.SharedFolder = s);
        return Stack(Check("Share text clipboard with the guest", vm.SharedClipboard, b => vm.SharedClipboard = b),
            Muted("Requires a full VM shutdown/start after enabling. Inside Ubuntu, install spice-vdagent, then log out and back in. Guest session support varies, especially on Wayland. Unicode text is supported; images and files are not supported."),
            Code("sudo apt update\nsudo apt install spice-vdagent"),
            Heading("Host folders", 18), AsyncButton("Shared folders · No password", () => new HostFoldersWindow(vm).ShowDialog(this), Error),
            Muted("Choose any Windows folder and its access level. Ubuntu opens it as a network folder; this also works while the VM runs. Shares are created immediately and independently of Save settings."),
            Field("Shared folder (VirtFS / 9p)", path), AsyncButton("Choose folder", async () => { var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a guest shared folder" }); if (folders.FirstOrDefault()?.TryGetLocalPath() is string selected) path.Text = selected; }, Error),
            Check("Read-only share", vm.ShareReadOnly, b => vm.ShareReadOnly = b),
            Muted("Optional and build-dependent. Many Windows QEMU builds do not ship VirtFS. Qemik checks for -virtfs support before launch and reports an error if unavailable. Clear the field to disable sharing."),
            Field("Linux guest mount command", Code("sudo mkdir -p /mnt/share\nsudo mount -t 9p -o trans=virtio,version=9p2000.L share /mnt/share")),
            Muted("Shared folders uses a local WebDAV connection in Ubuntu Files without a password or administrator prompt. Previous Windows SMB shares remain available from that window. File clipboard transfer is not implemented."));
    }
    private Control Serial() => Stack(Field("Serial output", Select(vm.Serial, ["none", "file", "tcp"], s => vm.Serial = s)), Field("Local TCP port", Number(vm.SerialPort, 1024, 65535, n => vm.SerialPort = n), "TCP mode listens on 127.0.0.1 only. Use a terminal client to connect."),
        Muted("File mode writes guest serial output to:\n" + Path.Combine(AppPaths.VmDirectory(prefs, vm.Id), "serial.log")));
    private Control Advanced()
    {
        var preview = Code("");
        void Refresh() { try { preview.Text = QemuCommand.Build(vm, prefs).Preview; } catch (Exception ex) { preview.Text = ex.Message; } }
        Refresh();
        return Stack(Check("Discard disk changes after shutdown (-snapshot)", vm.Ephemeral, b => vm.Ephemeral = b), Check("Exit instead of rebooting (-no-reboot)", vm.NoReboot, b => vm.NoReboot = b),
            Field("Additional QEMU arguments", Input(vm.ExtraArguments, s => vm.ExtraArguments = s, true), "One argument token per line. Put an option and its value on separate lines. Do not add shell quotes. These options can override generated hardware settings; QMP and process-control options are reserved."),
            Muted("Example: -device on one line and virtio-rng-pci on the next. Arguments go directly to QEMU without invoking a shell."),
            Row(Heading("Launch preview", 17), Button("Refresh preview", Refresh)), preview);
    }
    private Control FileField(string title, string value, Action<string> changed, string? hint = null)
    {
        var input = Input(value, changed);
        return Field(title, Columns(input, AsyncButton("Browse…", async () => { var path = await PickFile(title); if (path is not null) input.Text = path; }, Error), "*,Auto"), hint);
    }
    private async Task<string?> PickFile(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false }); return files.FirstOrDefault()?.TryGetLocalPath();
    }
}
