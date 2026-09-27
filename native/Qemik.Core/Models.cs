using System.Text.Json;

namespace Qemik.Core;

public sealed class VmConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New virtual machine";
    public string Description { get; set; } = "";
    public string Guest { get; set; } = "Linux";
    public string Architecture { get; set; } = "x86_64";
    public string Machine { get; set; } = "q35";
    public string Cpu { get; set; } = "max";
    public string CpuFeatures { get; set; } = "";
    public string Accelerator { get; set; } = "tcg";
    public int MemoryMiB { get; set; } = 4096;
    public int Cores { get; set; } = 4;
    public int Threads { get; set; } = 1;
    public bool Uefi { get; set; }
    public string FirmwareCode { get; set; } = "";
    public string FirmwareVarsTemplate { get; set; } = "";
    public string BootOrder { get; set; } = "dc";
    public bool BootMenu { get; set; } = true;
    public string Kernel { get; set; } = "";
    public string Initrd { get; set; } = "";
    public string KernelArguments { get; set; } = "";
    public List<DriveConfig> Drives { get; set; } = [];
    public string Display { get; set; } = "qemik";
    public string Video { get; set; } = "virtio-vga";
    public bool Fullscreen { get; set; }
    public int VncDisplay { get; set; } = 1;
    public string Audio { get; set; } = "none";
    public bool AudioCapture { get; set; }
    public bool SharedClipboard { get; set; }
    public string Network { get; set; } = "user";
    public string NetworkCard { get; set; } = "virtio-net-pci";
    public string MacAddress { get; set; } = NewMac();
    public string TapInterface { get; set; } = "";
    public bool IsolateNetwork { get; set; }
    public List<PortForward> PortForwards { get; set; } = [];
    public bool Usb { get; set; } = true;
    public bool Tablet { get; set; } = true;
    public string UsbDevices { get; set; } = "";
    public string Serial { get; set; } = "none";
    public int SerialPort { get; set; } = 4444;
    public string SharedFolder { get; set; } = "";
    public bool ShareReadOnly { get; set; } = true;
    public bool RtcLocaltime { get; set; }
    public bool NoReboot { get; set; }
    public bool Ephemeral { get; set; }
    public string ExtraArguments { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public VmConfig Clone() => JsonSerializer.Deserialize<VmConfig>(JsonSerializer.Serialize(this))!;
    public void SetArchitecture(string architecture)
    {
        Architecture = architecture; Accelerator = "tcg";
        (Machine, Cpu, Video) = architecture switch
        {
            "aarch64" => ("virt", "cortex-a72", "virtio-gpu-pci"),
            "arm" => ("virt", "cortex-a15", "virtio-gpu-pci"),
            "riscv64" => ("virt", "rv64", "virtio-gpu-pci"),
            "ppc" => ("mac99", "G4", "VGA"),
            "i386" => ("pc", "max", "VGA"),
            _ => ("q35", "max", "virtio-vga")
        };
    }
    public static string NewMac() => $"52:54:00:{Random.Shared.Next(256):x2}:{Random.Shared.Next(256):x2}:{Random.Shared.Next(256):x2}";
    public static VmConfig Template(string guest) => new()
    {
        Guest = guest, Name = guest == "Other" ? "New virtual machine" : guest,
        MemoryMiB = guest == "Windows" ? 8192 : 4096,
        Video = guest == "Windows" ? "VGA" : "virtio-vga",
        NetworkCard = guest == "Windows" ? "e1000e" : "virtio-net-pci"
    };
}

public sealed class DriveConfig
{
    public string Path { get; set; } = "";
    public string Format { get; set; } = "qcow2";
    public string Interface { get; set; } = "virtio";
    public bool CdRom { get; set; }
    public bool ReadOnly { get; set; }
    public string Cache { get; set; } = "writeback";
    public bool Discard { get; set; } = true;
}
public sealed class PortForward
{
    public string Protocol { get; set; } = "tcp";
    public int HostPort { get; set; } = 2222;
    public int GuestPort { get; set; } = 22;
}
public sealed class Preferences
{
    public string QemuDirectory { get; set; } = "";
    public string LibraryDirectory { get; set; } = "";
}
public static class AppPaths
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fezcode", "Qemik");
    public static string VmDirectory(Preferences prefs, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid virtual machine ID.");
        return Path.Combine(prefs.LibraryDirectory, id);
    }
}
