using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Qemik.Core;

public sealed record QemuCommand(string Executable, IReadOnlyList<string> Arguments)
{
    public ProcessStartInfo StartInfo()
    {
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in Arguments) info.ArgumentList.Add(arg);
        return info;
    }
    public string Preview => "& " + string.Join(" `\n  ", new[] { Executable }.Concat(Arguments).Select(s => "'" + s.Replace("'", "''") + "'"));
    private static string Opt(string value) => value.Replace(",", ",,");
    public static void Validate(VmConfig vm)
    {
        if (!Guid.TryParseExact(vm.Id, "N", out _)) throw new InvalidDataException("Invalid virtual machine ID.");
        if (string.IsNullOrWhiteSpace(vm.Name)) throw new InvalidDataException("Give the virtual machine a name.");
        if (vm.MemoryMiB is < 128 or > 1048576) throw new InvalidDataException("Memory must be between 128 MiB and 1 TiB.");
        if (vm.Cores is < 1 or > 256 || vm.Threads is < 1 or > 8 || vm.Cores * vm.Threads > 256) throw new InvalidDataException("Choose 1–256 virtual CPUs (cores × threads).");
        if (!new[] { "x86_64", "i386", "aarch64", "arm", "riscv64", "ppc" }.Contains(vm.Architecture)) throw new InvalidDataException("Unsupported architecture.");
        foreach (var value in new[] { vm.Machine, vm.Cpu, vm.Video, vm.NetworkCard })
            if (!Regex.IsMatch(value, @"^[a-zA-Z0-9_.+\-]+$")) throw new InvalidDataException("Machine and device names may only contain letters, digits, dots, plus signs, underscores and hyphens.");
        if (!new[] { "tcg", "whpx" }.Contains(vm.Accelerator)) throw new InvalidDataException("Choose TCG or WHPX acceleration.");
        if (vm.Accelerator == "whpx" && vm.Architecture is not ("x86_64" or "i386")) throw new InvalidDataException("Qemik's WHPX configuration requires an x86 guest. Choose TCG for this architecture.");
        if (!Regex.IsMatch(vm.BootOrder, "^[cdn]{1,3}$")) throw new InvalidDataException("Boot order uses c (disk), d (CD), n (network).");
        if (!Regex.IsMatch(vm.MacAddress, "^([0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$")) throw new InvalidDataException("Enter a MAC address like 52:54:00:12:34:56.");
        if (!new[] { "none", "user", "tap" }.Contains(vm.Network)) throw new InvalidDataException("Invalid network mode.");
        if (vm.Network == "tap" && string.IsNullOrWhiteSpace(vm.TapInterface)) throw new InvalidDataException("TAP networking needs the name of an existing Windows TAP adapter.");
        if (!new[] { "qemik", "sdl", "gtk", "vnc", "none" }.Contains(vm.Display)) throw new InvalidDataException("Invalid display backend.");
        if (!new[] { "none", "intel-hda", "AC97" }.Contains(vm.Audio)) throw new InvalidDataException("Invalid audio device.");
        if (!new[] { "none", "file", "tcp" }.Contains(vm.Serial)) throw new InvalidDataException("Invalid serial mode.");
        if (vm.SerialPort is < 1024 or > 65535 || vm.VncDisplay is < 0 or > 99) throw new InvalidDataException("Serial ports must be 1024–65535; VNC display numbers 0–99.");
        foreach (var p in vm.PortForwards)
            if (p.Protocol is not ("tcp" or "udp") || p.HostPort is < 1024 or > 65535 || p.GuestPort is < 1 or > 65535) throw new InvalidDataException("Port forwards require TCP/UDP, host ports 1024–65535 and guest ports 1–65535.");
        if (vm.PortForwards.GroupBy(p => (p.Protocol, p.HostPort)).Any(g => g.Count() > 1)) throw new InvalidDataException("Two forwarding rules use the same host port.");
        if (vm.Network != "user" && vm.PortForwards.Count > 0) throw new InvalidDataException("Port forwarding requires shared (user) networking.");
        foreach (var d in vm.Drives)
        {
            if (string.IsNullOrWhiteSpace(d.Path)) throw new InvalidDataException("Every drive needs an image path.");
            if (!new[] { "qcow2", "raw", "vhdx", "vmdk", "vdi" }.Contains(d.Format)) throw new InvalidDataException("Invalid drive format.");
            if (!new[] { "virtio", "ide", "scsi", "usb" }.Contains(d.Interface)) throw new InvalidDataException("Invalid drive interface.");
            if (d.CdRom && d.Interface is not ("ide" or "scsi")) throw new InvalidDataException("CD/DVD drives require IDE or SCSI.");
            if (d.Interface == "usb" && !vm.Usb) throw new InvalidDataException("USB drives require the USB controller.");
            if (!new[] { "writeback", "writethrough", "none", "directsync", "unsafe" }.Contains(d.Cache)) throw new InvalidDataException("Invalid disk cache mode.");
            if (vm.Architecture is not ("x86_64" or "i386" or "ppc") && d.Interface == "ide") throw new InvalidDataException("This machine needs SCSI or VirtIO drives instead of IDE.");
        }
        if (vm.Tablet && !vm.Usb) throw new InvalidDataException("The USB tablet requires the USB controller.");
        foreach (var device in vm.UsbDevices.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!Regex.IsMatch(device, "^[0-9a-fA-F]{4}:[0-9a-fA-F]{4}$")) throw new InvalidDataException("USB devices use vendor:product hexadecimal IDs, one per line (for example 1234:5678).");
        if (vm.UsbDevices.Trim().Length > 0 && !vm.Usb) throw new InvalidDataException("USB passthrough requires the USB controller.");
        if (!string.IsNullOrWhiteSpace(vm.CpuFeatures) && !Regex.IsMatch(vm.CpuFeatures, @"^[a-zA-Z0-9_=.,+\-]+$")) throw new InvalidDataException("CPU feature overrides must be comma-separated QEMU feature names.");
        var extra = Extra(vm);
        if (extra.Any(s => s.StartsWith('-') && new[] { "qmp", "monitor", "daemonize", "pidfile", "incoming", "readconfig", "chardev", "mon" }.Contains(s.TrimStart('-').Split('=')[0])))
            throw new InvalidDataException("Qemik reserves QMP, monitor, process, and configuration-file options for lifecycle management.");
    }
    public static string[] Extra(VmConfig vm) => vm.ExtraArguments.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    public static QemuCommand Build(VmConfig vm, Preferences prefs, int? qmpPort = null, int? guestPort = null, int? clipboardPort = null)
    {
        Validate(vm);
        var dir = AppPaths.VmDirectory(prefs, vm.Id);
        var args = new List<string>();
        void Add(string key, string value) { args.Add(key); args.Add(value); }
        Add("-name", "guest=" + Opt(vm.Name)); Add("-uuid", Guid.ParseExact(vm.Id, "N").ToString());
        Add("-machine", vm.Machine); Add("-accel", vm.Accelerator == "tcg" ? "tcg,thread=multi" : "whpx");
        Add("-cpu", vm.Cpu + (string.IsNullOrWhiteSpace(vm.CpuFeatures) ? "" : "," + vm.CpuFeatures));
        Add("-m", vm.MemoryMiB.ToString()); Add("-smp", $"cpus={vm.Cores * vm.Threads},sockets=1,cores={vm.Cores},threads={vm.Threads}");
        Add("-boot", $"order={vm.BootOrder},menu={(vm.BootMenu ? "on" : "off")}");
        Add("-rtc", "base=" + (vm.RtcLocaltime ? "localtime" : "utc"));
        if (vm.Uefi)
        {
            Add("-drive", $"if=pflash,format=raw,unit=0,readonly=on,file={Opt(vm.FirmwareCode)}");
            if (!string.IsNullOrWhiteSpace(vm.FirmwareVarsTemplate)) Add("-drive", $"if=pflash,format=raw,unit=1,file={Opt(Path.Combine(dir, "nvram.fd"))}");
        }
        if (vm.Kernel.Length > 0) Add("-kernel", vm.Kernel);
        if (vm.Initrd.Length > 0) Add("-initrd", vm.Initrd);
        if (vm.KernelArguments.Length > 0) Add("-append", vm.KernelArguments);
        if (vm.Usb) Add("-device", "qemu-xhci,id=usb0");
        if (vm.Drives.Any(d => d.Interface == "scsi")) Add("-device", "virtio-scsi-pci,id=scsi0");
        for (var i = 0; i < vm.Drives.Count; i++)
        {
            var d = vm.Drives[i]; var bus = d.Interface is "scsi" or "usb" ? "none" : d.Interface;
            Add("-drive", $"file={Opt(d.Path)},format={d.Format},if={bus},id=drive{i},media={(d.CdRom ? "cdrom" : "disk")},readonly={(d.ReadOnly || d.CdRom ? "on" : "off")},cache={d.Cache}" + (!d.CdRom && d.Discard ? ",discard=unmap" : ""));
            if (d.Interface == "scsi") Add("-device", $"{(d.CdRom ? "scsi-cd" : "scsi-hd")},drive=drive{i},bus=scsi0.0");
            if (d.Interface == "usb") Add("-device", $"usb-storage,drive=drive{i},bus=usb0.0");
        }
        if (vm.Video != "none") Add("-device", vm.Video);
        // Disable the machine's implicit VGA device: only the selected device should be present.
        if (vm.Architecture is "x86_64" or "i386" or "ppc") Add("-vga", "none");
        var acceleratedVideo = vm.Video is "virtio-vga-gl" or "virtio-gpu-gl-pci" or "virtio-gpu-gl-device";
        var displayBackend = acceleratedVideo ? (vm.Display is "qemik" or "vnc" or "none" ? "egl-headless" : vm.Display + ",gl=on") : vm.Display is "vnc" or "qemik" ? "none" : vm.Display;
        // SDL's close button otherwise terminates QEMU without a guest shutdown.
        if (vm.Display == "sdl") displayBackend += ",window-close=off";
        Add("-display", displayBackend);
        if (vm.Display == "qemik") Add("-vnc", $"127.0.0.1:{(guestPort ?? 5900 + vm.VncDisplay) - 5900}");
        if (vm.Display == "vnc") Add("-vnc", $"127.0.0.1:{vm.VncDisplay}");
        if (vm.Fullscreen && vm.Display is "sdl" or "gtk") args.Add("-full-screen");
        if (vm.Audio != "none")
        {
            Add("-audiodev", "dsound,id=audio0" + (vm.AudioCapture ? "" : ",in.voices=0"));
            if (vm.Audio == "intel-hda") { Add("-device", "intel-hda"); Add("-device", (vm.AudioCapture ? "hda-duplex" : "hda-output") + ",audiodev=audio0"); }
            else Add("-device", "AC97,audiodev=audio0");
        }
        if (vm.SharedClipboard)
        {
            Add("-device", "virtio-serial-pci,id=clipboard-serial");
            Add("-chardev", clipboardPort.HasValue
                ? $"socket,id=clipboard-agent,host=127.0.0.1,port={clipboardPort.Value},server=on,wait=off"
                : "qemu-vdagent,id=clipboard-agent,name=vdagent,clipboard=on,mouse=off");
            Add("-device", "virtserialport,bus=clipboard-serial.0,chardev=clipboard-agent,name=com.redhat.spice.0");
        }
        if (vm.Network == "none") Add("-nic", "none");
        else
        {
            var net = vm.Network == "user" ? $"user,id=net0,restrict={(vm.IsolateNetwork ? "on" : "off")}" : $"tap,id=net0,ifname={Opt(vm.TapInterface)},script=no,downscript=no";
            foreach (var f in vm.PortForwards) net += $",hostfwd={f.Protocol}:127.0.0.1:{f.HostPort}-:{f.GuestPort}";
            Add("-netdev", net); Add("-device", $"{vm.NetworkCard},netdev=net0,mac={vm.MacAddress}");
        }
        if (vm.Usb)
        {
            if (vm.Tablet) Add("-device", "usb-tablet,bus=usb0.0");
            foreach (var usb in vm.UsbDevices.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            { var ids = usb.Split(':'); Add("-device", $"usb-host,vendorid=0x{ids[0]},productid=0x{ids[1]},bus=usb0.0"); }
        }
        if (vm.SharedFolder.Length > 0) Add("-virtfs", $"local,path={Opt(vm.SharedFolder)},mount_tag=share,security_model=none,readonly={(vm.ShareReadOnly ? "on" : "off")}");
        Add("-serial", vm.Serial switch { "file" => "file:" + Path.Combine(dir, "serial.log"), "tcp" => $"tcp:127.0.0.1:{vm.SerialPort},server=on,wait=off", _ => "none" });
        Add("-monitor", "none");
        if (qmpPort.HasValue) Add("-qmp", $"tcp:127.0.0.1:{qmpPort},server=on,wait=off");
        if (vm.NoReboot) args.Add("-no-reboot");
        if (vm.Ephemeral) args.Add("-snapshot");
        args.AddRange(Extra(vm));
        return new QemuCommand(Path.Combine(prefs.QemuDirectory, $"qemu-system-{vm.Architecture}.exe"), args);
    }
}
