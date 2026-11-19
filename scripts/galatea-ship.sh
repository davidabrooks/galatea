#!/usr/bin/env bash
# galatea-ship.sh (2026-11-18): push a Galatea worktree branch, open (or reuse) its PR, wait for CI, merge, clean up.
#
#   galatea-ship.sh [--title T] [--body-file F] [--no-merge] [--cleanup] [--timeout MIN] [--sync-live] [WORKTREE]
#   galatea-ship.sh --pr N [--timeout MIN] [--sync-live]          # just wait for CI on PR N and merge it
#
# WORKTREE defaults to the current git toplevel; it must be on a branch (not main), have commits ahead of origin/main
# and no uncommitted changes (generated textclient/mcp-app-next/ is ignored). Commit first; this script never commits.
#   1. git push -u origin <branch>
#   2. gh pr create --base main (title/body default to the last commit subject/body), or reuse the open PR
#   3. wait for every check to finish (default 30 min): any failure or cancel -> exit 1, nothing merged
#   4. gh pr merge N --merge --match-head-commit <the head CI ran on>; print the merge commit
#   5. fetch origin in the main clone; galatea-wt-cleanup.sh dry run (or --apply with --cleanup)
# --no-merge stops after step 2 (or 3 with CI wait: use --no-merge --wait-ci). --sync-live also fast-forwards
# /workspace/galatea-sl-repo (the old pr-automerge.sh behaviour; skipped with a note if that tree is dirty).
# Exit: 0 merged (or opened with --no-merge), 1 CI failed / merge refused, 2 usage or tree not ready, 3 CI timeout.
set -uo pipefail
export GH_PROMPT_DISABLED=1 GIT_TERMINAL_PROMPT=0
TITLE=""; BODY=""; MERGE=1; WAITCI=1; CLEAN=0; TIMEOUT=30; SYNC=0; PR=""; WT=""
CLEANUP_SH=${GALATEA_WT_CLEANUP:-/home/box/tools/bin/galatea-wt-cleanup.sh}
HOSTCLONE=/workspace/galatea; LIVEREPO=/workspace/galatea-sl-repo
usage() { sed -n '2,17p' "$0"; exit 2; }
while (( $# )); do case "$1" in
  --title) TITLE=${2:?}; shift;; --body-file) BODY=${2:?}; shift;; --no-merge) MERGE=0; WAITCI=0;; --wait-ci) WAITCI=1;;
  --cleanup) CLEAN=1;; --timeout) TIMEOUT=${2:?}; shift;; --sync-live) SYNC=1;; --pr) PR=${2:?}; shift;;
  -h|--help) usage;; -*) echo "unknown option $1"; usage;; *) WT=$1;; esac; shift; done
say() { echo "[ship $(date +%H:%M:%S)] $*"; }

if [[ -z $PR ]]; then
  WT=$(cd "${WT:-.}" && git rev-parse --show-toplevel 2>/dev/null) || { say "not a git working tree"; exit 2; }
  cd "$WT"
  BR=$(git rev-parse --abbrev-ref HEAD)
  [[ $BR != HEAD && $BR != main ]] || { say "on '$BR': ship needs a feature branch"; exit 2; }
  dirty=$(git status --porcelain -- . ':!textclient/mcp-app-next')
  [[ -z $dirty ]] || { say "uncommitted changes; commit or stash first:"; echo "$dirty" | head -20; exit 2; }
  git fetch -q origin main
  ahead=$(git rev-list --count origin/main..HEAD)
  (( ahead > 0 )) || { say "no commits ahead of origin/main"; exit 2; }
  HEADSHA=$(git rev-parse HEAD)
  say "pushing $BR ($ahead commit(s), head ${HEADSHA:0:7})"
  git push -q -u origin "$BR" || { say "push failed"; exit 1; }
  PR=$(gh pr list --head "$BR" --state open --json number -q '.[0].number' 2>/dev/null)
  if [[ -n $PR ]]; then say "reusing open PR #$PR"
  else
    [[ -n $TITLE ]] || TITLE=$(git log -1 --format=%s)
    tmpb=$(mktemp); if [[ -n $BODY ]]; then cp "$BODY" "$tmpb"; else git log --reverse --format='%s%n%n%b' origin/main..HEAD >"$tmpb"; fi
    url=$(gh pr create --base main --head "$BR" --title "$TITLE" --body-file "$tmpb") || { rm -f "$tmpb"; say "gh pr create failed"; exit 1; }
    rm -f "$tmpb"; PR=${url##*/}; say "opened $url"
  fi
else
  cd "$HOSTCLONE" || exit 2
  HEADSHA=$(gh pr view "$PR" --json headRefOid -q .headRefOid) || { say "no PR #$PR"; exit 2; }
fi
URL=$(gh pr view "$PR" --json url -q .url)
(( WAITCI )) || { say "PR #$PR open: $URL (not waiting for CI, not merging)"; exit 0; }

say "waiting for CI on #$PR (head ${HEADSHA:0:7}, timeout ${TIMEOUT} min)"
deadline=$(( $(date +%s) + TIMEOUT * 60 )); seen=0
while :; do
  cur=$(gh pr view "$PR" --json headRefOid -q .headRefOid)
  [[ $cur == "$HEADSHA" ]] || { say "PR head moved to ${cur:0:7} while waiting; refusing to merge"; exit 1; }
  js=$(gh pr checks "$PR" --json name,bucket 2>/dev/null || true)
  n=$(jq 'length' <<<"${js:-[]}" 2>/dev/null || echo 0)
  if (( n > 0 )); then
    seen=1
    bad=$(jq -r '.[] | select(.bucket=="fail" or .bucket=="cancel") | .name' <<<"$js")
    [[ -z $bad ]] || { say "CI FAILED on #$PR: $(tr '\n' ',' <<<"$bad")"; say "$URL"; exit 1; }
    pend=$(jq '[.[] | select(.bucket=="pending")] | length' <<<"$js")
    if (( pend == 0 )); then say "CI green: $(jq -r '[.[] | "\(.name)=\(.bucket)"] | join(", ")' <<<"$js")"; break; fi
  fi
  (( $(date +%s) < deadline )) || { say "timed out waiting for CI on #$PR ($([[ $seen == 1 ]] && echo 'checks still pending' || echo 'no checks reported'))"; exit 3; }
  sleep 20
done
(( MERGE )) || { say "PR #$PR green, not merging (--no-merge): $URL"; exit 0; }
gh pr merge "$PR" --merge --match-head-commit "$HEADSHA" || { say "merge refused"; exit 1; }
for _ in 1 2 3 4 5 6; do
  read -r state mc < <(gh pr view "$PR" --json state,mergeCommit -q '"\(.state) \(.mergeCommit.oid // "")"')
  [[ $state == MERGED && -n $mc ]] && break; sleep 5
done
say "merged #$PR as $mc ($URL)"
git -C "$HOSTCLONE" fetch -q origin
if (( SYNC )); then
  if [[ -n $(git -C "$LIVEREPO" status --porcelain -- . ':!textclient/mcp-app-next') ]]; then say "--sync-live skipped: $LIVEREPO has uncommitted changes"
  else git -C "$LIVEREPO" fetch -q && git -C "$LIVEREPO" merge -q --ff-only '@{u}' && say "$LIVEREPO fast-forwarded to $(git -C "$LIVEREPO" rev-parse --short HEAD)" || say "--sync-live: fast-forward failed"; fi
fi
if [[ -n $WT && -x $CLEANUP_SH ]]; then
  cd /
  if (( CLEAN )); then say "cleanup: $CLEANUP_SH --apply $WT"; "$CLEANUP_SH" --apply "$WT"
  else say "cleanup dry run (re-run with --apply once nothing else needs the tree):"; "$CLEANUP_SH" "$WT"; fi
fi
exit 0
