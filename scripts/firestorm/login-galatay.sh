#!/bin/bash
# Auto-logs Galatay Resident into Second Life with Firestorm (800x500 window, low-performance defaults, llvmpipe software GL, headless PulseAudio, voice off by default), reading the password from $SECONDLIFE_PASSWORD or, if that is empty, from the box secret store /home/box/agent-data/box-secrets.json (entry SECONDLIFE_PASSWORD; never echoed); usage: login-galatay.sh [--dry-run] [last|home|Region/x/y/z|secondlife://...|http://maps.secondlife.com/secondlife/...]
#
# How it works: Firestorm's "--login <first> <last> <password>" takes the PLAIN-TEXT password and MD5-hashes it
# in memory (LLLoginHandler::loadSavedUserLoginInfo); the login server gets "$1$"+md5. A pre-hashed "$1$..." value is
# NOT accepted on the command line (it would be hashed again). "--set FSLoginDontSavePassword TRUE --set RememberPassword FALSE"
# stops Firestorm from saving the credential to user_settings/bin_conf.dat (the "--logindontsavepassword" switch is
# commented out of cmd_line.xml in 7.2.4 and aborts startup with "unrecognised option"). The start location goes in "--url", which must be the LAST option.
# The password is never written to disk or logged by this script, but it IS visible in the argv of the firestorm
# processes (/proc/<pid>/cmdline, ps) while the viewer runs.
set +x
set -u

FS_DIR=/home/box/viewers/firestorm-current   # -> Phoenix-Firestorm-Releasex64_AVX2-7-2-5-81788
FIRST=Galatay
LAST=Resident
GRID=util.agni.lindenlab.com   # Second Life main grid (Agni), as named in Firestorm's grid manager

DRY_RUN=0
if [ "${1:-}" = "--dry-run" ]; then DRY_RUN=1; shift; fi
if [ "${1:-}" = "-h" ] || [ "${1:-}" = "--help" ]; then
    sed -n '2p' "$0" | sed 's/^# //'; exit 0
fi

LOCATION="${1:-last}"
if [ $# -gt 1 ]; then echo "ERROR: too many arguments (expected at most one start location)." >&2; exit 2; fi

# Fallback: the env var is not always injected, so load it in-process from the box secret store.
# The python helper prints ONLY the value, into the command substitution (never to the terminal/log); errors are silenced.
BOX_SECRETS_FILE=/home/box/agent-data/box-secrets.json
if [ -z "${SECONDLIFE_PASSWORD:-}" ] && [ -r "$BOX_SECRETS_FILE" ]; then
    SECONDLIFE_PASSWORD="$(python3 - "$BOX_SECRETS_FILE" 2>/dev/null <<'PYEOF'
import json, sys
KEY = "SECONDLIFE_PASSWORD"
def find(o):
    if isinstance(o, dict):
        v = o.get(KEY)
        if isinstance(v, str) and v:
            return v
        if o.get("name") == KEY or o.get("key") == KEY:
            v = o.get("value")
            if isinstance(v, str) and v:
                return v
        for v in o.values():
            r = find(v)
            if r:
                return r
    elif isinstance(o, list):
        for v in o:
            r = find(v)
            if r:
                return r
    return None
try:
    with open(sys.argv[1]) as f:
        r = find(json.load(f))
except Exception:
    r = None
if r:
    sys.stdout.write(r)
PYEOF
)"
fi

if [ -z "${SECONDLIFE_PASSWORD:-}" ]; then
    echo "ERROR: SECONDLIFE_PASSWORD is empty or not set in this process's environment or the box secret store; refusing to log in." >&2
    echo "       (It was also not found in $BOX_SECRETS_FILE under the key/name SECONDLIFE_PASSWORD.)" >&2
    exit 1
fi
case "$SECONDLIFE_PASSWORD" in
    -*) echo "ERROR: the password starts with '-', which Firestorm's command-line parser would treat as an option; refusing." >&2; exit 1 ;;
esac

# Normalise the start location: 'last'/'home' pass through; a bare Region/x/y/z becomes secondlife://Region/x/y/z
case "$LOCATION" in
    last|home) ;;
    secondlife://*|http://*|https://*|uri:*) ;;
    *) LOCATION="secondlife://${LOCATION// /%20}" ;;
esac

VIEWER_ARGS=(--login "$FIRST" "$LAST" "$SECONDLIFE_PASSWORD" --grid "$GRID"
             --set FSLoginDontSavePassword TRUE --set RememberPassword FALSE   # this build has the --logindontsavepassword switch commented out of cmd_line.xml
             --set FSShowWhitelistReminder FALSE
             --set CrashSettings.CrashSubmitBehavior 2
             --set WindowWidth 800 --set WindowHeight 500 --set WindowMaximized FALSE --set FullScreen FALSE)   # 2 = never send: disables Breakpad/BugSplat minidumps (which could capture argv/memory)
if [ "${FS_ENABLE_VOICE:-0}" != "1" ]; then
    VIEWER_ARGS+=(--set EnableVoiceChat FALSE)
fi
# "--url last" / "--url home" make initConfiguration() fail silently in 7.2.4 (viewer quits at once), so
# last/home go through the LoginLocation setting instead; only real SLURLs use --url.
case "$LOCATION" in
    last|home) VIEWER_ARGS+=(--set LoginLocation "$LOCATION") ;;
    *) VIEWER_ARGS+=(--url "$LOCATION") ;;   # must stay last. KNOWN BROKEN 2026-09-25: any --url (or CmdLineLoginLocation) value also makes initConfiguration() fail; log in with last/home and teleport instead
esac

if [ "$DRY_RUN" = "1" ]; then
    printf 'DRY RUN (password masked). Would run in %s:\n  ./firestorm' "$FS_DIR"
    for a in "${VIEWER_ARGS[@]}"; do
        if [ "$a" = "$SECONDLIFE_PASSWORD" ]; then printf ' %s' '********'; else printf ' %q' "$a"; fi
    done
    printf '\n  env: LIBGL_ALWAYS_SOFTWARE=1 GALLIUM_DRIVER=llvmpipe LP_NUM_THREADS=6, DISPLAY=%s\n' "${DISPLAY:-<unset>}"
    exit 0
fi

# A running viewer would receive the --url hand-off and/or conflict with a second instance.
if pgrep -x do-not-directly >/dev/null 2>&1; then
    echo "ERROR: a Firestorm viewer is already running; close it first." >&2; exit 3
fi
if [ -z "${DISPLAY:-}" ]; then echo "ERROR: DISPLAY is not set (use your own agent display)." >&2; exit 4; fi

# Low-performance defaults + small window into the per-user settings file (no viewer is running at this point).
if [ "${FS_SKIP_LOWPERF:-0}" != "1" ]; then
    python3 /home/box/viewers/fs-lowperf-settings.py || echo "WARNING: could not seed low-performance settings" >&2
fi

export LIBGL_ALWAYS_SOFTWARE=1
export GALLIUM_DRIVER=llvmpipe
export LP_NUM_THREADS=6

# Headless audio (WebRTC voice init aborts without an audio device); config in ~/.config/pulse/default.pa
if ! pulseaudio --check 2>/dev/null; then
    pulseaudio --start --log-target=syslog || echo "WARNING: could not start PulseAudio; WebRTC voice init may abort" >&2
fi

cd "$FS_DIR" || exit 1
exec ./firestorm "${VIEWER_ARGS[@]}"
