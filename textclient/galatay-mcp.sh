#!/usr/bin/env bash
# MCP stdio server for Galatay Resident (Second Life via LibreMetaverse).
# Register this script as an MCP server command. stdout = MCP protocol only; logs go to stderr
# and /workspace/secondlife/textclient.log. Login happens only via the 'login' tool
# (or set GT_AUTOLOGIN=1). The password is read in-process from the box secret store.
export DOTNET_ROOT="${DOTNET_ROOT:-/home/box/.dotnet}" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_gcServer=0
export GT_SOCK="${GT_SOCK:-/home/box/viewers/textclient/run/galatay.sock}"
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
# Staged connector build (2026-09-27): a new build is prepared in textclient/mcp-app-next (with a READY file) while the
# running connector keeps using mcp-app. At the next launch, if no connector process is running (pgrep -x), the old
# build is moved to mcp-app-backups/mcp-app-<time> and mcp-app-next becomes mcp-app. Any failure rolls back and the old
# build starts as before. Nothing is printed to stdout (MCP protocol only).
TCD=/home/box/viewers/textclient
if [[ -f "$TCD/mcp-app-next/READY" && -x "$TCD/mcp-app-next/galatay-mcp" ]] && ! pgrep -x galatay-mcp >/dev/null 2>&1; then
  sw_ts=$(date +%Y%m%d-%H%M%S); mkdir -p "$TCD/mcp-app-backups"
  if mv "$TCD/mcp-app" "$TCD/mcp-app-backups/mcp-app-$sw_ts" 2>/dev/null; then
    if mv "$TCD/mcp-app-next" "$TCD/mcp-app" 2>/dev/null; then
      mv "$TCD/mcp-app/READY" "$TCD/mcp-app-backups/mcp-app-$sw_ts.READY-of-new-build" 2>/dev/null
      echo "$(date '+%F %T') [mcp-launch] staged connector build installed (old build -> mcp-app-backups/mcp-app-$sw_ts)" >>/workspace/secondlife/textclient.log 2>/dev/null
    else
      mv "$TCD/mcp-app-backups/mcp-app-$sw_ts" "$TCD/mcp-app" 2>/dev/null
      echo "$(date '+%F %T') [mcp-launch] staged build swap failed: kept the old build" >>/workspace/secondlife/textclient.log 2>/dev/null
    fi
  fi
fi
exec /home/box/viewers/textclient/mcp-app/galatay-mcp
