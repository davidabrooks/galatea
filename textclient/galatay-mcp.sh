#!/usr/bin/env bash
# MCP stdio server for Galatay Resident (Second Life via LibreMetaverse).
# 2026-10-28: this script is now only the LAUNCHER the agent host registers. It runs the boot/background hooks below
# (idempotent, detached), then execs galatay-mcp-proxy.py, a thin stdio MCP proxy that runs the real connector as a child
# (galatay-mcp-child.sh: staged-build swap + exec mcp-app/galatay-mcp). A new connector build is loaded by
# /home/box/tools/bin/restart-connector.sh (restarts the child only), so no remove + re-add of the MCP server is needed.
# Register this script as an MCP server command. stdout = MCP protocol only; logs go to stderr
# and /workspace/secondlife/textclient.log. Login happens only via the 'login' tool
# (or set GT_AUTOLOGIN=1). The password is read in-process from the box secret store.
export DOTNET_ROOT="${DOTNET_ROOT:-/home/box/.dotnet}" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_gcServer=0
export GT_SOCK="${GT_SOCK:-/home/box/viewers/textclient/run/galatay.sock}"
export TZ="${TZ:-America/Los_Angeles}"   # 2026-10-21: logs and schedules in David's time even if the box zone is lost (box move)
mkdir -p /home/box/viewers/textclient/run && chmod 700 /home/box/viewers/textclient/run
# Boot autostart hook (2026-09-26): the agent host launches this connector after every box boot, so it also triggers
# galatay-autostart.sh (text-client daemon start, once per boot, skipped after a deliberate stop). Fully detached: own
# session, stdio on /dev/null, so the MCP stdout stays protocol-only and nothing here can delay or kill the connector.
if [[ -x /home/box/viewers/galatay-autostart.sh && ! -e /tmp/galatay-autostart.boot ]]; then
  setsid -f /home/box/viewers/galatay-autostart.sh --detach </dev/null >/dev/null 2>&1 || true
fi
# Backup hook (2026-09-26): start the hourly Second Life backup loop (idempotent: flock inside, a second start is a
# no-op). Fully detached like the autostart hook above; see /home/box/backups/RESTORE.md.
if [[ -x /home/box/backups/bin/backup-loop.sh ]]; then
  setsid -f /home/box/backups/bin/backup-loop.sh start </dev/null >/dev/null 2>&1 || true
fi
# iCloud mail watcher hook (2026-10-04): start /home/box/viewers/icloudwatch (IMAP IDLE -> "SL iCloud mail watcher" wake).
# Idempotent (pid file + flock: a second start is a no-op; 'disabled' file keeps it off). Fully detached like the hooks above.
if [[ -x /home/box/viewers/icloudwatch/icloudwatch.sh ]]; then
  setsid -f /home/box/viewers/icloudwatch/icloudwatch.sh start </dev/null >/dev/null 2>&1 || true
fi
# Scratch cleanup hook (2026-10-06): start the daily Galatea temp/scratch cleaner loop (/home/box/tools/galatea-tmp-clean-loop.sh;
# rules + log: /home/box/tools/galatea-tmp-clean.sh, /workspace/secondlife/cleanup-log.md). Idempotent (pid + flock), detached like the hooks above.
if [[ -x /home/box/tools/galatea-tmp-clean-loop.sh ]]; then
  setsid -f /home/box/tools/galatea-tmp-clean-loop.sh start </dev/null >/dev/null 2>&1 || true
fi
# Proxy dir: this script's real directory (deployed together: galatay-mcp.sh, galatay-mcp-proxy.py, galatay-mcp-child.sh).
HERE="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")" && pwd)"
export GT_PROXY_CHILD_CMD="${GT_PROXY_CHILD_CMD:-$HERE/galatay-mcp-child.sh}"
# Fallback: if the proxy file is missing, run the connector directly (old behaviour) so a bad deploy never takes it down.
if [[ ! -f "$HERE/galatay-mcp-proxy.py" ]] || ! command -v python3 >/dev/null 2>&1; then exec "$HERE/galatay-mcp-child.sh"; fi
exec python3 "$HERE/galatay-mcp-proxy.py"
