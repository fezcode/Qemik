using Qemik.Core;
using Xunit;

namespace Qemik.Tests;

// Excerpts of real QEMU 11.1 x86_64 help output.
public sealed class QemuCapabilitiesTests
{
    private const string MachineHelp = """
        Supported machines are:
        microvm              microvm (i386)
        pc                   Standard PC (i440FX + PIIX, 1996) (alias of pc-i440fx-11.1)
        pc-i440fx-11.1       Standard PC (i440FX + PIIX, 1996) (default)
        pc-i440fx-8.1        Standard PC (i440FX + PIIX, 1996) (deprecated)
        q35                  Standard PC (Q35 + ICH9, 2009) (alias of pc-q35-11.1)
        pc-q35-11.1          Standard PC (Q35 + ICH9, 2009)
        none                 empty machine
        """;
    private const string CpuHelp = """
        Available CPUs:
          486                   (alias configured by machine type)
          486-v1
          Broadwell-IBRS        (alias of Broadwell-v3)
          max                   Enables all features supported by the accelerator in the current host
          qemu64                QEMU Virtual CPU version 2.5+

        Recognized CPUID flags:
          3dnow 3dnowext abm adx aes
          avx avx2 sse4.2 x2apic
        """;
    private const string DeviceHelp = """
        USB devices:
        name "qemu-xhci", bus PCI
        name "usb-host", bus usb-bus

        Network devices:
        name "e1000", bus PCI, alias "e1000-82540em", desc "Intel Gigabit Ethernet"
        name "e1000e", bus PCI, desc "Intel 82574L GbE Controller"
        name "virtio-net-pci", bus PCI, alias "virtio-net"

        Input devices:
        name "usb-tablet", bus usb-bus
        """;

    [Fact]
    public void MachinesKeepEveryNameWithItsDescription()
    {
        var machines = QemuCapabilities.ParseMachines(MachineHelp);
        Assert.Equal(["microvm", "pc", "pc-i440fx-11.1", "pc-i440fx-8.1", "q35", "pc-q35-11.1", "none"], machines.Select(m => m.Value));
        Assert.Equal("Standard PC (Q35 + ICH9, 2009) (alias of pc-q35-11.1)", machines.Single(m => m.Value == "q35").Description);
    }

    [Fact]
    public void CpusStopBeforeTheFlagListAndKeepBlankDescriptions()
    {
        var cpus = QemuCapabilities.ParseCpus(CpuHelp);
        Assert.Equal(["486", "486-v1", "Broadwell-IBRS", "max", "qemu64"], cpus.Select(c => c.Value));
        Assert.Equal("", cpus.Single(c => c.Value == "486-v1").Description);
    }

    [Fact]
    public void CpuFlagsComeFromTheRecognizedFlagBlock()
    {
        Assert.Equal(["3dnow", "3dnowext", "abm", "adx", "aes", "avx", "avx2", "sse4.2", "x2apic"], QemuCapabilities.ParseCpuFlags(CpuHelp));
        Assert.Empty(QemuCapabilities.ParseCpuFlags("Available CPUs:\n  max\n"));
    }

    [Fact]
    public void DevicesAreReadFromOneCategoryOnly()
    {
        var network = QemuCapabilities.ParseDevices(DeviceHelp, "Network devices");
        Assert.Equal(["e1000", "e1000e", "virtio-net-pci"], network.Select(d => d.Value));
        Assert.Equal("Intel 82574L GbE Controller", network.Single(d => d.Value == "e1000e").Description);
        Assert.Empty(QemuCapabilities.ParseDevices(DeviceHelp, "Sound devices"));
    }

    [Fact]
    public void FeatureOverridesRoundTripAsSignedFlags()
    {
        var flags = CpuFeatures.Parse(" +sse4.2, -avx ,x2apic,,");
        Assert.Equal([new CpuFeature("sse4.2", true), new CpuFeature("avx", false), new CpuFeature("x2apic", true)], flags);
        Assert.Equal("+sse4.2,-avx,+x2apic", CpuFeatures.Format(flags));
        Assert.Equal("", CpuFeatures.Format([]));
    }

    [Fact]
    public void UsbIdsComeFromDeviceInstancesNotInterfacesOrHubs()
    {
        Assert.Equal("046d:c52b", HostDevices.UsbId(@"USB\VID_046D&PID_C52B\5&1A2B3C&0&2"));
        Assert.Null(HostDevices.UsbId(@"USB\VID_046D&PID_C52B&MI_00\6&2B&0&0000"));
        Assert.Null(HostDevices.UsbId(@"USB\ROOT_HUB30\4&1F2E&0&0"));
    }

    [Fact]
    public async Task InstalledQemuReportsItsOwnLists()
    {
        if (Environment.GetEnvironmentVariable("QEMIK_QEMU_DIR") is not { } qemu) { Assert.Skip("Opt in with QEMIK_QEMU_DIR to read lists from a real QEMU."); return; }
        var caps = await QemuCapabilities.LoadAsync(qemu, "x86_64");
        Assert.True(caps.FromQemu);
        Assert.Contains(caps.Machines, m => m.Value == "q35");
        Assert.Contains(caps.Cpus, c => c.Value == "max");
        Assert.Contains("sse4.2", caps.CpuFlags);
        Assert.Contains(caps.NetworkDevices, d => d.Value == "virtio-net-pci");
        Assert.True(caps.Machines.Count > 20 && caps.Cpus.Count > 50, $"{caps.Machines.Count} machines, {caps.Cpus.Count} CPUs");
    }

    [Fact]
    public void FallbacksStillOfferTheArchitectureDefaults()
    {
        var fallback = QemuCapabilities.Fallback("aarch64");
        Assert.Contains(fallback.Machines, m => m.Value == "virt");
        Assert.Contains(fallback.Cpus, c => c.Value == "cortex-a72");
        Assert.Contains(fallback.NetworkDevices, d => d.Value == "virtio-net-pci");
        Assert.False(fallback.FromQemu);
    }
}
