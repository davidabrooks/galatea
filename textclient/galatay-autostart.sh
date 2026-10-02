#!/usr/bin/env bash
# galatay-autostart.sh - boot entry point for Galatay's text client (added 2026-09-26 after the 10:22 PT reboot).
#
# Why this and not systemd/cron: on this box PID 1 is tini -> pod-daemon (no systemd, no cron installed), and a reboot
# recreates the root filesystem from the image (/etc, /usr, /tmp are fresh; only /home/box and /workspace are restored),
# so a unit file, crontab or rc script outside /home/box would not survive a reboot anyway. The agent host starts the
# Second Life MCP connector (/home/box/viewers/galatay-mcp.sh) as user box after each boot; that launcher calls
# this script fully detached (own session, no stdio), so it acts as the boot hook.
#
# What it does (once per boot; /tmp is empty after a reboot):
#   - nothing if run/deliberate-stop exists (set by 'text-galatay.sh stop' or a logout command; cleared by a manual start)
#   - nothing if the client is already up (pgrep -x galatay-text / supervisor pid file) or Firestorm is running
#   - waits for the network (TCP to the SL login server, up to 10 min) and for the box clock/timezone to settle
#   - runs 'text-galatay.sh start' with GT_AUTOSTART=1 (start refuses if the marker appeared meanwhile), retries on failure
# Usage: galatay-autostart.sh            run now in the foreground (what the hook runs, minus the detach)
#        galatay-autostart.sh --detach   re-launch itself in the background (used by galatay-mcp.sh)
# Log: /workspace/secondlife/textclient.autostart.log (+ one-line [autostart] entries in textclient.log). Never logs secrets.
set -uo pipefail
SELF=$(readlink -f "$0")
CTL=/home/box/viewers/text-galatay.sh
RUN=/home/box/viewers/textclient/run
MARK="$RUN/deliberate-stop"; SUPF="$RUN/supervisor.pid"
ALOG=/workspace/secondlife/textclient.autostart.log
GT_LOG=/workspace/secondlife/textclient.log
STAMP=/tmp/galatay-autostart.boot      # decision made this boot
LOCK=/tmp/galatay-autostart.lock
LOGIN_HOST=login.agni.lindenlab.com

if [[ "${1:-}" == "--detach" ]]; then
  [[ -e "$STAMP" ]] && exit 0
  mkdir -p "$(dirname "$ALOG")"
  setsid -f "$SELF" >>"$ALOG" 2>&1 </dev/null
  exit 0
fi

[[ "$(id -un)" == box ]] || { echo "must run as user box (is $(id -un)); doing nothing"; exit 0; }
export HOME=/home/box USER=box LOGNAME=box
export PATH="/usr/local/bin:/usr/bin:/bin:${PATH:-}"
mkdir -p "$(dirname "$ALOG")"
log() { local m="$(date '+%F %T') [autostart] $*"; echo "$m" >>"$ALOG"; echo "$m" >>"$GT_LOG"; }
say() { echo "$(date '+%F %T') [autostart] $*" >>"$ALOG"; }   # detail only in the autostart log

exec 9>"$LOCK"
flock -n 9 || { say "another autostart is already running; exiting"; exit 0; }
[[ -e "$STAMP" ]] && { say "already decided this boot ($(cat "$STAMP")); exiting"; exit 0; }
decide() { echo "$(date '+%F %T') $*" > "$STAMP"; }

up_s=$(cut -d. -f1 /proc/uptime)
say "autostart run (uptime ${up_s} s, pid $$)"

# Python venv for the wander snapshot / `overhead` (scripts/overhead.py needs Pillow). The box-store sync doesn't restore
# .venv dirs after a reboot and the backups skip them, so rebuild it from scripts/requirements.txt, detached so it never
# delays the client start (added 2026-09-27; log lines "[venv]" in this autostart log).
EV=/workspace/secondlife/scripts/ensure-venv.sh
[[ -x "$EV" ]] && setsid -f "$EV" >/dev/null 2>&1 </dev/null

check_down_reasons() {   # prints a reason and returns 0 if we must not start
  if [[ -f "$MARK" ]]; then echo "deliberate-stop marker present ($(head -1 "$MARK")): not starting her"; return 0; fi
  if p=$(pgrep -x galatay-text); then echo "client already running (pid $(echo $p)): nothing to do"; return 0; fi
  if [[ -f "$SUPF" ]] && kill -0 "$(cat "$SUPF")" 2>/dev/null && grep -q text-galatay.sh "/proc/$(cat "$SUPF")/cmdline" 2>/dev/null; then
    echo "supervisor already running (pid $(cat "$SUPF")): nothing to do"; return 0; fi
  if pgrep -x do-not-directly >/dev/null; then echo "Firestorm is running (same account): not starting the text client"; return 0; fi
  return 1
}
if why=$(check_down_reasons); then log "$why"; decide "$why"; exit 0; fi

# boot settle: the box sets its timezone/clock ~1-2 min after boot (10:23:46 today); logs should be in PT
while (( $(cut -d. -f1 /proc/uptime) < 90 )); do sleep 5; done

# wait for the network: TCP connect to the SL login server (DNS + route), up to 10 min
net_ok=0
for i in $(seq 1 60); do
  if timeout 8 bash -c "exec 3<>/dev/tcp/$LOGIN_HOST/443" 2>/dev/null; then net_ok=1; break; fi
  (( i == 1 )) && log "network not ready yet (no TCP to $LOGIN_HOST:443); waiting up to 10 min"
  sleep 10
done
(( net_ok )) || { log "network still down after 10 min: giving up this run (a later hook call this boot will retry)"; exit 1; }
say "network OK ($LOGIN_HOST:443 reachable)"

for attempt in 1 2 3; do
  if why=$(check_down_reasons); then log "$why"; decide "$why"; exit 0; fi   # a deliberate stop during the wait wins
  log "starting the text client (boot autostart, attempt $attempt)"
  out=$(GT_AUTOSTART=1 "$CTL" start 2>&1 9>&-); rc=$?
  echo "$out" >>"$ALOG"
  if (( rc == 0 )); then log "started: $(echo "$out" | tail -1)"; decide "started: $(echo "$out" | tail -1)"; exit 0; fi
  if (( rc == 6 )); then log "start refused: deliberate-stop marker appeared"; decide "marker appeared"; exit 0; fi
  log "start attempt $attempt failed (rc $rc): $(echo "$out" | tail -1)"
  sleep $(( 60 * attempt ))
done
if why=$(check_down_reasons); then log "$why"; decide "$why"; exit 0; fi
log "gave up after 3 start attempts; see $ALOG and textclient.supervisor.log (a later hook call this boot will retry)"
exit 1
