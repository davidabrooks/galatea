#!/usr/bin/env bash
# install-box-tools.sh (2026-11-23): copy the box helper scripts from a git ref (default origin/main) into
# /home/box/tools/bin, so they run without a worktree and survive worktree cleanup.
#
#   install-box-tools.sh [--ref REF] [--dest DIR] [--dry-run]
#
# Installs: galatea-test.sh galatea-ship.sh lmv-pr.sh dep-scan.sh pr-automerge.sh
# galatea-wt-new.sh galatea-wt-cleanup.sh galatea-wt-report.sh (scripts/) and deploy.sh
# check-lmv-patches.sh restart-connector.sh (textclient/; deploy.sh calls the other two from its own directory).
# A replaced file that differs is saved under <dest>-backups/<time>/ first. Unchanged files are left alone.
set -euo pipefail
REF=origin/main; DEST=/home/box/tools/bin; DRY=0
while (( $# )); do case "$1" in --ref) REF=$2; shift;; --dest) DEST=$2; shift;; --dry-run) DRY=1;; *) sed -n '2,10p' "$0"; exit 2;; esac; shift; done
REPO=$(cd "$(dirname "$(readlink -f "$0")")" && git rev-parse --show-toplevel)
git -C "$REPO" fetch -q origin main || true
sha=$(git -C "$REPO" rev-parse --short "$REF")
BK="$DEST-backups/$(date +%Y%m%d-%H%M%S)"; n=0
for f in scripts/galatea-test.sh scripts/galatea-ship.sh scripts/lmv-pr.sh scripts/dep-scan.sh scripts/pr-automerge.sh \
         scripts/galatea-wt-new.sh scripts/galatea-wt-cleanup.sh scripts/galatea-wt-report.sh \
         textclient/deploy.sh textclient/check-lmv-patches.sh textclient/restart-connector.sh; do
  b=$(basename "$f"); tmp=$(mktemp)
  git -C "$REPO" show "$REF:$f" >"$tmp" || { echo "missing $f at $REF"; rm -f "$tmp"; exit 1; }
  if [[ -f $DEST/$b ]] && cmp -s "$tmp" "$DEST/$b"; then echo "same      $b"; rm -f "$tmp"; continue; fi
  if (( DRY )); then echo "would set $b"; rm -f "$tmp"; continue; fi
  mkdir -p "$DEST"; [[ -f $DEST/$b ]] && { mkdir -p "$BK"; cp -p "$DEST/$b" "$BK/$b"; }
  install -m 0755 "$tmp" "$DEST/$b"; rm -f "$tmp"; n=$((n+1)); echo "installed $b"
done
echo "$n file(s) installed into $DEST from $REF ($sha)$([[ -d $BK ]] && echo "; replaced copies in $BK")"
