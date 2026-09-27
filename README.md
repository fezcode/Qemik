<p align="center"><img src="native/Qemik.Desktop/Assets/qemik.png" width="100" alt="Qemik bone Q with flowing waves"></p>
<h1 align="center">Qemik</h1>
<p align="center">QEMU + kemik. Virtual machines, with backbone.</p>

Qemik is a native Windows desktop interface for QEMU, inspired by UTM. **Kemik** means **bone** in Turkish; the icon combines an ivory Q and bone-shaped tail with flowing teal and green waves.

It uses Airlift's native technology stack: **C# / .NET 10, Avalonia 12.1.2, and SQLite**. It does not use an Electron or web wrapper.

## Run

The self-contained Windows build is `dist/win-x64/Qemik.exe`. The guest integration update is published separately to `dist/win-x64-polished/Qemik.exe` so an active installation can finish in the previous build. Keep the entire folder together; .NET does not need to be installed separately. QEMU itself is installed or selected from within the app.

```powershell
.\build.ps1 -Test -Publish
.\dist\win-x64\Qemik.exe
```

For development, install the .NET 10 SDK and run `./build.ps1 -Test -Run`. No Node.js dependency is required. To isolate your library, launch `Qemik.exe --data-dir D:\Qemik-TestData`. `--help` and `--version` are also available.

## First machine

1. Open **QEMU engine**. Choose **Check available release**, review the installer metadata, then **Download & verify**. Once verification succeeds, **Open QEMU Setup** launches the publisher's installation wizard. Complete that wizard, then Qemik detects the installed binaries. For a custom path, use **Locate QEMU folder**.
2. Choose **New machine** and a guest template. Configure memory and CPUs in **System**. TCG works without enabling a host hypervisor; WHPX requires Windows Hypervisor Platform and compatible host hardware.
3. In **Drives**, create a QCOW2 disk and attach your operating-system ISO. Windows x86 templates default to IDE disks; install VirtIO guest drivers before using VirtIO disks, the VirtIO SCSI controller, or VirtIO NICs.
4. Configure **Boot** if your OS needs UEFI. **Find bundled UEFI firmware** locates matching EDK2 images in QEMU's share folder. Firmware variables are copied into a private file for each machine.
5. Save and start. The default **qemik** display opens a dedicated guest window with keyboard/mouse input, aspect-preserving scaling, fullscreen, Ctrl+Alt+Del, pause/resume, reset, shutdown and force stop. **Ctrl+Alt+G** releases input. Closing the display leaves the VM running; **Open** brings it back. SDL/GTK and external VNC remain available in Display settings. The guest must support ACPI for a shutdown request to complete.

Choose **Download an OS → Check latest images** for Ubuntu Desktop/Server LTS, Fedora Workstation/KDE, Linux Mint Cinnamon/MATE/Xfce, openSUSE Tumbleweed DVD/network installers, or Debian netinst. Use the distribution filter to browse. Stable versions, sizes and SHA-256 metadata come from publisher sources; openSUSE uses a pinned rolling snapshot. An unavailable publisher reports a warning while other images remain usable. Downloads support HTTPS mirrors, pause/resume and checksum verification. Create a machine with a sparse disk and verified ISO mounted, or use **Download installer** on an existing stopped machine. Network installers need internet inside the guest. **Show in Explorer** selects the downloaded ISO; **Open images folder** opens the cache.

Downloaded x86 guests select WHPX when Windows reports it available and the installed QEMU supports it, otherwise TCG. Modern desktop ISOs can take tens of minutes under TCG software emulation. Start the machine to run the installer; Qemik does not automatically install the guest OS. On the tested host, Ubuntu 26.04.1 reached its language-selection screen in about three minutes using WHPX with the `max` CPU model.

Other operating systems use publisher downloads and locally attached ISOs. The Windows template is a hardware starting point; it does not automatically meet Windows 11's TPM/Secure Boot requirements.

## Implemented

- Persistent, searchable VM library; create, edit, remove, JSON import/export.
- Official Linux catalog with ten installer choices across five distributions, resumable downloads, SHA-256 verification, installer attachment and new-machine provisioning.
- Dedicated guest display over a dynamically assigned loopback VNC endpoint, with keyboard/pointer input, display resize handling, fullscreen and lifecycle controls. No external viewer is required.
- Vector toolbar icons, wrapping guest controls, and an Options shortcut on every library card.
- Force shutdown beside Shut down on running library cards, machine details and guest controls. It immediately terminates QEMU and closes its guest window without waiting for the OS or asking for confirmation; unsaved guest work is lost.
- Optional Fit resolution requests through RFB ExtendedDesktopSize; changed-region framebuffer copying without a full-frame allocation on each update.
- Optional Unicode text clipboard channel with bounded payloads, guest-agent setup instructions and a per-window sharing switch.
- Guests without a clipboard channel show Set up clipboard instead of a disabled switch. Setup saves the preference for the next full shutdown/start; reconnecting does not activate a channel missing from the running VM.
- Live mounted-disk inspection, read-only ISO replacement/ejection and Explorer access. Locked media cannot be forcibly ejected; hard-disk hardware changes require shutdown.
- Removal confirmation with optional permanent deletion of reviewed disks, ISOs and machine files; shared media, backing images and firmware are protected.
- QEMU discovery through configured directories, standard installation paths, registry and PATH. Version, executable location, installed architectures, and runtime capability queries are displayed.
- Live publisher installer resolution, source URL, filename, size, SHA-512, download progress/cancellation, checksum verification, interactive setup launch and post-setup detection. No installer executes just because it was downloaded.
- Settings for identity, architecture, machine, CPU model/features/topology, memory, TCG/WHPX, firmware, boot order/menu, kernel/initrd/arguments, disk images/buses/cache/discard, CD/DVD, display/GPU/fullscreen/VNC, network/NAT/TAP/MAC/forwarding, audio, USB/tablet/passthrough, optional VirtFS sharing, serial, RTC, ephemeral disk writes, reboot behavior and extra QEMU arguments.
- Architecture defaults tested with x86-64, i386, AArch64, ARM, RISC-V 64 and PowerPC emulators. This verifies machine startup and QMP connectivity, not installation of a guest OS on every architecture.
- Start, pause/resume, ACPI shutdown request, reset and force stop. Live state is retained while Qemik is open; close is blocked while machines or engine operations are active.
- Exact PowerShell launch preview, copied/exportable configuration, per-machine QEMU logs, direct process argument passing without a shell.
- Offline per-disk QCOW2 snapshot list/create/restore/delete. Snapshots exclude RAM, other disks, firmware variables and host shares.
- Wave-themed Q-and-bone artwork, rounded PNG and Windows ICO with multiple sizes.
- Airlift-style custom window captions, bundled DM Sans/Manrope typography, grouped vector navigation, centered button labels and fixed page headings. Scrollbars reserve their own space so they cannot cover actions or fields. The engine page can reveal the detected installation folder in Explorer.

## Current boundaries

This is a working initial implementation, **not complete UTM feature parity**. The integrated display uses raw RFB frames and scales the guest's chosen resolution. Fit resolution is on by default and requests a matching physical-pixel size up to 3840×2160 (4K); the guest graphics driver decides whether and when it applies. Host-reserved shortcuts remain host-controlled; Ctrl+Alt+Del is available on the toolbar. Image/file/rich-text clipboard, SPICE WebDAV, automated TPM/Secure Boot provisioning, save/restore of guest RAM, bundled guest tools, multiple NIC/display editors and app self-updates are not implemented. Advanced QEMU arguments cover additional devices, with lifecycle/control-channel options reserved by Qemik.

## Ubuntu after installation

Wait for installation to finish before changing VM hardware or replacing Qemik. When Ubuntu requests removal of the installation medium, use **Mounted disks → Eject** in the updated guest window. Live media changes last for the current QEMU session; after shutdown, detach the ISO in **Options → Edit settings → Drives** and set **Boot → Boot order** to `c`. Detaching preserves the ISO file. Do not detach or change the system disk's controller during installation.

- **CPU and storage:** use WHPX with `max`, 4 vCPUs and 8192 MiB on the tested 6-core/32-GB host. Keep the VirtIO system disk, VirtIO NIC and existing writeback cache. More CPUs/RAM are not automatically faster; leave memory for Windows. The tested disk lives on an NVMe SSD.
- **Host GPU acceleration:** stop an x86 Linux VM, open **Settings → Display → Use host GPU (custom window)**, save, and start it. This selects `virtio-vga-gl` with `sdl,gl=on,window-close=off`, hosting the native OpenGL surface inside Qemik's custom guest window without CPU framebuffer copies. Ubuntu 26.04.1 was booted on a disposable overlay of an installed system and its Mesa renderer reported **`virgl (NVIDIA GeForce RTX 5070 Ti/PCIe/SSE2)`**, OpenGL 4.2, under Wayland. This verifies guest hardware rendering on this host; no FPS benchmark or game compatibility claim is made. VirGL exposes a virtual OpenGL GPU, not physical GPU passthrough or CUDA.
- **GPU display controls:** the custom Qemik guest window hosts QEMU's native OpenGL surface on Windows. Its toolbar provides pause/resume, shutdown/force shutdown, reset, fullscreen, Ctrl+Alt+Del, reconnect, mounted disks, shared folders, and current settings. Closing the custom window detaches and hides the surface without stopping QEMU; **Open** reattaches it. An isolated installed Ubuntu test verified the NVIDIA/VirGL renderer while embedded, window resizing/fullscreen, and close/reopen with QMP still responsive. Window resizing is forwarded to Ubuntu automatically. Click the guest or **Capture keyboard** to direct physical key events through QMP; toolbar clicks and losing focus release held keys. The GPU viewer has an independent virtio-serial text clipboard channel using `spice-vdagent`. **Use integrated 2D display** restores that path.
- **Windows GPU backend limitation:** the installed QEMU's `egl-headless`/ANGLE path failed at Ubuntu scanout (`virtio_gpu_virgl_process_cmd: ctrl 0x103, error 0x1203` with Direct3D). Detecting a host GPU or connecting to the firmware framebuffer does not prove guest acceleration. Native SDL/OpenGL was verified separately and is the GPU preset. The manual video/backend selectors remain available for other engines and hosts.
- **Repeated launches:** reopening the same library activates the existing Qemik window through a same-user local pipe. The library file lock still prevents concurrent writers, including CLI tools. An older build that lacks this activation channel must be closed once before switching builds.
- **Resolution:** **Fit resolution** is on by default. Requests follow window size and Windows display scaling, are debounced, and preserve aspect ratio within 3840×2160 (4K). Higher resolutions require more rendering work; disable fitting or use a smaller window if needed. Qemik retries requests made before the guest driver is ready and reports success only when the framebuffer matches. Ubuntu GNOME may retain a saved resolution: use **Auto-fit setup** in the guest toolbar, run its one-time command inside Ubuntu, then keep **Fit resolution** enabled. The helper follows the VirtIO preferred mode through GNOME DisplayConfig, runs as your desktop user, and supports a single virtual monitor up to 4K. No sudo or SPICE server is needed. Remove `~/.config/autostart/qemik-display-fit.desktop` and log out to disable the helper.
- **Sound:** new Linux and Windows machines enable **intel-hda** playback by default. For an existing machine, fully shut down, choose **Sound → intel-hda**, save and start again. Playback uses Windows DirectSound and the default output. Microphone capture is a separate option and is off by default so missing capture hardware does not produce ADC errors. Select the virtual Intel HD Audio output in Ubuntu Sound settings if needed.
- **Text clipboard:** after shutdown, enable **Sharing → Share text clipboard with the guest**, then start again. Inside Ubuntu run `sudo apt update` and `sudo apt install spice-vdagent`, then log out/in. The guest window can toggle sharing and synchronizes while active. The QEMU bridge carries UTF-8 text up to 512 KiB; it does **not** carry images or files. An isolated installed Ubuntu 26.04.1 Wayland guest verified synthetic Unicode text in both directions through the GPU clipboard channel. Other desktop sessions still depend on their guest-agent support. The bridge retries its handshake while Linux boots.

The display update path now reuses its framebuffer, copies only the changed bounding region and reduces its added pacing delay from 33 ms to 16 ms. At 1080p this removes an 8.3 MB full-screen clone per update. These reduce host-side work; they are not an end-to-end FPS benchmark or a substitute for guest 3D acceleration. While the actual installer was running, WHPX was active, QEMU used about 2.8 logical cores in a three-second sample, and Windows had about 5 GB of free RAM. No running-machine configuration was changed during the audit.

VirtFS/9p sharing, USB host passthrough, audio and accelerators depend on the installed QEMU build and guest drivers. Qemik checks VirtFS and WHPX build support before starting, but host feature enablement or device-driver problems may still fail at launch; see **Logs**. TAP adapters must be provisioned separately. Windows installer management currently targets x64. Other host platforms and architectures are not packaged or validated.

The installer source is Stefan Weil's Windows build site, linked from QEMU's download page. It may publish release candidates or development builds. The checksum verifies bytes against metadata from that same HTTPS publisher, not an independent signature. Qemik leaves licensing and elevation to the original installation wizard. Setup exit codes and detected binaries are shown separately; cancellation does not become a successful install solely because an older QEMU exists.

## Data and safety

Default app data is `%LOCALAPPDATA%\Fezcode\Qemik`. `library.db` stores VM JSON and preferences. `Machines/<id>` stores runtime logs, launch commands and private UEFI variables. `Downloads` stores verified installers. Disk images can live elsewhere. Set the machine data folder before creating machines; Qemik prevents changing it under an existing library.

Removing a machine opens a confirmation dialog and keeps files by default. Opt into permanent deletion to review and delete its attached disks, installer ISOs and runtime files. Files referenced by other library machines (including their backing chains), firmware and linked paths are kept. If another disk's backing chain cannot be inspected, file deletion is disabled. File eligibility is checked again before deletion. References outside the Qemik library cannot be discovered. Detaching a drive preserves its file. Creating a disk never overwrites an existing file. Cancelling settings after creating a disk keeps that disk on disk. Imported configurations receive a fresh ID and MAC address, and advanced arguments are cleared for review; disk paths remain references to the originals. Do not boot the same writable disk in multiple VMs.

`Images/<checksum-prefix>` caches guest ISOs and resumable partial downloads. Checksums come from publisher HTTPS metadata (Mint uses its linked kernel.org checksum mirror); detached GPG signatures are not verified. The optional `Qemik.Cli prepare-ubuntu` command performs the same download/provision flow without starting the guest. `catalog` lists current images; `configure-guest --vm <id>` backs up the VM configuration and selects the dedicated display and available accelerator while the app is closed. `diagnose-boot --vm <id>` runs a disposable ISO-only guest and captures boot screenshots without attaching existing writable guest disks.

QMP, VNC, serial TCP, and forwarded guest ports bind to loopback. Other local processes on the same host can access those endpoints; these are not authenticated remote-management services. Starting a VM reserves a free local port before QEMU binds it; if another process claims it first, startup fails with a log rather than silently choosing a different endpoint.

## Validation

```powershell
.\build.ps1 -Test

# Opt-in tests against an existing QEMU installation (creates disposable test VMs):
$env:QEMIK_QEMU_DIR = 'C:\Program Files\qemu'
dotnet test native/Qemik.Tests/Qemik.Tests.csproj --filter FullyQualifiedName~LiveQemuTests
```

The automated suite covers persistence, path/argument handling, malformed configurations, reserved control arguments, firmware isolation, installer selection/hash verification, failed-download cleanup, QMP event/response handling, protection against disk overwrites, every settings page, and the create/save workflow. Headless Avalonia screenshots are written to `artifacts/screenshots` by `build.ps1 -Test`.

Opt-in download verification uses `QEMIK_DOWNLOAD_TEST=<artifact directory>` and `QEMIK_7ZIP=<7z.exe>` with the `PublisherDownloadVerifiesAndExtractsForIsolatedTesting` test. It downloads and extracts the publisher installer but never runs it or writes installer registry entries.

Validated here against the Windows installer dated 2026-08-11: publisher download/hash, real QEMU lifecycle, six architecture startup profiles, UEFI variable isolation, offline disk snapshots, dedicated guest frame/input/reconnect, and Ubuntu 26.04.1 reaching its installer with WHPX. Added protocol tests cover resize negotiation/forwarded responses, Unicode clipboard exchange and framebuffer reuse. Disposable QEMU guests verify 2D/VirGL framebuffer connections, audio and clipboard channel startup, hard-disk removal rejection, and ISO ejection/replacement with Unicode filenames. Windows short-path aliases work around QEMU builds with ANSI filename handling where aliases are available. Linux catalog metadata and bounded range requests were checked against every new ISO. An isolated installed Ubuntu overlay verified GNOME auto-fit at 1440×900, 3840×2160, and 1152×685, and another verified guest VirGL rendering through the RTX 5070 Ti with native SDL. A further isolated Ubuntu test verified physical Windows keyboard input in the embedded GPU window and synthetic Unicode clipboard exchange in both directions. Installation wizard/UAC, audible playback, USB passthrough, and TAP networking still need interactive machine testing.

## References

- [QEMU downloads](https://www.qemu.org/download/)
- [Windows builds and release history](https://qemu.weilnetz.de/w64/)
- [QEMU invocation reference](https://www.qemu.org/docs/master/system/invocation.html)
- [QMP specification](https://www.qemu.org/docs/master/interop/qmp-spec.html)
- [Windows Hypervisor Platform](https://www.qemu.org/docs/master/system/whpx.html)
- [UTM settings documentation](https://docs.getutm.app/settings-qemu/system/)
- [Fedora release metadata](https://fedoraproject.org/releases.json)
- [Linux Mint downloads](https://linuxmint.com/download.php)
- [openSUSE Tumbleweed](https://get.opensuse.org/tumbleweed/)
- [Debian downloads](https://www.debian.org/distrib/)
- [RFB protocol specification](https://www.rfc-editor.org/rfc/rfc6143)
- [RFB resize and clipboard extensions](https://github.com/rfbproto/rfbproto/blob/master/rfbproto.rst)
- [QEMU VirtIO graphics backends](https://www.qemu.org/docs/master/system/devices/virtio/virtio-gpu.html)
- [QEMU VNC clipboard implementation (text format)](https://gitlab.com/qemu-project/qemu/-/blob/master/ui/vnc-clipboard.c)
- [SPICE guest-agent setup](https://spice.pages.freedesktop.org/spice-space/spice-user-manual.html)

## Shared Windows folders and blueprints

Open **Shared folders** in the guest toolbar, machine details, or Sharing settings. Choose any local Windows folder and a share name, keep read-only access or allow changes, then select **Share folder…**. In Ubuntu Files, press Ctrl+L and paste the shown `dav://10.0.2.2:port/...` address. No Windows password, guest login, administrator prompt, or firewall changes are required. This uses app-managed WebDAV on IPv4 loopback and needs Shared (user) networking without isolation. The private link grants the chosen access to anyone who has it on this host or its guests; it is not bound to a particular VM identity. Qemik must remain open. Shares and addresses persist across app restarts. **Stop sharing…** revokes the link without deleting files. Basic file operations and ranged downloads are supported; WebDAV locks and POSIX filesystem semantics are not. Symlinks and Windows reparse points are excluded. **Previous Windows SMB shares…** lets you inspect, convert to a new read-only password-free link, or remove older authenticated shares. Creating a local share does not remove the older SMB share automatically.

After a full guest shutdown, use **Set as blueprint** on its details page. **Create VM from blueprint** makes independent QCOW2 copies with fresh VM IDs and MAC addresses, including private UEFI variables. Create up to 100 machines per batch and repeat as needed. The source cannot start while marked as a blueprint; **Use as regular VM** restores that ability. Copies retain guest accounts and hostname. ISOs remain shared read-only; fixed host port forwards, TCP serial endpoints, and extra QEMU arguments are cleared to avoid conflicts. Completed copies remain if a later copy fails or is canceled. Host-folder shares belong to the source VM and are not automatically duplicated.

Running machines expose **View settings** from Options and the guest toolbar. The view is read-only and uses a startup configuration snapshot plus a current query of mounted media. All settings sections remain browsable and values can be copied. Changes saved for a later start are not presented as active hardware; the Advanced page includes the actual launch command. Reopen the view to refresh live media.
