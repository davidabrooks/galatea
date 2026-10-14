#!/usr/bin/env bash
# Host setup for the Galatea text client (idempotent; run with sudo).
# - net.core.rmem_max >= 16 MB so the client's 8 MB UDP SO_RCVBUF is granted (PR #172, UdpDrops.cs).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
src="$here/sysctl-galatay-udp.conf"
dst=/etc/sysctl.d/90-galatay-udp.conf
if [ "$(id -u)" -ne 0 ]; then echo "run as root (sudo $0)" >&2; exit 1; fi
if ! cmp -s "$src" "$dst" 2>/dev/null; then install -m 0644 "$src" "$dst"; echo "wrote $dst"; else echo "$dst up to date"; fi
want=$(awk -F= '/net.core.rmem_max/{gsub(/ /,"",$2);print $2}' "$src")
cur=$(cat /proc/sys/net/core/rmem_max)
if [ "$cur" -lt "$want" ]; then sysctl -q -w net.core.rmem_max="$want"; echo "net.core.rmem_max $cur -> $want"; else echo "net.core.rmem_max = $cur (ok)"; fi
