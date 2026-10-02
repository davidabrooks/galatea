#!/usr/bin/env bash
# ensure-venv.sh - make sure /workspace/secondlife/.venv exists and can run scripts/overhead.py (needs Pillow).
# Why: the box-store sync does not restore .venv dirs after a reboot (and the backups deliberately skip them), so the
# venv is rebuilt from scripts/requirements.txt. Idempotent and quick when the venv is fine. Called (detached) by
# /home/box/viewers/galatay-autostart.sh once per boot; safe to run by hand. Waits up to ~10 min for PyPI.
# Log: /workspace/secondlife/textclient.autostart.log ([venv] lines).
set -uo pipefail
SL=/workspace/secondlife; VENV=${GT_VENV:-$SL/.venv}; REQ=$SL/scripts/requirements.txt
LOG=$SL/textclient.autostart.log
log() { echo "$(date '+%F %T') [venv] $*" >>"$LOG"; [[ -t 1 ]] && echo "$*"; }
exec 8>/tmp/galatay-ensure-venv.lock; flock -n 8 || { log "another ensure-venv is running"; exit 0; }
ok() { [[ -x "$VENV/bin/python" ]] && "$VENV/bin/python" -c 'import PIL' 2>/dev/null; }
if ok; then log "venv OK ($("$VENV/bin/python" -c 'import PIL; print("pillow", PIL.__version__)'))"; exit 0; fi
log "venv missing or broken: rebuilding $VENV from $REQ"
for i in $(seq 1 20); do
  rm -rf "$VENV"
  if python3 -m venv "$VENV" >>"$LOG" 2>&1 && "$VENV/bin/pip" install -q -r "$REQ" >>"$LOG" 2>&1 && ok; then
    log "venv rebuilt: $("$VENV/bin/python" -c 'import PIL; print("pillow", PIL.__version__)')"; exit 0
  fi
  log "rebuild attempt $i failed (network/PyPI?); retrying in 30 s"; sleep 30
done
log "gave up rebuilding the venv (wander snapshots will log 'snapshot failed' until it exists)"; exit 1
