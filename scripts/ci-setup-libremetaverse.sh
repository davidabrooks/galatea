#!/usr/bin/env bash
# Clone LibreMetaverse next to the text client and apply galatea patches (no SL login, no secrets).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TC="$ROOT/textclient"
DEST="$TC/src-libremetaverse"
# 2026-11-24: master cf64363a (3 commits past 56af5f6d). PrimMesher: restore missing bottom cap,
# degrees-to-radians twist, HasProfileCut/HasHollow, numPrimFaces (issue #222 parts 1-3);
# hollow-box inner wall is face 5 not 6 (part 4). Also seeds group/conference chat rosters
# from session-start agent_info and stops mute-only updates wiping moderator flags. All 7
# local patches still apply, including outfit-send (#218 still open).
# 2026-11-23 evening: master 56af5f6d (5 commits past 3ee812f5). Dropped object-cache.patch: Cinder closed #219 and
# landed the cache in LibreMetaverse.Caching as IObjectCacheProvider; the client assigns
# Objects.CacheProvider = new ObjectDiskCache(dir, cap). Also landed (behavior unchanged for us): IAssetCacheProvider
# (AssetCache still the default), IInventoryCacheProvider, LslSyntax cache dir. Kept: outfit-send (#218 still open),
# outfit-order, outfit-send-recheck, 3 transitive package overrides. Remaining patches applied cleanly on this pin.
# 2026-11-23 morning: master 3ee812f5 (3.4.0 pre-release; Cinder merged our #215 caps connect callback, #216 drop own avatar from the
# left region, #217 opt-in fast region crossing incl. his fix f5d4316f). Dropped: doh-connect (= upstream SetCapsConnectCallback);
# multisim shrunk to what upstream lacks (crossing local-id adoption, terse self-heal, older own-avatar copies dropped, extra
# ForgetSelfIn calls at connect and 3 s after, crossing timeline stamps); fast hand-over = upstream AgentSettings.FastRegionCrossing.
# 2026-11-18: v3.3.1 5c24674b (was v3.3.0 7f1cbd11). Dropped, now upstream: experienceid (#209), render (#207/#208/#210),
# compressed-particles (#205), udp-rcvbuf (#211), crossing-early-complete + mesh-texdomain backports, hosting/maintenance/zlogger
# package bumps. multisim rebased onto upstream AgentManager.IsSelfIn (d1d0a781, replaces our self-local-id checks).
# 2026-11-09: v3.3.0 (was v3.1.6 1844571e); seed-retry, messagepack-security and corej2k dropped then.
PIN="${LIBREMETAVERSE_SHA:-cf64363a8a6c3775f9eacfc7c5502a2e2e099dd0}"
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
apply "$TC/libremetaverse-outfit-send.patch"
apply "$TC/libremetaverse-multisim.patch"       # 2026-10-06: neighbor regions; 2026-11-23 (3ee812f5): fast hand-over and ForgetSelfIn are upstream (#217/#216); kept: her local id on crossing (newest entry, terse self-heal, older copies dropped), ForgetSelfIn also at connect + 3 s after, crossing timeline stamps
apply "$TC/libremetaverse-outfit-order.patch"     # 2026-10-27: Settings.Agent.OutfitSendRank (outfit send: body first)
apply "$TC/libremetaverse-outfit-send-recheck.patch"   # 2026-11-09: add-missing outfit send re-reads the COF after the settle wait and waits out an outfit change (stale COF re-attached the removed bikini)
apply "$TC/libremetaverse-utf8stringinterpolation.patch"    # 2026-11-09: overrides live in LibreMetaverse.Assets (ZLogger/MessagePack moved there in 3.2.0)
apply "$TC/libremetaverse-system-io-hashing.patch"
apply "$TC/libremetaverse-stringtools.patch"               # 2026-11-08: transitive override Microsoft.NET.StringTools 18.11.0 (MessagePack floor 17.11.4)
echo "LibreMetaverse ready at $DEST @ $PIN (patched)"
