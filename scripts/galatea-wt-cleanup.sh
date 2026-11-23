#!/usr/bin/env bash
# Remove a Galatea worktree after its PR is merged or closed, if it is clean and not in use.
# Dry-run by default. --apply actually removes the worktree, deletes the local branch,
# deletes the remote branch if it is still on origin, and prunes host metadata.
#
#   galatea-wt-cleanup.sh [--apply] <path> [<path> ...]
#   galatea-wt-cleanup.sh [--apply] --merged    # scan worktrees of known hosts
set -euo pipefail

HOST_DEFAULT=/workspace/galatea
APPLY=0
MERGED_SCAN=0
PATHS=()

usage() { sed -n '2,11p' "$0"; exit 2; }

while [[ $# -gt 0 ]]; do
  case $1 in
    --apply) APPLY=1 ;;
    --merged) MERGED_SCAN=1 ;;
    -h|--help) usage ;;
    --) shift; PATHS+=("$@"); break ;;
    -*) echo "unknown option $1" >&2; usage ;;
    *) PATHS+=("$1") ;;
  esac
  shift
done

KNOWN_HOSTS=(
  /workspace/galatea
  /workspace/galatea-sl-repo
  /workspace/batch-20261115/galatea
  /workspace/dep-audit-20261115/src-main
)

if (( MERGED_SCAN )); then
  for host in "${KNOWN_HOSTS[@]}"; do
    [[ -e $host/.git || -d $host/.git ]] || continue
    while IFS= read -r wt; do
      [[ -n $wt && $wt != "$host" ]] && PATHS+=("$wt")
    done < <(git -C "$host" worktree list --porcelain | awk '/^worktree /{print substr($0,10)}')
  done
fi

if [[ ${#PATHS[@]} -eq 0 ]]; then
  echo "no worktrees to check (pass a path, or --merged)" >&2
  exit 2
fi

mode="dry-run"
(( APPLY )) && mode="APPLY"
echo "galatea-wt-cleanup.sh ($mode)  $(date '+%Y-%m-%d %H:%M:%S %Z')"

is_in_use() {
  local prefix=$1 pid cwd fd
  for pid in /proc/[0-9]*; do
    cwd=$(readlink "$pid/cwd" 2>/dev/null) || continue
    if [[ $cwd == "$prefix" || $cwd == "$prefix"/* ]]; then
      echo "cwd $cwd pid ${pid##*/}"
      return 0
    fi
    if [[ -d $pid/fd ]]; then
      for fd in "$pid"/fd/*; do
        t=$(readlink "$fd" 2>/dev/null) || continue
        if [[ $t == "$prefix" || $t == "$prefix"/* ]]; then
          echo "fd $t pid ${pid##*/}"
          return 0
        fi
      done
    fi
  done
  return 1
}

meaningful_dirty() {
  local cwd=$1 line path
  while IFS= read -r line; do
    [[ -z $line ]] && continue
    path=${line:3}
    path=${path#* -> }
    path=${path//\"/}
    case $path in
      mcp-app-next/*|textclient/mcp-app-next/*|app-staging/*|textclient/app-staging/*|mcp-app-staging/*|textclient/mcp-app-staging/*|textclient/app/*) continue ;;
      */bin/*|bin/*|*/obj/*|obj/*|*TestResults*|/*TestResults) continue ;;
    esac
    printf '%s\n' "$line"
  done < <(git -C "$cwd" status --porcelain 2>/dev/null)
}

host_of() {
  local p=$1 common
  common=$(git -C "$p" rev-parse --git-common-dir 2>/dev/null) || { echo ""; return; }
  if [[ $common != /* ]]; then common=$(cd "$p" && cd "$common" && pwd); fi
  # common is .../foo/.git
  local host=${common%.git}
  host=${host%/}
  printf '%s\n' "$host"
}

# Returns 0 if this tree has no unique unpushed commits.
no_unpushed() {
  local p=$1 head
  head=$(git -C "$p" rev-parse HEAD 2>/dev/null) || return 1
  # known on any remote-tracking ref, or ancestor of origin/main, or a merged PR head
  if git -C "$p" merge-base --is-ancestor "$head" origin/main 2>/dev/null; then
    return 0
  fi
  if git -C "$p" log --oneline --not --remotes HEAD | grep -q .; then
    return 1
  fi
  return 0
}

pr_state_for() {
  # prints "NUMBER STATE" or "none"
  # Named feature branches are matched by branch name only. SHA matching is
  # only for detached HEADs, because a new branch created from origin/main
  # shares the latest merge-commit SHA and must not be treated as "that PR".
  local p=$1 branch head json
  branch=$(git -C "$p" rev-parse --abbrev-ref HEAD 2>/dev/null || true)
  head=$(git -C "$p" rev-parse HEAD 2>/dev/null || true)
  if [[ $branch == main ]]; then
    echo "none"
    return
  fi
  if [[ $branch != HEAD && -n $branch ]]; then
    json=$(gh pr list --repo davidabrooks/galatea --state all --head "$branch" --json number,state 2>/dev/null || true)
    python3 -c 'import json,sys
d=json.loads(sys.argv[1] or "[]")
print("%s %s" % (d[0]["number"], d[0]["state"]) if d else "none")
' "${json:-[]}"
    return
  fi
  json=$(gh pr list --repo davidabrooks/galatea --state all --search "$head" --limit 10 --json number,state,headRefOid,mergeCommit 2>/dev/null || true)
  python3 -c 'import json,sys
want=sys.argv[1]
d=json.loads(sys.argv[2] or "[]")
for p in d:
    mc=(p.get("mergeCommit") or {}).get("oid")
    if p.get("headRefOid")==want or mc==want:
        print("%s %s" % (p["number"], p["state"]))
        break
else:
    print("none")
' "$head" "${json:-[]}"
}

checked=0
would=0
skipped=0
did=0

for p in "${PATHS[@]}"; do
  p=${p%/}
  checked=$((checked+1))
  echo
  echo "== $p =="
  if [[ ! -e $p ]]; then
    echo "  SKIP missing"; skipped=$((skipped+1)); continue
  fi
  if [[ $p == "$HOST_DEFAULT" || $p == /workspace/galatea-sl-repo ]]; then
    echo "  SKIP host clone (will not remove the main or live-vision tree)"; skipped=$((skipped+1)); continue
  fi
  if ! git -C "$p" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    echo "  SKIP not a git worktree"; skipped=$((skipped+1)); continue
  fi
  host=$(host_of "$p")
  branch=$(git -C "$p" rev-parse --abbrev-ref HEAD 2>/dev/null || echo HEAD)
  head=$(git -C "$p" rev-parse --short HEAD 2>/dev/null || echo "?")
  echo "  host=$host branch=$branch head=$head"

  if used=$(is_in_use "$p"); then
    echo "  SKIP in use ($used)"; skipped=$((skipped+1)); continue
  fi
  dirty=$(meaningful_dirty "$p" || true)
  if [[ -n $dirty ]]; then
    echo "  SKIP uncommitted/untracked work:"
    echo "$dirty" | sed 's/^/    /'
    skipped=$((skipped+1)); continue
  fi
  if ! no_unpushed "$p"; then
    echo "  SKIP unpushed commits vs remotes"
    git -C "$p" log --oneline --not --remotes HEAD | sed 's/^/    /' | head
    skipped=$((skipped+1)); continue
  fi
  pr=$(pr_state_for "$p")
  echo "  PR $pr"
  state=${pr##* }
  if [[ $pr == none || ( $state != MERGED && $state != CLOSED ) ]]; then
    echo "  SKIP PR is not merged or closed"
    skipped=$((skipped+1)); continue
  fi

  if [[ $branch != HEAD && $branch != main && -n $branch ]]; then
    echo "  WOULD remove worktree, delete local branch $branch, prune $host"
  else
    echo "  WOULD remove worktree (detached; no feature branch to delete), prune $host"
  fi
  would=$((would+1))
  if (( ! APPLY )); then
    continue
  fi

  echo "  APPLY git -C $host worktree remove --force $p"
  if ! git -C "$host" worktree remove --force "$p"; then
    echo "  FAIL worktree remove"; skipped=$((skipped+1)); would=$((would-1)); continue
  fi
  if [[ $branch != HEAD && $branch != main && -n $branch ]]; then
    if git -C "$host" show-ref --verify --quiet "refs/heads/$branch"; then
      # only delete if no remaining worktree uses it
      if ! git -C "$host" worktree list --porcelain | grep -q "branch refs/heads/$branch"; then
        echo "  APPLY git -C $host branch -D $branch"
        git -C "$host" branch -D "$branch" || true
      fi
    fi
    # delete remote branch if it is still on origin (merged branches are often already gone)
    if git -C "$host" ls-remote --exit-code origin "refs/heads/$branch" >/dev/null 2>&1; then
      echo "  APPLY git -C $host push origin --delete $branch"
      git -C "$host" push origin --delete "$branch" || echo "  warning: remote branch delete failed"
    else
      echo "  remote branch origin/$branch already gone"
    fi
  fi
  git -C "$host" worktree prune
  echo "  REMOVED $p"
  did=$((did+1))
done

echo
echo "checked=$checked would_remove=$would skipped=$skipped applied=$did mode=$mode"
(( APPLY )) || echo "dry-run only; pass --apply to remove"
