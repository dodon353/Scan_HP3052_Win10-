#!/bin/sh
# Ask the system bus itself for its registered names. Keep diagnostics on error.
bus_ready() {
    probe_output=$(timeout 4s dbus-send --system --print-reply --reply-timeout=2000 \
        --dest=org.freedesktop.DBus /org/freedesktop/DBus \
        org.freedesktop.DBus.ListNames 2>&1)
    probe_status=$?
    [ "$probe_status" -eq 0 ]
}
if bus_ready; then
    exit 0
fi
printf 'Initial D-Bus probe failed (exit %s): %s\n' "$probe_status" "$probe_output" >&2
# Do not restart a potentially active shared service or remove its PID/socket.
timeout -k 2s 12s service dbus start
if bus_ready; then
    printf '%s\n' 'D-Bus is responding after start.'
    exit 0
fi
printf 'D-Bus did not respond after start (exit %s): %s\n' "$probe_status" "$probe_output" >&2
exit 1
