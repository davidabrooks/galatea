#!/usr/bin/env bash
# check-lmv-patches.sh <dir>...  (2026-11-23): every directory must hold a LibreMetaverse.dll that carries
# Caps.ScheduleSeedRetry (upstream v3.2.0+ seed-capability retry fix; an older DLL can crash the connector
# ~1 s after login) AND a LibreMetaverse.Caching.dll that carries ObjectDiskCache (the optional on-disk
# object cache we opt into via ObjectManager.CacheProvider; Cinder closed #219 and moved it out of core).
# build-all.sh runs this on app-staging + mcp-app-staging; run it on a directory before deploying from it.
# Exit 0 = all good, 6 = a DLL is missing or unpatched.
bad=0
for d in "$@"; do
  f="$d/LibreMetaverse.dll"
  c="$d/LibreMetaverse.Caching.dll"
  if [[ ! -f "$f" ]]; then echo "LMV CHECK FAILED: $f does not exist"; bad=1; continue; fi
  miss=""
  grep -aq "ScheduleSeedRetry" "$f" || miss="$miss upstream-seed-retry-fix(v3.2.0+)"
  if [[ ! -f "$c" ]]; then miss="$miss LibreMetaverse.Caching.dll"
  else grep -aq "ObjectDiskCache" "$c" || miss="$miss LibreMetaverse.Caching.ObjectDiskCache"; fi
  if [[ -z "$miss" ]]; then echo "LMV check OK: $f is current ($(sha256sum "$f" | cut -c1-12)); Caching present ($(sha256sum "$c" | cut -c1-12))"
  else echo "LMV CHECK FAILED: $d is UNPATCHED/stale ($(sha256sum "$f" | cut -c1-12)): missing$miss; logging in with an old LibreMetaverse can crash the connector. Build with build-all.sh from the repo and deploy from the repo's own app-staging / mcp-app-staging."; bad=1; fi
done
(( bad )) && exit 6
exit 0
