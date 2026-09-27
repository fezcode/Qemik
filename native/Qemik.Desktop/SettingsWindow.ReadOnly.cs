using Avalonia.Controls;
using Qemik.Core;
using static Qemik.Desktop.Ui;

namespace Qemik.Desktop;

public sealed partial class SettingsWindow
{
    private static string YesNo(bool value) => value ? "Enabled" : "Disabled";
    private static Control ReadFields(params (string Name, object? Value)[] fields) => Stack(fields.Select(f => Field(f.Name,
        new TextBox { Text = string.IsNullOrEmpty(f.Value?.ToString()) ? "—" : f.Value.ToString(), IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 37 })).ToArray());
    private Control ReadOnlySection(string selected) => selected switch
    {
        "General" => ReadFields(("Name", vm.Name), ("Guest system", vm.Guest), ("Notes", vm.Description), ("Machine ID", vm.Id), ("Created", vm.Created.ToLocalTime()), ("Files", AppPaths.VmDirectory(prefs, vm.Id))),
        "System" => ReadFields(("Architecture", vm.Architecture), ("Machine type", vm.Machine), ("CPU model", vm.Cpu), ("Acceleration", vm.Accelerator.ToUpperInvariant()), ("Memory (MiB)", vm.MemoryMiB), ("CPU cores", vm.Cores), ("Threads per core", vm.Threads), ("CPU feature overrides", vm.CpuFeatures)),
        "Boot" => ReadFields(("UEFI firmware", YesNo(vm.Uefi)), ("Firmware code image", vm.FirmwareCode), ("Firmware variables template", vm.FirmwareVarsTemplate), ("Boot order", vm.BootOrder), ("Boot menu", YesNo(vm.BootMenu)), ("RTC local time", YesNo(vm.RtcLocaltime)), ("Kernel image", vm.Kernel), ("Initial RAM disk", vm.Initrd), ("Kernel command line", vm.KernelArguments)),
        "Drives" => vm.Drives.Count == 0 ? Muted("No drives attached.") : Stack(vm.Drives.Select((d, i) => Card(Stack(Heading($"Drive {i + 1} · " + (d.CdRom ? "CD / DVD" : "Virtual disk"), 17), ReadFields(("Current image", d.Path.Length == 0 ? "Empty drive" : d.Path), ("Format", d.Format), ("Interface", d.Interface), ("Cache mode", d.Cache), ("Read-only", YesNo(d.ReadOnly || d.CdRom)), ("Discard / TRIM", YesNo(d.Discard)))))).ToArray()),
        "Display" => Stack(ReadFields(("Display backend", vm.Display), ("Graphics device", vm.Video), ("3D acceleration", vm.Video.Contains("-gl", StringComparison.Ordinal) ? "VirGL requested (guest driver and host support required)" : "2D virtual graphics"), ("Start in fullscreen", YesNo(vm.Fullscreen)), ("External VNC display number", vm.VncDisplay)),
            Card(Muted(vm.Display == "sdl" && vm.Video.Contains("-gl", StringComparison.Ordinal)
                ? "GPU surface hosted inside Qemik. Linux Mesa/VirGL uses the host OpenGL driver. Check glxinfo -B inside Linux to verify its active renderer. Use the guest toolbar for fullscreen and power controls. Change resolution in the guest display settings."
                : "To enable host GPU acceleration, shut down and choose Display → Use host GPU (custom window). WHPX accelerates the CPU; it does not enable guest 3D graphics. In the integrated viewer, a 1080p resolution cap reduces copying overhead."))),
        "Network" => Stack(ReadFields(("Network mode", vm.Network), ("Network device", vm.NetworkCard), ("MAC address", vm.MacAddress), ("TAP adapter", vm.TapInterface), ("Isolate guest", YesNo(vm.IsolateNetwork))), Heading("Port forwarding", 17),
            vm.PortForwards.Count == 0 ? Muted("No port forwarding rules.") : ReadFields(vm.PortForwards.Select((f, i) => ($"Rule {i + 1}", (object?)$"{f.Protocol.ToUpperInvariant()} · 127.0.0.1:{f.HostPort} → guest:{f.GuestPort}")).ToArray())),
        "Sound" => ReadFields(("Audio device", vm.Audio), ("Host audio backend", vm.Audio == "none" ? "None" : "Windows DirectSound"), ("Microphone input", YesNo(vm.AudioCapture))),
        "USB & input" => ReadFields(("USB controller", YesNo(vm.Usb)), ("Absolute pointing device", YesNo(vm.Tablet)), ("USB passthrough devices", vm.UsbDevices)),
        "Sharing" => Stack(ReadFields(("Guest text clipboard channel", YesNo(vm.SharedClipboard)), ("VirtFS / 9p folder", vm.SharedFolder), ("VirtFS read-only", YesNo(vm.ShareReadOnly))), Muted("Password-free host folders are managed separately in Shared folders. The guest toolbar controls whether clipboard text is currently synchronized.")),
        "Serial" => ReadFields(("Serial output", vm.Serial), ("Local TCP port", vm.SerialPort)),
        _ => Stack(ReadFields(("Discard disk changes after shutdown", YesNo(vm.Ephemeral)), ("Exit instead of rebooting", YesNo(vm.NoReboot)), ("Additional QEMU arguments", vm.ExtraArguments)), Heading("Command used to start this session", 17), Code(launchCommand ?? "Open View settings from the running machine to see its actual launch command."))
    };
}
