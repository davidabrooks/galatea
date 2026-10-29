#!/usr/bin/env bash
# check-lmv-patches.sh <dir>...  (2026-10-28): every directory must hold a LibreMetaverse.dll that carries the galatea patches
# (marker: the seed-retry patch's Caps.SeedMaxAttempts). An unpatched/stale DLL (e.g. the old /home/box/viewers/textclient/*-staging,
# sha 08b13a80...) crashes the connector ~1 s after login (unbounded seed-capability recursion = stack overflow, SIGABRT).
# build-all.sh runs this on app-staging + mcp-app-staging; run it on a directory before deploying from it, or on the live one after.
# Exit 0 = all good, 6 = a DLL is missing or unpatched.
bad=0
for d in "$@"; do
  f="$d/LibreMetaverse.dll"
  if [[ ! -f "$f" ]]; then echo "LMV CHECK FAILED: $f does not exist"; bad=1; continue; fi
  if grep -aq "SeedMaxAttempts" "$f"; then echo "LMV check OK: $f is patched ($(sha256sum "$f" | cut -c1-12))"
  else echo "LMV CHECK FAILED: $f is UNPATCHED/stale ($(sha256sum "$f" | cut -c1-12)): no seed-retry patch; logging in with it crashes the connector. Build with build-all.sh from the repo and deploy from the repo's own app-staging / mcp-app-staging."; bad=1; fi
done
(( bad )) && exit 6
exit 0
