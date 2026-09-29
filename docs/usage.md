# Using Qemik

[Back to the README](../README.md)

## Engine and installation media

The **QEMU engine** page discovers a configured or installed engine and lists its
location, version, emulators, and accelerator support. **Check available release**
retrieves Windows installer metadata. Downloading and verifying never executes
an installer automatically; **Open QEMU Setup** launches the publisher's wizard.

Windows builds come from Stefan Weil's site, linked by QEMU's download page,
and can include development builds. Checksums verify bytes against publisher
HTTPS metadata, not an independent signature. Cancellation is not treated as
success merely because an older QEMU installation is present.

**Download an OS** supports Ubuntu LTS, Fedora, Mint, openSUSE, and Debian.
Downloads support HTTPS mirrors, pause/resume, and SHA-256 verification. A
publisher failure is reported while other entries remain usable. openSUSE
downloads pin a rolling snapshot. Network installers need guest internet access.
**Show in Explorer** reveals downloaded images. For Windows or another OS,
obtain an ISO from its publisher and attach it in **Drives**.

## After installation

Use **Mounted disks → Eject** when the installer asks to remove its installation
medium. Live changes apply to that QEMU session. After shutdown, detach the ISO
in **Options → Edit settings → Drives** and set **Boot → Boot order** to `c`.
Detaching preserves the ISO. Do not change the system-disk controller during
installation; Windows needs VirtIO drivers before switching relevant devices.

Hardware edits require a full shutdown/start. **View settings** on a running
machine shows its startup configuration and current mounted media; saved values
for the next start are not presented as active hardware. Reopen it to refresh
media. The Advanced page includes the actual launch command.

## Display, keyboard, and graphics

The integrated viewer needs no external display application. It provides
keyboard/pointer input, fullscreen, control actions, and resolution requests.
**Ctrl+Alt+G** releases input in this viewer. Host-reserved shortcuts remain
controlled by Windows; use the toolbar for **Ctrl+Alt+Del**.

For compatible x86 Linux guests, shut down, select **Display → Use host GPU
(custom window)**, save, and start again. The preset chooses `virtio-vga-gl`
with native SDL/OpenGL embedded in Qemik. Click the guest or **Capture keyboard**
to type. Toolbar clicks or switching away release held keys. **Use integrated
2D display** restores the framebuffer viewer.

The GPU path has been checked with an installed Ubuntu Wayland guest reporting
a hardware-backed VirGL renderer. Support depends on the host driver, guest
Mesa stack, and QEMU build. Detecting a GPU alone does not establish working
guest acceleration. This is not physical GPU passthrough or CUDA. The tested
Windows `egl-headless` backend failed guest scanout, so the preset uses SDL/OpenGL.

The button immediately left of Minimize toggles the toolbar and bottom status
information together. The title bar stays visible, and the guest receives the
extra display area. Closing a running guest window asks **Do you want to force close?**
Choose **Force close** to immediately shut down the VM and close its display, or
**Cancel** to keep it running. Unsaved guest work is lost on force close.

## Resolution and performance

The GPU window forwards size changes automatically. The integrated viewer
requests a matching physical-pixel resolution, accounting for Windows scaling,
with a cap up to 3840×2160. The guest driver must accept the request.

GNOME can retain a saved resolution. **Auto-fit setup** in the integrated viewer
provides a one-time guest command for a desktop-user helper that follows the
VirtIO preferred mode through GNOME DisplayConfig. It supports one display up
to 4K. Disable it by removing its `qemik-display-fit.desktop` guest autostart
entry and logging out.

Use WHPX for compatible x86 guests when supported by Windows and QEMU. TCG
emulates the CPU in software and can make desktop installers very slow. Leave
CPU and memory capacity for Windows. Increasing virtual CPUs or resolution
does not necessarily improve performance; smaller display sizes reduce rendering
work. VirtIO storage and networking require compatible guest drivers.

## Clipboard and sound

Enable **Sharing → Share text clipboard with the guest**, then fully shut down
and start the VM if it was started without that channel. Reconnect or an in-guest
reboot cannot add missing virtual hardware. In Ubuntu, install the agent:

```sh
sudo apt update
sudo apt install spice-vdagent
```

Log out and back in. **Share text clipboard** controls synchronization while
the guest window is active. The GPU channel retries its handshake during boot.
Synthetic Unicode text has been verified in both directions in an Ubuntu Wayland
session; other desktops depend on their agent. The limit is 512 KiB of UTF-8
text. Images, files, and rich text are not supported.

Linux and Windows templates enable Intel HD Audio playback. For existing VMs,
choose **Sound → intel-hda** while stopped, then start again. Playback uses
Windows DirectSound and the default output. Microphone capture is separate and
off by default. Guest output selection may be needed. Audio-device startup tests
do not themselves verify audible playback.

## Shared folders

Open **Shared folders**, choose a host folder, name it, and select read-only or
writable access. Paste its WebDAV address into Ubuntu Files using Ctrl+L.
No Windows password or administrator prompt is needed. Shared (user) networking
must be enabled without isolation, and Qemik must remain open.

The private link grants the selected access to anyone who possesses it on the
host or its guests; it is not bound to a VM identity. Do not publish these links.
**Stop sharing** revokes access without deleting files. Symlinks and Windows
reparse points are excluded. Basic file operations and ranged downloads are
supported; WebDAV locks and POSIX filesystem semantics are not.

**Previous Windows SMB shares** manages older authenticated shares. Creating
a WebDAV share does not silently remove an existing SMB share.

## Blueprints and storage

After a full shutdown, **Set as blueprint** makes a VM a reusable starting point.
**Create VM from blueprint** produces independent QCOW2 copies, fresh IDs/MAC
addresses, and private firmware variables. A batch supports up to 100 copies.
The source cannot start until **Use as regular VM** is selected.

Copies retain guest accounts and hostnames. ISOs remain shared read-only;
fixed port forwards, TCP serial endpoints, and extra arguments are cleared to
avoid conflicts. Host-folder shares are not duplicated. Completed copies remain
if a later copy fails or is canceled.

Offline QCOW2 snapshots apply per disk. They exclude RAM, other disks, firmware
variables, and host shares. Live ISO replacement/ejection is supported; locked
media cannot be forcibly ejected, and disk-hardware changes need shutdown.

Removing a library entry keeps files by default. Optional deletion presents a
review, protects shared references and backing chains, and checks eligibility
again before deleting. External references unknown to the library cannot be
discovered. Never boot the same writable disk in multiple VMs.

## Local data and diagnostics

The default library is under the current Windows user's local application data,
in **Fezcode → Qemik**. `library.db` holds configurations and preferences. Machine
data includes logs, generated launch commands, and private firmware variables.
Downloaded media is cached separately; disks may live elsewhere. `--data-dir`
selects an isolated library. Back up configurations and their referenced disks.

The app uses a library lock and same-user activation pipe; reopening the same
library activates the existing window. QMP, VNC, and sharing endpoints bind to
loopback. Other local processes can reach unauthenticated endpoints, so these
are not authenticated remote-management services.

The CLI provides catalog, image preparation, and diagnostic commands.
`prepare-ubuntu` downloads/verifies an installer and creates a VM without booting
it. `diagnose-boot` uses a disposable ISO-only guest and never attaches existing
writable guest disks. Run the CLI with `help` for the available options.

## Validation

```powershell
.\build.ps1 -Test

# Select a QEMU installation to enable disposable integration checks.
$env:QEMIK_QEMU_DIR = '<QEMU installation folder>'
dotnet test native/Qemik.Tests/Qemik.Tests.csproj
```

Tests cover persistence, command/path validation, image checksums, QMP,
clipboard/resize protocols, settings, storage protections, and UI workflows.
Offscreen captures go under `artifacts/screenshots`. Some guest, download, and
host checks require additional opt-in fixtures; skipped checks are not passes.
Do not reuse arbitrary test captures in public docs: they may show local paths.

The README images are fresh renders of actual application views with fictional
machines and sample catalog metadata. They contain no captured host desktop,
account names, device identifiers, personal directories, or sharing links.

## References

- [QEMU downloads](https://www.qemu.org/download/) and [Windows builds](https://qemu.weilnetz.de/w64/)
- [QEMU invocation](https://www.qemu.org/docs/master/system/invocation.html)
- [Windows Hypervisor Platform](https://www.qemu.org/docs/master/system/whpx.html)
- [VirtIO GPU backends](https://www.qemu.org/docs/master/system/devices/virtio/virtio-gpu.html)
- [SPICE guest-agent setup](https://spice.pages.freedesktop.org/spice-space/spice-user-manual.html)
- [UTM documentation](https://docs.getutm.app/)
