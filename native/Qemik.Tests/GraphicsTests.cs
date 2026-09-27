using System.Text.Json.Nodes;
using Qemik.Core;
using Xunit;

namespace Qemik.Tests;
public sealed class GraphicsTests
{
    [Fact]
    public void NativeGpuPresetPreservesDisksAndAllUnrelatedSettings()
    {
        var vm = new VmConfig { Guest = "Linux", Accelerator = "whpx", Audio = "intel-hda", SharedClipboard = true,
            Uefi = true, FirmwareCode = "firmware.fd", Drives = [new() { Path = "installed.qcow2" }],
            PortForwards = [new() { HostPort = 2225 }], ExtraArguments = "-S" };
        var before = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(vm))!;
        GraphicsProfiles.UseNativeGpu(vm);
        Assert.Equal("sdl", vm.Display); Assert.Equal("virtio-vga-gl", vm.Video);
        var after = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(vm))!;
        before.AsObject().Remove("Display"); before.AsObject().Remove("Video");
        after.AsObject().Remove("Display"); after.AsObject().Remove("Video");
        Assert.True(JsonNode.DeepEquals(before, after));
        var command = QemuCommand.Build(vm, new Preferences { QemuDirectory = "engine", LibraryDirectory = "library" });
        Assert.Contains("sdl,gl=on,window-close=off", command.Arguments);
        Assert.DoesNotContain("-vnc", command.Arguments); Assert.DoesNotContain("egl-headless", command.Arguments);
        GraphicsProfiles.UseIntegratedDisplay(vm);
        Assert.Equal("qemik", vm.Display); Assert.Equal("virtio-vga", vm.Video);
        Assert.Throws<InvalidOperationException>(() => GraphicsProfiles.UseNativeGpu(new VmConfig { Guest = "Windows" }));
    }
}
