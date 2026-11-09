#!/usr/bin/env bash
# Clone LibreMetaverse next to the text client and apply galatea patches (no SL login, no secrets).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TC="$ROOT/textclient"
DEST="$TC/src-libremetaverse"
# 2026-11-09: v3.3.0 (was v3.1.6 1844571e). Patches rebased onto it; seed-retry, messagepack-security and corej2k dropped (upstream has them).
PIN="${LIBREMETAVERSE_SHA:-7f1cbd11ff110edbe494f98f1a9ff3cbcc187d91}"
REPO="${LIBREMETAVERSE_REPO:-https://github.com/cinderblocks/libremetaverse.git}"

if [[ -d "$DEST/.git" ]]; then
  git -C "$DEST" fetch --depth 1 origin "$PIN"
  git -C "$DEST" checkout -q "$PIN"
  git -C "$DEST" reset --hard "$PIN"
  git -C "$DEST" clean -fdq
else
  rm -rf "$DEST"
  git clone --quiet "$REPO" "$DEST"
  git -C "$DEST" fetch --depth 1 origin "$PIN"
  git -C "$DEST" checkout -q "$PIN"
fi

apply() {
  local p="$1"
  echo "apply $(basename "$p")"
  git -C "$DEST" apply --whitespace=nowarn "$p"
}
apply "$TC/libremetaverse-experienceid.patch"
apply "$TC/libremetaverse-render.patch"           # rebased 2026-11-09: BVH/materials files moved to LibreMetaverse.Assets in 3.2.0
apply "$TC/libremetaverse-outfit-send.patch"
apply "$TC/libremetaverse-object-cache.patch"   # 2026-10-05: on-disk object cache (VOCache-like)
apply "$TC/libremetaverse-multisim.patch"       # 2026-10-06: neighbor regions: self updates only from the current region; her local id on crossing (newest entry, terse self-heal, stale copies dropped); fast hand-over (CAM at once on the live child circuit, keep child caps, no appearance re-pass)
apply "$TC/libremetaverse-compressed-particles.patch"   # 2026-10-21: compressed ObjectUpdate particle systems parsed from the exact 86-byte block (leash chains were always empty)
apply "$TC/libremetaverse-doh-connect.patch"    # 2026-10-09: GridClient.CapsConnectCallback (SocketsHttpHandler) for SlDns.cs DoH + real-IP connects; 2026-11-09: certificates checked with upstream ServerCertificateValidator (never accept-any)
apply "$TC/libremetaverse-udp-rcvbuf.patch"     # 2026-10-14: Settings.UdpSocketReceiveBuffer (SO_RCVBUF) - lost chat when the box stalled; 2026-11-09: via UDPBase.SocketReceiveBufferSize (UDPBase moved to Transport)
apply "$TC/libremetaverse-outfit-order.patch"     # 2026-10-27: Settings.Agent.OutfitSendRank (outfit send: body first)
apply "$TC/libremetaverse-outfit-send-recheck.patch"   # 2026-11-09: add-missing outfit send re-reads the COF after the settle wait and waits out an outfit change (stale COF re-attached the removed bikini)
apply "$TC/libremetaverse-hosting-patches.patch"
apply "$TC/libremetaverse-maintenance-patches.patch"
apply "$TC/libremetaverse-zlogger.patch"
apply "$TC/libremetaverse-utf8stringinterpolation.patch"    # 2026-11-09: overrides live in LibreMetaverse.Assets (ZLogger/MessagePack moved there in 3.2.0)
apply "$TC/libremetaverse-system-io-hashing.patch"
apply "$TC/libremetaverse-stringtools.patch"               # 2026-11-08: transitive override Microsoft.NET.StringTools 18.11.0 (MessagePack floor 17.11.4)
echo "LibreMetaverse ready at $DEST @ $PIN (patched)"
