#!/bin/sh
# Removes amptray-monitor. Settings and logs in /var/lib/amptray stay unless --purge is given.
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

rm -f /usr/local/bin/amptray-monitor
rm -rf /opt/amptray

if [ "${1:-}" = "--purge" ]; then
    rm -rf /var/lib/amptray
    echo "amptray-monitor removed, including settings and logs."
else
    echo "amptray-monitor removed. Settings and logs are still in /var/lib/amptray (use --purge to delete them)."
fi
