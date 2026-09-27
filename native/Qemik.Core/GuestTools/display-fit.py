#!/usr/bin/env python3
"""Follow QEMU's preferred VirtIO monitor mode in a single-monitor GNOME session."""
import os
import pathlib
import subprocess
import sys


def install():
    source = pathlib.Path(__file__)
    destination = pathlib.Path.home() / '.local/share/qemik/display-fit.py'
    destination.parent.mkdir(parents=True, exist_ok=True)
    if source.resolve() != destination.resolve():
        destination.write_bytes(source.read_bytes())
    autostart = pathlib.Path.home() / '.config/autostart/qemik-display-fit.desktop'
    autostart.parent.mkdir(parents=True, exist_ok=True)
    # Desktop Exec quoting: no shell expansion or user-supplied command text.
    escaped = str(destination).replace('\\', '\\\\').replace('"', '\\"').replace('`', '\\`').replace('$', '\\$').replace('%', '%%')
    autostart.write_text('[Desktop Entry]\nType=Application\nName=Qemik display fitting\n'
                         'Exec=/usr/bin/python3 "' + escaped + '"\n'
                         'OnlyShowIn=GNOME;Unity;\nX-GNOME-Autostart-enabled=true\n')
    subprocess.Popen(['/usr/bin/python3', str(destination)], start_new_session=True,
                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    print('Qemik auto-fit enabled for this user, now and at login. No administrator access needed.')
    print('To remove: delete ~/.config/autostart/qemik-display-fit.desktop and log out.')


def main():
    import fcntl
    import gi
    gi.require_version('Gio', '2.0')
    from gi.repository import Gio, GLib
    lock = open(pathlib.Path(os.environ.get('XDG_RUNTIME_DIR', '/tmp')) / ('qemik-display-fit-' + str(os.getuid()) + '.lock'), 'w')
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        return
    proxy = Gio.DBusProxy.new_for_bus_sync(Gio.BusType.SESSION, Gio.DBusProxyFlags.NONE, None,
                                         'org.gnome.Mutter.DisplayConfig', '/org/gnome/Mutter/DisplayConfig',
                                         'org.gnome.Mutter.DisplayConfig', None)
    last_applied = None

    def fit():
        nonlocal last_applied
        try:
            serial, monitors, logical, props = proxy.call_sync('GetCurrentState', None, Gio.DBusCallFlags.NONE, 2000, None).unpack()
            # Leave physical monitors, multi-monitor layouts, rotation and mirror configurations alone.
            if len(monitors) != 1 or len(logical) != 1:
                return True
            spec, modes, monitor_props = monitors[0]
            connector, vendor, product, monitor_serial = spec
            if not connector.startswith('Virtual-') or 'QEMU' not in (vendor + product).upper():
                return True
            preferred = next((m for m in modes if m[6].get('is-preferred')), None)
            current = next((m for m in modes if m[6].get('is-current')), None)
            if preferred is None or current is None or preferred[1] > 3840 or preferred[2] > 2160:
                return True
            wanted = (connector, preferred[0])
            if current[0] == preferred[0]:
                last_applied = wanted
                return True
            # Respect manual changes until QEMU advertises a different preferred mode.
            if wanted == last_applied:
                return True
            x, y, scale, transform, primary, monitor_specs, logical_props = logical[0]
            if transform != 0 or len(monitor_specs) != 1:
                return True
            supported_scales = preferred[5]
            if scale not in supported_scales:
                scale = preferred[4]
            options = {}
            if 'layout-mode' in props:
                options['layout-mode'] = GLib.Variant('u', props['layout-mode'])
            parameters = GLib.Variant('(uua(iiduba(ssa{sv}))a{sv})',
                                     (serial, 1, [(0, 0, scale, 0, True, [(connector, preferred[0], {})])], options))
            proxy.call_sync('ApplyMonitorsConfig', parameters, Gio.DBusCallFlags.NONE, 2000, None)
            last_applied = wanted
        except GLib.Error as error:
            print('Waiting for GNOME display configuration:', error, flush=True)
        return True

    fit()
    GLib.timeout_add_seconds(2, fit)
    GLib.MainLoop().run()


if __name__ == '__main__':
    # Check the preinstalled Ubuntu GNOME dependency before installing autostart files.
    import gi
    if '--install' in sys.argv:
        install()
    else:
        main()
