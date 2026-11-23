#!/usr/bin/env bash
# Create a Galatea git worktree under /workspace/wt/<name> from /workspace/galatea,
# branched from origin/main, and materialize LibreMetaverse source WITHOUT a nested .git.
#
#   galatea-wt-new.sh <short-name> <branch>
#
# Example: galatea-wt-new.sh collar-scripts fix/collar-parcel-scripts
set -euo pipefail

HOST=/workspace/galatea
WTROOT=/workspace/wt
LMV_CACHE=/home/box/lib/libremetaverse-cache
LMV_REPO_DEFAULT=https://github.com/cinderblocks/libremetaverse.git

usage() {
  sed -n '2,10p' "$0"
  exit 2
}

[[ ${1:-} == -h || ${1:-} == --help ]] && usage
[[ $# -eq 2 ]] || usage

NAME=$1
BRANCH=$2
if [[ ! $NAME =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]]; then
  echo "bad short-name '$NAME' (use letters, digits, dot, underscore, hyphen)" >&2
  exit 2
fi
if [[ ! $BRANCH =~ ^[A-Za-z0-9][A-Za-z0-9._/+\-]*$ || $BRANCH == *- || $BRANCH == */*/*/* ]]; then
  # allow typical git branch names like fix/foo-bar
  :
fi
if [[ $BRANCH == *..* || $BRANCH == /* || $BRANCH == *\\* ]]; then
  echo "bad branch name '$BRANCH'" >&2
  exit 2
fi

DEST="$WTROOT/$NAME"
if [[ -e $DEST ]]; then
  echo "already exists: $DEST" >&2
  exit 1
fi
if [[ ! -d $HOST/.git ]]; then
  echo "main clone missing: $HOST" >&2
  exit 1
fi

mkdir -p "$WTROOT"

echo "fetching origin/main in $HOST"
git -C "$HOST" fetch --quiet origin main

if git -C "$HOST" show-ref --verify --quiet "refs/heads/$BRANCH"; then
  echo "local branch '$BRANCH' already exists; refusing to reuse it. Pick a new name or delete the old branch once its worktree is gone." >&2
  exit 1
fi

echo "worktree add $DEST -b $BRANCH origin/main"
git -C "$HOST" worktree add -b "$BRANCH" "$DEST" origin/main

# --- LibreMetaverse without .git ---
# Read PIN / repo / patch list from this worktree's setup script so new trees
# follow the same recipe as CI, but copy the patched source without a nested git dir.
# A shared cache at $LMV_CACHE keeps one upstream .git; each worktree gets a
# git-less copy so concurrent builds cannot clobber each other or the live viewer tree.
setup=$DEST/scripts/ci-setup-libremetaverse.sh
TC=$DEST/textclient
DEST_LMV=$TC/src-libremetaverse

if [[ ! -f $setup ]]; then
  echo "warning: $setup missing; worktree created but LibreMetaverse was not set up" >&2
  echo "OK $DEST (branch $BRANCH, no LMV)"
  exit 0
fi

PIN=$(python3 -c 'import re,sys
t=open(sys.argv[1],encoding="utf-8").read()
m=re.search(r"PIN=\"\$\{LIBREMETAVERSE_SHA:-([0-9a-fA-F]+)\}\"", t)
print(m.group(1) if m else "")
' "$setup")
REPO=$(python3 -c 'import re,sys
t=open(sys.argv[1],encoding="utf-8").read()
m=re.search(r"REPO=\"\$\{LIBREMETAVERSE_REPO:-([^}]+)\}\"", t)
print(m.group(1) if m else "")
' "$setup")
REPO=${REPO:-$LMV_REPO_DEFAULT}
if [[ -z ${PIN:-} ]]; then
  echo "warning: could not parse PIN from $setup; worktree created but LibreMetaverse was not set up" >&2
  echo "OK $DEST (branch $BRANCH, no LMV)"
  exit 0
fi

mapfile -t PATCHES < <(grep -E '^apply "\$TC/' "$setup" | sed -E 's/^apply "\$TC\/([^"]+)".*/\1/')

cache_pin=""
if [[ -d $LMV_CACHE/.git ]]; then
  cache_pin=$(git -C "$LMV_CACHE" rev-parse HEAD 2>/dev/null || true)
  # After patches, HEAD is still the pin (patches are uncommitted). Also stash a pin file.
  [[ -f $LMV_CACHE/.galatea-lmv-pin ]] && cache_pin=$(cat "$LMV_CACHE/.galatea-lmv-pin")
fi

need_refresh=1
if [[ -d $LMV_CACHE/.git && ${cache_pin:-} == "$PIN" && -f $LMV_CACHE/.galatea-lmv-ready ]]; then
  need_refresh=0
fi

if (( need_refresh )); then
  echo "refreshing LMV cache $LMV_CACHE @ $PIN"
  mkdir -p "$(dirname "$LMV_CACHE")"
  if [[ -d $LMV_CACHE/.git ]]; then
    # The cache holds the previous pin with patches applied (uncommitted), so
    # drop the ready stamp and discard those edits before switching commits.
    rm -f "$LMV_CACHE/.galatea-lmv-ready"
    git -C "$LMV_CACHE" reset -q --hard
    git -C "$LMV_CACHE" clean -fdq
    git -C "$LMV_CACHE" fetch --depth 1 origin "$PIN"
    git -C "$LMV_CACHE" checkout -q -f --detach "$PIN"
    git -C "$LMV_CACHE" reset --hard "$PIN"
    git -C "$LMV_CACHE" clean -fdq
  else
    rm -rf "$LMV_CACHE"
    # Prefer a local reference clone when one exists, so we do not depend on a
    # full re-download. Never use the live viewer tree as the cache itself.
    ref=""
    for cand in \
      /workspace/wt-collar-scripts/textclient/src-libremetaverse \
      /workspace/galatea/textclient/src-libremetaverse \
      /home/box/viewers/textclient/src-libremetaverse
    do
      if [[ -d $cand/.git ]]; then ref=$cand; break; fi
    done
    if [[ -n $ref ]] && git clone --quiet --reference "$ref" "$REPO" "$LMV_CACHE" 2>/dev/null; then
      :
    else
      rm -rf "$LMV_CACHE"
      echo "cloning $REPO (no usable local reference)"
      git clone --quiet "$REPO" "$LMV_CACHE"
    fi
    git -C "$LMV_CACHE" fetch --depth 1 origin "$PIN"
    git -C "$LMV_CACHE" checkout -q --detach "$PIN"
    git -C "$LMV_CACHE" reset --hard "$PIN"
  fi
  for rel in "${PATCHES[@]}"; do
    p="$TC/$rel"
    if [[ ! -f $p ]]; then
      echo "warning: patch missing $p" >&2
      continue
    fi
    echo "  apply $rel"
    git -C "$LMV_CACHE" apply --whitespace=nowarn "$p"
  done
  printf '%s\n' "$PIN" > "$LMV_CACHE/.galatea-lmv-pin"
  # fingerprint patches so a later setup-script change refreshes the cache
  { printf '%s\n' "$PIN"; printf '%s\n' "${PATCHES[@]}"; } > "$LMV_CACHE/.galatea-lmv-ready"
fi

mkdir -p "$DEST_LMV"
# Copy source only: no nested .git, no build outputs. Concurrent worktrees stay isolated.
# (rsync is not installed on this box; tar is.)
rm -rf "$DEST_LMV"
mkdir -p "$DEST_LMV"
tar -C "$LMV_CACHE" --exclude='.git' --exclude='bin' --exclude='obj' \
  --exclude='.galatea-lmv-pin' --exclude='.galatea-lmv-ready' -cf - . \
  | tar -C "$DEST_LMV" -xf -
if [[ -e $DEST_LMV/.git ]]; then
  echo "refusing: $DEST_LMV still has a .git after copy" >&2
  exit 1
fi
echo "LibreMetaverse copied without .git to $DEST_LMV @ $PIN (from cache)"
echo "OK $DEST (branch $BRANCH)"
