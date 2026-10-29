#!/bin/sh
# Removes AmpTray (the service and the desktop widget). Settings and logs in /var/lib/amptray stay
# unless --purge is given; the widget's own settings live in each user's ~/.config/AmpTray.
#   sudo ./uninstall.sh [--purge]
set -eu

if [ "$(id -u)" -ne 0 ]; then
    echo "Please run as root: sudo ./uninstall.sh" >&2
    exit 1
fi

systemctl disable --now amptray-monitor 2>/dev/null || true
rm -f /etc/systemd/system/amptray-monitor.service
systemctl daemon-reload 2>/dev/null || true

rm -f /etc/udev/rules.d/70-amptray.rules /etc/udev/rules.d/99-amptray.rules
if command -v udevadm >/dev/null 2>&1; then
    udevadm control --reload-rules 2>/dev/null || true
fi
rm -f /etc/tmpfiles.d/amptray.conf
rm -rf /run/amptray

rm -f /usr/share/applications/amptray.desktop /usr/share/icons/hicolor/256x256/apps/amptray.png
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications 2>/dev/null || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q /usr/share/icons/hicolor 2>/dev/null || true

rm -f /usr/local/bin/amptray-monitor /usr/local/bin/amptray
rm -rf /opt/amptray

if [ "${1:-}" = "--purge" ]; then
    rm -rf /var/lib/amptray
    getent group amptray >/dev/null 2>&1 && groupdel amptray 2>/dev/null || true
    echo "AmpTray removed, including the service's settings and logs."
else
    echo "AmpTray removed. The service's settings and logs are still in /var/lib/amptray (use --purge to delete them)."
fi
