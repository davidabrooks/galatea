#!/usr/bin/env bash
# The real connector launch (2026-10-28, split out of galatay-mcp.sh): staged-build swap, then exec mcp-app/galatay-mcp.
# galatay-mcp-proxy.py runs this for every child it spawns (first start and every restart). stdout = MCP protocol only;
# nothing here prints to stdout.
export DOTNET_ROOT="${DOTNET_ROOT:-/home/box/.dotnet}" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_gcServer=0
export GT_SOCK="${GT_SOCK:-/home/box/viewers/textclient/run/galatay.sock}"
export TZ="${TZ:-America/Los_Angeles}"
# Staged connector build (2026-09-27): a new build is prepared in textclient/mcp-app-next (with a READY file) while the
# running connector keeps using mcp-app. At launch, if no connector process is running (pgrep -x), the old build is
# moved to mcp-app-backups/mcp-app-<time> and mcp-app-next becomes mcp-app. Any failure rolls back and the old build
# starts as before. With two connectors (two exec daemons) each restarting at once, the one that finds the other's child
# still alive waits up to GT_SWAP_WAIT_S for it to exit or for the other launcher to do the swap (READY gone), then
# starts anyway. Swap section is under a flock (run/connector-spawn.lock).
# Test knobs: GT_TCD (textclient dir), GT_CHILD_PROC_NAME (process name pgrep looks for), GT_SWAP_LOG.
TCD="${GT_TCD:-/home/box/viewers/textclient}"
PNAME="${GT_CHILD_PROC_NAME:-galatay-mcp}"
SWAPLOG="${GT_SWAP_LOG:-/workspace/secondlife/textclient.log}"
if [[ -f "$TCD/mcp-app-next/READY" && -x "$TCD/mcp-app-next/galatay-mcp" ]]; then
  mkdir -p "$(dirname "$GT_SOCK")" 2>/dev/null
  exec 9>"$(dirname "$GT_SOCK")/connector-spawn.lock"
  deadline=$(( $(date +%s) + ${GT_SWAP_WAIT_S:-15} ))
  while :; do
    flock -w 20 9 || break
    [[ -f "$TCD/mcp-app-next/READY" ]] || break                       # someone else already swapped
    if ! pgrep -x "$PNAME" >/dev/null 2>&1; then
      sw_ts=$(date +%Y%m%d-%H%M%S); mkdir -p "$TCD/mcp-app-backups"
      if mv "$TCD/mcp-app" "$TCD/mcp-app-backups/mcp-app-$sw_ts" 2>/dev/null; then
        if mv "$TCD/mcp-app-next" "$TCD/mcp-app" 2>/dev/null; then
          mv "$TCD/mcp-app/READY" "$TCD/mcp-app-backups/mcp-app-$sw_ts.READY-of-new-build" 2>/dev/null
          echo "$(date '+%F %T') [mcp-launch] staged connector build installed (old build -> mcp-app-backups/mcp-app-$sw_ts)" >>"$SWAPLOG" 2>/dev/null
        else
          mv "$TCD/mcp-app-backups/mcp-app-$sw_ts" "$TCD/mcp-app" 2>/dev/null
          echo "$(date '+%F %T') [mcp-launch] staged build swap failed: kept the old build" >>"$SWAPLOG" 2>/dev/null
        fi
      fi
      break
    fi
    flock -u 9
    if (( $(date +%s) >= deadline )); then
      echo "$(date '+%F %T') [mcp-launch] staged build NOT installed: another $PNAME is still running; starting the current build" >>"$SWAPLOG" 2>/dev/null
      break
    fi
    sleep 0.5
  done
  exec 9>&-
fi
exec "$TCD/mcp-app/galatay-mcp"
