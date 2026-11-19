#!/usr/bin/env bash
# lmv-pr.sh (2026-11-18): LibreMetaverse upstream PRs from worktrees of /workspace/wt/lmv-main
# (origin = davidabrooks/libremetaverse fork, upstream = cinderblocks/libremetaverse, base master).
#
#   lmv-pr.sh new <name> <branch>               worktree /workspace/wt/lmv-<name> on a new branch from upstream/master
#   lmv-pr.sh commit <wt> -m MSG [git args...]  git commit authored+committed as David Brooks <davidabrooks@gmail.com>
#   lmv-pr.sh test <wt> [--filter EXPR]         dotnet test LibreMetaverse.Tests -f net10.0, Category!=RequiresLiveServer
#                                               (AND EXPR); records a pass stamp for HEAD
#   lmv-pr.sh open <wt> --title T --body-file F [--draft]
#                                               checks: clean tree, commits ahead of upstream/master, every commit authored
#                                               as David Brooks, a passing test stamp for HEAD; then push to the fork and
#                                               gh pr create against cinderblocks (reuses an existing PR for the branch)
#   lmv-pr.sh cleanup <wt> [--apply] [--discard]
#                                               dry run by default. Removes the worktree + local branch (+ fork branch) when
#                                               its upstream PR is merged/closed, or when it never got a PR and has no
#                                               commits ahead of upstream/master. --discard drops unpublished work.
#   lmv-pr.sh list                              worktrees, branches, ahead counts, PR state
# <wt> may be the path or the <name>. Test logs: /tmp/lmv-test-<name>-<time>.log
# Exit: 0 ok, 1 failed, 2 usage / not ready.
set -uo pipefail
export GH_PROMPT_DISABLED=1 GIT_TERMINAL_PROMPT=0
MAIN=${LMV_MAIN:-/workspace/wt/lmv-main}; WTROOT=${LMV_WTROOT:-/workspace/wt}
UPREPO=cinderblocks/libremetaverse; FORK_OWNER=davidabrooks; BASE=master
AUTHOR_NAME="David Brooks"; AUTHOR_EMAIL="davidabrooks@gmail.com"
FILTER_BASE="Category!=RequiresLiveServer"
usage() { sed -n '2,22p' "$0"; exit 2; }
die() { echo "lmv-pr: $1"; exit "${2:-2}"; }
wtpath() { local w=$1; [[ -d $w ]] || w=$WTROOT/lmv-$1; [[ -d $w ]] || die "no worktree '$1'"; (cd "$w" && git rev-parse --show-toplevel); }
stampfile() { echo "$(git -C "$1" rev-parse --git-dir)/lmv-pr-test-stamp"; }
prinfo() { gh pr list --repo "$UPREPO" --head "$1" --state all --json number,state,url,headRepositoryOwner \
  -q "[.[] | select(.headRepositoryOwner.login==\"$FORK_OWNER\")] | if length > 0 then .[0] | \"\(.number) \(.state) \(.url)\" else empty end" 2>/dev/null; }

cmd=${1:-}; shift || true
case "$cmd" in
new)
  [[ $# -eq 2 ]] || usage; name=$1; br=$2
  [[ $name =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || die "bad name"
  dest=$WTROOT/lmv-$name; [[ ! -e $dest ]] || die "$dest exists"
  git -C "$MAIN" show-ref -q --verify "refs/heads/$br" && die "branch $br already exists"
  git -C "$MAIN" fetch -q upstream "$BASE" || die "fetch upstream failed" 1
  git -C "$MAIN" worktree add -q --no-track -b "$br" "$dest" "upstream/$BASE" || die "worktree add failed" 1
  echo "OK $dest (branch $br from upstream/$BASE $(git -C "$dest" rev-parse --short HEAD))";;
commit)
  [[ $# -ge 1 ]] || usage; w=$(wtpath "$1") || exit 2; shift
  GIT_AUTHOR_NAME="$AUTHOR_NAME" GIT_AUTHOR_EMAIL="$AUTHOR_EMAIL" GIT_COMMITTER_NAME="$AUTHOR_NAME" GIT_COMMITTER_EMAIL="$AUTHOR_EMAIL" \
    git -C "$w" -c user.name="$AUTHOR_NAME" -c user.email="$AUTHOR_EMAIL" commit "$@" || exit 1
  git -C "$w" log -1 --format='committed %h as %an <%ae>: %s';;
test)
  [[ $# -ge 1 ]] || usage; w=$(wtpath "$1") || exit 2; shift; filter=$FILTER_BASE
  while (( $# )); do case "$1" in --filter) filter="($FILTER_BASE)&(${2:?})"; shift;; *) usage;; esac; shift; done
  export PATH="/home/box/.dotnet:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-/home/box/.dotnet}" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
  log=/tmp/lmv-test-$(basename "$w")-$(date +%Y%m%d-%H%M%S).log; head=$(git -C "$w" rev-parse HEAD)
  dirty=$(git -C "$w" status --porcelain --untracked-files=no)
  echo "testing $(basename "$w") @ ${head:0:7}${dirty:+ (+uncommitted changes)}; filter $filter; log $log"
  (cd "$w" && dotnet test LibreMetaverse.Tests/LibreMetaverse.Tests.csproj -f net10.0 --filter "$filter" --nologo) >"$log" 2>&1; rc=$?
  sum=$(grep -E '(Passed|Failed)!' "$log" | tail -1 | sed -E 's/^ +//')
  if (( rc )); then grep -E ': error |^\s+Failed ' "$log" | sort -u | head -20; rm -f "$(stampfile "$w")"; die "tests FAILED (rc $rc) ${sum:-no summary}" 1; fi
  [[ -n $sum ]] || { rm -f "$(stampfile "$w")"; die "no test summary in $log (did the filter match nothing?)" 1; }
  if [[ -z $dirty ]]; then echo "$head $(date +%s) $sum" >"$(stampfile "$w")"; else echo "note: uncommitted changes, so no pass stamp recorded (commit, then test again before open)"; fi
  echo "tests PASS: $sum";;
open)
  [[ $# -ge 1 ]] || usage; w=$(wtpath "$1") || exit 2; shift; title=""; body=""; draft=()
  while (( $# )); do case "$1" in --title) title=${2:?}; shift;; --body-file) body=${2:?}; shift;; --draft) draft=(--draft);; *) usage;; esac; shift; done
  [[ -n $title && -f $body ]] || die "--title and an existing --body-file are required"
  br=$(git -C "$w" rev-parse --abbrev-ref HEAD); [[ $br != HEAD && $br != "$BASE" ]] || die "not on a feature branch"
  [[ -z $(git -C "$w" status --porcelain) ]] || die "uncommitted changes in $w"
  git -C "$w" fetch -q upstream "$BASE"
  n=$(git -C "$w" rev-list --count "upstream/$BASE..HEAD"); (( n > 0 )) || die "no commits ahead of upstream/$BASE"
  bad=$(git -C "$w" log --format='%h %an <%ae> / %cn <%ce>' "upstream/$BASE..HEAD" | grep -v -F "$AUTHOR_NAME <$AUTHOR_EMAIL> / $AUTHOR_NAME <$AUTHOR_EMAIL>")
  [[ -z $bad ]] || die "commits not authored+committed as $AUTHOR_NAME <$AUTHOR_EMAIL> (use lmv-pr.sh commit / amend):
$bad"
  head=$(git -C "$w" rev-parse HEAD); read -r sh _ < <(cat "$(stampfile "$w")" 2>/dev/null || echo none)
  [[ $sh == "$head" ]] || die "no passing 'lmv-pr.sh test' for HEAD ${head:0:7}; run it first"
  git -C "$w" push -q -u origin "$br" || die "push to fork failed" 1
  read -r num state url < <(prinfo "$br")
  if [[ -n ${num:-} && $state == OPEN ]]; then echo "PR already open: $url (pushed ${head:0:7})"; exit 0; fi
  url=$(gh pr create --repo "$UPREPO" --base "$BASE" --head "$FORK_OWNER:$br" --title "$title" --body-file "$body" "${draft[@]}") || die "gh pr create failed" 1
  echo "opened $url ($n commit(s), head ${head:0:7})";;
cleanup)
  [[ $# -ge 1 ]] || usage; w=$(wtpath "$1") || exit 2; shift; apply=0; discard=0
  while (( $# )); do case "$1" in --apply) apply=1;; --discard) discard=1;; *) usage;; esac; shift; done
  [[ $(cd "$w" && pwd -P) != $(cd "$MAIN" && pwd -P) ]] || die "refusing to remove the main clone"
  br=$(git -C "$w" rev-parse --abbrev-ref HEAD)
  git -C "$MAIN" fetch -q upstream "$BASE" 2>/dev/null
  ahead=$(git -C "$w" rev-list --count "upstream/$BASE..HEAD"); dirty=$(git -C "$w" status --porcelain)
  read -r num state url < <(prinfo "$br"); state=${state:-NONE}
  onfork=$(git -C "$MAIN" ls-remote --heads origin "$br" | wc -l)
  echo "$w: branch $br, $ahead ahead of upstream/$BASE, ${dirty:+uncommitted changes, }PR ${num:+#$num }$state, fork branch $([[ $onfork -gt 0 ]] && echo present || echo absent)"
  ok=0; why=""
  if [[ $state == MERGED || $state == CLOSED ]]; then ok=1; why="PR $state"
  elif [[ $state == NONE && $ahead == 0 && -z $dirty ]]; then ok=1; why="never published, nothing ahead"
  elif (( discard )) && [[ $state != OPEN ]]; then ok=1; why="--discard"; fi
  [[ $state != OPEN ]] || die "PR #$num is still open; nothing removed"
  (( ok )) || die "has unpublished work (ahead $ahead${dirty:+, uncommitted}); keep it, open a PR, or pass --discard"
  delfork=0; [[ $onfork -gt 0 && ( $state == MERGED || $state == CLOSED ) ]] && delfork=1
  echo "plan ($why): git worktree remove --force $w; git branch -D $br$([[ $delfork == 1 ]] && echo "; git push origin --delete $br"); git worktree prune"
  (( apply )) || { echo "dry run: re-run with --apply"; exit 0; }
  git -C "$MAIN" worktree remove --force "$w" && git -C "$MAIN" branch -D "$br" || die "remove failed" 1
  (( delfork )) && { git -C "$MAIN" push -q origin --delete "$br" || echo "warning: fork branch delete failed"; }
  git -C "$MAIN" worktree prune; echo "removed $w and branch $br";;
list)
  git -C "$MAIN" worktree list --porcelain | awk '/^worktree /{print $2}' | while read -r w; do
    br=$(git -C "$w" rev-parse --abbrev-ref HEAD); a=$(git -C "$w" rev-list --count "upstream/$BASE..HEAD" 2>/dev/null)
    read -r num state _ < <(prinfo "$br"); echo "$w  $br  ahead=$a  PR=${num:+#$num }${state:-none}"; done;;
*) usage;;
esac
