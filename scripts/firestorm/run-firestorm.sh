#!/bin/bash
# Launches Firestorm (firestorm-current -> 7.2.5.81788) in a small 800x500 window with Mesa llvmpipe software rendering (no GPU on this box) and low-performance defaults (seeded by fs-lowperf-settings.py), after making sure the headless PulseAudio null-sink daemon is running (WebRTC voice aborts without an audio device); voice chat is disabled by default (set FS_ENABLE_VOICE=1 to allow it); extra args are passed to the viewer.
export LIBGL_ALWAYS_SOFTWARE=1
export GALLIUM_DRIVER=llvmpipe
export LP_NUM_THREADS=6

# Headless audio: per-user PulseAudio configured in ~/.config/pulse/default.pa (null sink "nullsink" + null source "nullmic").
# ALSA "default" is routed to PulseAudio via ~/.asoundrc (libasound2-plugins).
if ! pulseaudio --check 2>/dev/null; then
    pulseaudio --start --log-target=syslog || echo "WARNING: could not start PulseAudio; WebRTC voice init may abort" >&2
fi

# Low-performance defaults + small window, written into the per-user settings file (only while no viewer runs,
# since a running viewer rewrites that file on exit). Set FS_SKIP_LOWPERF=1 to skip.
if [ "${FS_SKIP_LOWPERF:-0}" != "1" ] && ! pgrep -x do-not-directly >/dev/null 2>&1; then
    python3 /home/box/viewers/fs-lowperf-settings.py || echo "WARNING: could not seed low-performance settings" >&2
fi
WINDOW_ARGS=(--set WindowWidth 800 --set WindowHeight 500 --set WindowMaximized FALSE --set FullScreen FALSE)

VOICE_ARGS=()
if [ "${FS_ENABLE_VOICE:-0}" != "1" ]; then
    VOICE_ARGS=(--set EnableVoiceChat FALSE)
fi

cd /home/box/viewers/firestorm-current || exit 1
exec ./firestorm "${WINDOW_ARGS[@]}" "${VOICE_ARGS[@]}" "$@"
