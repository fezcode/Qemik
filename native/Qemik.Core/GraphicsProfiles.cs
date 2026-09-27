namespace Qemik.Core;

public static class GraphicsProfiles
{
    public static bool SupportsNativeGpu(VmConfig vm) => vm.Guest == "Linux" && vm.Architecture is "x86_64" or "i386";
    public static void UseNativeGpu(VmConfig vm)
    {
        if (!SupportsNativeGpu(vm)) throw new InvalidOperationException("This GPU preset requires an x86 Linux guest with Mesa/VirGL drivers.");
        // Native SDL uses the host OpenGL driver and avoids EGL-headless's Windows
        // scanout failure and the integrated viewer's CPU framebuffer copies.
        vm.Video = "virtio-vga-gl";
        vm.Display = "sdl";
    }
    public static void UseIntegratedDisplay(VmConfig vm)
    {
        vm.Video = vm.Architecture is "x86_64" or "i386" ? "virtio-vga" : "virtio-gpu-pci";
        vm.Display = "qemik";
    }
}
