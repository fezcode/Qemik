using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Qemik.Core;

/// <summary>Host hardware a VM setting can point at: plugged-in USB devices and TAP adapters.</summary>
public static partial class HostDevices
{
    /// <summary>Connected USB devices as vendor:product IDs, one entry per device, hubs excluded.</summary>
    public static IReadOnlyList<ChoiceOption> UsbDevices()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var set = SetupDiGetClassDevs(0, "USB", 0, Present | AllClasses);
        if (set == -1) return [];
        try
        {
            var data = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                var id = new StringBuilder(512);
                if (!SetupDiGetDeviceInstanceId(set, ref data, id, (uint)id.Capacity, out _)) continue;
                if (UsbId(id.ToString()) is not { } usb || found.ContainsKey(usb)) continue;
                var name = Property(set, ref data, FriendlyName) ?? Property(set, ref data, DeviceDescription) ?? "USB device";
                if (name.Contains("hub", StringComparison.OrdinalIgnoreCase)) continue;
                found[usb] = name;
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return found.Select(p => new ChoiceOption(p.Key, p.Value)).OrderBy(o => o.Description, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>
    /// The vendor:product pair QEMU's usb-host expects, from a Windows device instance ID such as
    /// USB\VID_046D&amp;PID_C52B\5&amp;1A2B. Interface children (&amp;MI_xx) and root hubs yield null.
    /// </summary>
    public static string? UsbId(string instanceId)
    {
        var m = UsbInstance().Match(instanceId);
        return m.Success ? $"{m.Groups[1].Value}:{m.Groups[2].Value}".ToLowerInvariant() : null;
    }

    /// <summary>TAP-style adapters QEMU's tap netdev can attach to, by Windows connection name.</summary>
    public static IReadOnlyList<ChoiceOption> TapAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.Description.Contains("TAP", StringComparison.OrdinalIgnoreCase) || n.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase))
                .Select(n => new ChoiceOption(n.Name, n.Description)).ToArray();
        }
        catch (NetworkInformationException) { return []; }
    }

    private static string? Property(nint set, ref DeviceInfo data, uint property)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, buffer, (uint)buffer.Length, out var size) || size < 2) return null;
        var text = Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
        return text.Length > 0 ? text : null;
    }

    [GeneratedRegex(@"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})\\", RegexOptions.IgnoreCase)] private static partial Regex UsbInstance();

    private const uint Present = 0x2, AllClasses = 0x4, DeviceDescription = 0x0, FriendlyName = 0xC;
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public nint Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(nint classGuid, string enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref DeviceInfo data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInstanceIdW", SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(nint set, ref DeviceInfo data, StringBuilder id, uint size, out uint required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(nint set, ref DeviceInfo data, uint property, out uint type, byte[] buffer, uint size, out uint required);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
}
