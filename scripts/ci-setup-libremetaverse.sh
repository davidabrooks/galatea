#!/usr/bin/env bash
# Clone LibreMetaverse next to the text client and apply galatea patches (no SL login, no secrets).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TC="$ROOT/textclient"
DEST="$TC/src-libremetaverse"
PIN="${LIBREMETAVERSE_SHA:-1844571eb563fe2f26c9acbfc1b1479dedb8c1b5}"
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
apply "$TC/libremetaverse-render.patch"
apply "$TC/libremetaverse-outfit-send.patch"
apply "$TC/libremetaverse-object-cache.patch"   # 2026-10-05: on-disk object cache (VOCache-like)
apply "$TC/libremetaverse-multisim.patch"       # 2026-10-06: neighbor regions: self updates only from the current region; adopt her local id on crossing
echo "LibreMetaverse ready at $DEST @ $PIN (patched)"
