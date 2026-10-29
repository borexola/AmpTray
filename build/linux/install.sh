#!/bin/sh
# Installs AmpTray for Linux: the amptray-monitor systemd service and, when the tarball has it, the
# amptray desktop widget. Run as root from the unpacked tarball:
#   sudo ./install.sh
# Files: /opt/amptray (programs and these scripts), /var/lib/amptray (service settings and logs,
# group amptray), /etc/systemd/system/amptray-monitor.service, /etc/udev/rules.d/70-amptray.rules,
# /etc/tmpfiles.d/amptray.conf (the shared device lock in /run/amptray), /usr/share/applications and
# /usr/share/icons for the desktop entry. Running it again upgrades in place; ./uninstall.sh removes everything.
set -eu

if [ "$(id -u)" -ne 0 ]; then
    echo "Please run as root: sudo ./install.sh" >&2
    exit 1
fi
if ! command -v systemctl >/dev/null 2>&1; then
    echo "systemd is required (systemctl not found)." >&2
    exit 1
fi

here=$(cd "$(dirname "$0")" && pwd)
for f in amptray-monitor amptray-monitor.service 70-amptray.rules amptray.conf; do
    [ -f "$here/$f" ] || { echo "Missing $f next to install.sh" >&2; exit 1; }
done
# Re-running from /opt/amptray itself (the desktop app's "Run as a service" switch does this): skip the copies.
in_place=0
[ "$here" -ef /opt/amptray ] && in_place=1
# The user who ran sudo gets the service's settings folder and the desktop app wired up.
user=${SUDO_USER:-}
[ "$user" = root ] && user=""

systemctl stop amptray-monitor 2>/dev/null || true

# --- Programs ---------------------------------------------------------------------------------
install -d -m 755 /opt/amptray
if [ $in_place -eq 0 ]; then
    install -m 755 "$here/amptray-monitor" /opt/amptray/amptray-monitor
    [ -f "$here/amptray" ] && install -m 755 "$here/amptray" /opt/amptray/amptray
    [ -f "$here/LICENSE" ] && install -m 644 "$here/LICENSE" /opt/amptray/LICENSE
    for f in install.sh uninstall.sh amptray-monitor.service 70-amptray.rules amptray.conf amptray.desktop amptray.png; do
        [ -f "$here/$f" ] && install -m 644 "$here/$f" "/opt/amptray/$f"
    done
    chmod 755 /opt/amptray/install.sh /opt/amptray/uninstall.sh
fi
ln -sf /opt/amptray/amptray-monitor /usr/local/bin/amptray-monitor
[ -f /opt/amptray/amptray ] && ln -sf /opt/amptray/amptray /usr/local/bin/amptray

# --- Settings folder: root writes it, members of "amptray" (the desktop app) may too -----------
getent group amptray >/dev/null 2>&1 || groupadd --system amptray
install -d -m 2775 -o root -g amptray /var/lib/amptray
chgrp -R amptray /var/lib/amptray 2>/dev/null || true
chmod -R g+rwX /var/lib/amptray 2>/dev/null || true
relogin=0
if [ -n "$user" ]; then
    if ! id -nG "$user" | tr ' ' '\n' | grep -qx amptray; then
        usermod -aG amptray "$user"
        relogin=1
    fi
    # The group takes effect at the next login; an ACL makes the folder usable right away.
    if command -v setfacl >/dev/null 2>&1; then
        setfacl -R -m "u:$user:rwX" -m "d:u:$user:rwX" /var/lib/amptray 2>/dev/null && relogin=0 || true
    fi
fi

# --- Device access: udev rule and the shared lock ---------------------------------------------
rm -f /etc/udev/rules.d/99-amptray.rules   # the name an early build used
install -m 644 "$here/70-amptray.rules" /etc/udev/rules.d/70-amptray.rules
if command -v udevadm >/dev/null 2>&1; then
    udevadm control --reload-rules 2>/dev/null || true
    udevadm trigger --subsystem-match=tty 2>/dev/null || true
fi
install -m 644 "$here/amptray.conf" /etc/tmpfiles.d/amptray.conf
systemd-tmpfiles --create /etc/tmpfiles.d/amptray.conf 2>/dev/null || true

# --- Desktop entry (when the widget is included) ----------------------------------------------
if [ -f /opt/amptray/amptray ] && [ -f "$here/amptray.desktop" ]; then
    install -Dm644 "$here/amptray.desktop" /usr/share/applications/amptray.desktop
    [ -f "$here/amptray.png" ] && install -Dm644 "$here/amptray.png" /usr/share/icons/hicolor/256x256/apps/amptray.png
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications 2>/dev/null || true
    command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q /usr/share/icons/hicolor 2>/dev/null || true
fi

# --- Service ----------------------------------------------------------------------------------
install -m 644 "$here/amptray-monitor.service" /etc/systemd/system/amptray-monitor.service
systemctl daemon-reload
systemctl enable --now amptray-monitor

echo
echo "amptray-monitor is installed and running."
echo "  Settings:  /var/lib/amptray/service.json   (re-read when it changes; shutdown rules stay off until \"Enabled\": true)"
echo "  Log:       /var/lib/amptray/service.log     CSV logs: /var/lib/amptray/Logs (when LogEnabled is true)"
echo "  Check:     sudo amptray-monitor --probe     Status: systemctl status amptray-monitor"
if [ -f /opt/amptray/amptray ]; then
    echo "  Widget:    amptray  (also in the application menu as AmpTray)"
fi
if [ $relogin -eq 1 ]; then
    echo
    echo "Note: $user was added to the amptray group; log out and back in before the widget can change the service's settings."
fi
