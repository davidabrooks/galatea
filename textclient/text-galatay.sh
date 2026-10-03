#!/usr/bin/env bash
# text-galatay.sh - headless Second Life text client for Galatay Resident (LibreMetaverse based).
# Usage: text-galatay.sh start|stop|status|cmd "<command>"|log [n]|check   (start runs the client under a relaunch supervisor)
# Boot autostart: galatay-autostart.sh (triggered by galatay-mcp.sh at box boot) runs 'start' unless run/deliberate-stop exists.
# The password is read by the client process itself from the box secret store
# (/home/box/agent-data/box-secrets.json, key card.SECONDLIFE_PASSWORD). It is never
# placed in argv, env of this script, or any file.
set -euo pipefail
BASE=/home/box/viewers/textclient
APP="$BASE/app/galatay-text"
RUN="$BASE/run"
PIDF="$RUN/galatay-text.pid"
export GT_SOCK="${GT_SOCK:-$RUN/galatay.sock}"
export GT_LOG="${GT_LOG:-/workspace/secondlife/textclient.log}"
export GT_START="${GT_START:-last}"
export GT_LURE_ALLOW="${GT_LURE_ALLOW:-David Nightingale,SophieJeanneLaDouce Resident,d695b17a-6504-4697-a945-0b71c53e4771}"
# sit-height guard (HeightGuard.cs): pinned hover height is POSTed on login/region change; each sit is re-checked
export GT_HOVER_FILE="${GT_HOVER_FILE:-$BASE/hover.txt}"
export GT_SIT_GUARD="${GT_SIT_GUARD:-1}"
# wander greetings (2026-09-27 09:29, David): each avatar at most once per 24 h, David and Sophie included; history permanent
export GT_GREET_REPEAT_HOURS="${GT_GREET_REPEAT_HOURS:-24}"
# webhook debounce (2026-10-02, Webhook.cs): per conversation, POST after this many quiet seconds, at most MAX_S after the first held line
export GT_WEBHOOK_QUIET_S="${GT_WEBHOOK_QUIET_S:-20}" GT_WEBHOOK_MAX_S="${GT_WEBHOOK_MAX_S:-60}"
[[ -f "$BASE/hover.txt" ]] || echo 0 > "$BASE/hover.txt"
CONSOLE="${GT_CONSOLE:-/workspace/secondlife/textclient.console.log}"
export DOTNET_ROOT="${DOTNET_ROOT:-/home/box/.dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_gcServer=0 DOTNET_GCHeapHardLimit=0x20000000
mkdir -p "$RUN" "$(dirname "$GT_LOG")"; chmod 700 "$RUN"

SUPF="$RUN/supervisor.pid"; STOPF="$RUN/stop-requested"; SUPLOG="${GT_SUPLOG:-/workspace/secondlife/textclient.supervisor.log}"
# Deliberate-stop marker (2026-09-26): 'stop' writes it (the client also writes it on a 'logout' command), only a manual
# 'start' clears it. It persists across a box reboot (lives in /home/box); galatay-autostart.sh does nothing while it exists.
MARK="$RUN/deliberate-stop"; export GT_STOP_MARKER="$MARK" GT_STOP_REQUEST="$STOPF"
LOCKF="$RUN/start.lock"
alog() { echo "$(date '+%F %T') [autostart] $*" >> "$GT_LOG"; }
supervising() { [[ -f "$SUPF" ]] && kill -0 "$(cat "$SUPF")" 2>/dev/null && grep -q "text-galatay.sh" "/proc/$(cat "$SUPF")/cmdline" 2>/dev/null; }
running() { [[ -f "$PIDF" ]] && kill -0 "$(cat "$PIDF")" 2>/dev/null && [[ "$(ps -o comm= -p "$(cat "$PIDF")" 2>/dev/null)" == galatay-text* ]]; }

send() {  # send one command line over the unix socket, print the reply
  python3 - "$GT_SOCK" "$1" <<'PY'
import socket, sys
s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM); s.settimeout(90)
try: s.connect(sys.argv[1])
except OSError as e: print("client not reachable:", e); sys.exit(3)
s.sendall(sys.argv[2].replace("\n", " ").encode() + b"\n")
buf = b""
while True:
    try: d = s.recv(65536)
    except socket.timeout: print("(timeout waiting for reply)"); break
    if not d: break
    buf += d
sys.stdout.write(buf.decode("utf-8", "replace"))
PY
}

case "${1:-}" in
  start)
    # idempotent: one start at a time (flock), then pid file + supervisor pid file + pgrep -x galatay-text
    exec 8>"$LOCKF"; flock -w 30 8 || { echo "another start is in progress; not starting a second client" >&2; exit 5; }
    if pgrep -x do-not-directly >/dev/null; then echo "Firestorm is running; refusing (same account would kick it)." >&2; exit 3; fi
    if running; then echo "already running (pid $(cat "$PIDF"))"; exit 0; fi
    if supervising; then echo "supervisor already running (pid $(cat "$SUPF")); client relaunch pending"; exit 0; fi
    if p=$(pgrep -x galatay-text); then echo "a galatay-text process is already running (pid $(echo $p)) but not under this supervisor/pid file; not starting a second one" >&2; exit 4; fi
    if [[ "${GT_AUTOSTART:-0}" == 1 && -f "$MARK" ]]; then echo "deliberate-stop marker present; autostart does not start her ($(head -1 "$MARK"))" >&2; exit 6; fi
    if [[ -f "$MARK" ]]; then alog "deliberate-stop marker cleared by a manual start (was: $(head -1 "$MARK"))"; rm -f "$MARK"; fi
    rm -f "$STOPF"; : > "$CONSOLE"
    setsid nohup "$0" supervise >>"$SUPLOG" 2>&1 < /dev/null 8>&- &
    echo $! > "$SUPF"
    for _ in $(seq 1 120); do
      if grep -q "\[ready\]" "$CONSOLE" 2>/dev/null; then grep -E "\[login\]" "$CONSOLE" | tail -1; echo "started (pid $(cat "$PIDF"), supervisor pid $(cat "$SUPF"))"; exit 0; fi
      if ! supervising; then echo "supervisor exited:"; tail -5 "$SUPLOG"; tail -5 "$CONSOLE"; exit 1; fi
      sleep 1
    done
    echo "still starting after 120 s; see $CONSOLE / $SUPLOG"; exit 1 ;;
  supervise)
    # Relaunch loop (2026-09-26): relaunches the client after an unexpected exit (non-zero code, e.g. 75 = watchdog /
    # NetworkTimeout, or killed); a clean exit (code 0: logout command / stop / region-restart give-up) ends it.
    # Backoff 15, 30, 60, 120, 240 s; gives up after more than 5 relaunches within an hour.
    # A hang monitor kills the client (its exact pid only) if run/heartbeat is older than 120 s on 2 checks 15 s apart.
    set +e
    slog() { echo "$(date '+%F %T') [supervisor] $*" | tee -a "$GT_LOG"; }
    relaunches=(); n=0
    while :; do
      [[ -f "$STOPF" ]] && { slog "stop requested: not (re)launching"; break; }
      now=$(date +%s); keep=(); for t in "${relaunches[@]}"; do (( now - t < 3600 )) && keep+=("$t"); done; relaunches=("${keep[@]}")
      if (( n > 0 )); then
        if (( ${#relaunches[@]} >= 5 )); then slog "more than 5 relaunches within an hour: giving up (start it again by hand)"; break; fi
        back=$(( 15 * (1 << ${#relaunches[@]}) )); (( back > 300 )) && back=300
        slog "relaunching in ${back} s (relaunch $(( ${#relaunches[@]} + 1 )) in the last hour)"
        sleep "$back"; [[ -f "$STOPF" ]] && { slog "stop requested during backoff"; break; }
        relaunches+=("$(date +%s)")
        [[ -f "$CONSOLE" ]] && mv -f "$CONSOLE" "$CONSOLE.prev"
      fi
      n=$(( n + 1 ))
      rm -f "$RUN/heartbeat"
      "$APP" >>"$CONSOLE" 2>&1 < /dev/null &
      child=$!; echo "$child" > "$PIDF"; slog "client started (pid $child, launch $n)"
      ( stale=0; t0=$(date +%s)
        while kill -0 "$child" 2>/dev/null && [[ "$(ps -o stat= -p "$child" 2>/dev/null)" != Z* ]]; do
          sleep 15
          (( $(date +%s) - t0 < 180 )) && continue
          hb=$(stat -c %Y "$RUN/heartbeat" 2>/dev/null || echo 0)
          if (( $(date +%s) - hb > 120 )); then stale=$(( stale + 1 )); else stale=0; fi
          if (( stale >= 2 )); then
            echo "$(date '+%F %T') [supervisor] heartbeat file stale > 120 s: killing hung client pid $child" | tee -a "$GT_LOG"
            kill -TERM "$child" 2>/dev/null; sleep 10; kill -KILL "$child" 2>/dev/null; break
          fi
        done ) &
      mon=$!
      wait "$child"; rc=$?
      kill "$mon" 2>/dev/null; wait "$mon" 2>/dev/null
      rm -f "$PIDF"
      if [[ -f "$STOPF" ]]; then slog "client exited (code $rc) after a stop request"; break; fi
      if (( rc == 0 )); then slog "client exited cleanly (code 0: logout); not relaunching"; break; fi
      slog "client exited unexpectedly (code $rc)"
    done
    rm -f "$SUPF" ;;
  stop)
    touch "$STOPF"
    echo "stopped deliberately at $(date '+%F %T') by text-galatay.sh stop (uid $(id -un))" > "$MARK"
    alog "deliberate-stop marker set by text-galatay.sh stop (autostart will not start her until a manual start)"
    if ! running; then echo "not running"; rm -f "$PIDF"; for _ in $(seq 1 10); do supervising || break; sleep 1; done; supervising && kill -TERM "$(cat "$SUPF")" 2>/dev/null; rm -f "$SUPF"; exit 0; fi
    send "logout" || true
    for _ in $(seq 1 25); do running || { for _ in $(seq 1 10); do supervising || break; sleep 1; done; echo "stopped"; rm -f "$PIDF"; exit 0; }; sleep 1; done
    kill -TERM "$(cat "$PIDF")" 2>/dev/null || true; sleep 5
    running && kill -KILL "$(cat "$PIDF")" 2>/dev/null || true
    rm -f "$PIDF"; echo "stopped (forced)" ;;
  status)
    supervising && echo "supervisor running (pid $(cat "$SUPF"))" || echo "supervisor not running"
    [[ -f "$MARK" ]] && echo "deliberate-stop marker: $(head -1 "$MARK") (autostart disabled until a manual start)" || echo "deliberate-stop marker: none (autostart armed)"
    if running; then echo "running (pid $(cat "$PIDF"))"; send "status"; else echo "not running"; fi ;;
  cmd|c)
    shift; [[ $# -ge 1 ]] || { echo "usage: $0 cmd \"<command>\""; exit 2; }
    running || { echo "not running"; exit 1; }
    send "$*" ;;
  log)
    tail -n "${2:-40}" "$GT_LOG" ;;
  check)
    bash -n "$0" && echo "launcher syntax OK"
    "$APP" --check ;;
  *)
    echo "usage: $0 start|stop|status|cmd \"<command>\"|log [n]|check"; exit 2 ;;
esac
