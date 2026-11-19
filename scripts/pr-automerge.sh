#!/usr/bin/env bash
# pr-automerge.sh <pr-number> (2026-11-18): kept for old habits; now just galatea-ship.sh --pr N, which waits for CI and
# merges with --match-head-commit (the old version merged whatever head was current and fast-forwarded galatea-sl-repo).
# Add --sync-live to also fast-forward /workspace/galatea-sl-repo. Log: /workspace/pr-automerge-<n>.log
[[ ${1:-} =~ ^[0-9]+$ ]] || { echo "usage: pr-automerge.sh <pr-number> [--sync-live]"; exit 2; }
n=$1; shift
"$(dirname "$(readlink -f "$0")")/galatea-ship.sh" --pr "$n" "$@" 2>&1 | tee -a "/workspace/pr-automerge-$n.log"
exit "${PIPESTATUS[0]}"
