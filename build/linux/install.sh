#!/bin/sh
# Installs amptray-monitor as a systemd service. Run as root from the unpacked tarball:
#   sudo ./install.sh
# Files: /opt/amptray (program), /var/lib/amptray (settings, logs), /etc/systemd/system/amptray-monitor.service,
# /etc/udev/rules.d/70-amptray.rules. Running it again upgrades in place; ./uninstall.sh removes everything.
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
for f in amptray-monitor amptray-monitor.service 70-amptray.rules; do
    [ -f "$here/$f" ] || { echo "Missing $f next to install.sh" >&2; exit 1; }
done

systemctl stop amptray-monitor 2>/dev/null || true

install -d -m 755 /opt/amptray /var/lib/amptray
install -m 755 "$here/amptray-monitor" /opt/amptray/amptray-monitor
[ -f "$here/LICENSE" ] && install -m 644 "$here/LICENSE" /opt/amptray/LICENSE
ln -sf /opt/amptray/amptray-monitor /usr/local/bin/amptray-monitor

rm -f /etc/udev/rules.d/99-amptray.rules   # the name an early build used
install -m 644 "$here/70-amptray.rules" /etc/udev/rules.d/70-amptray.rules
if command -v udevadm >/dev/null 2>&1; then
    udevadm control --reload-rules 2>/dev/null || true
    udevadm trigger --subsystem-match=tty 2>/dev/null || true
fi

install -m 644 "$here/amptray-monitor.service" /etc/systemd/system/amptray-monitor.service
systemctl daemon-reload
systemctl enable --now amptray-monitor

echo
echo "amptray-monitor is installed and running."
echo "  Settings:  /var/lib/amptray/service.json   (re-read when it changes; shutdown rules stay off until \"Enabled\": true)"
echo "  Log:       /var/lib/amptray/service.log     CSV logs: /var/lib/amptray/Logs (when LogEnabled is true)"
echo "  Check:     sudo amptray-monitor --probe     Status: systemctl status amptray-monitor"
