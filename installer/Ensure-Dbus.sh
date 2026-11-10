#!/bin/sh
# Check the actual system bus; a PID file alone does not prove it responds.
bus_ready() {
    timeout 4s dbus-send --system --print-reply --reply-timeout=2000 \
        --dest=org.freedesktop.DBus /org/freedesktop/DBus \
        org.freedesktop.DBus.Peer.Ping >/dev/null 2>&1
}
if bus_ready; then
    exit 0
fi
# Start only. Never restart an existing daemon or remove its socket/PID file.
timeout -k 2s 12s service dbus start
if bus_ready; then
    printf '%s\n' 'D-Bus is responding after start.'
    exit 0
fi
printf '%s\n' 'The Ubuntu system D-Bus did not respond after service dbus start.' >&2
exit 1
