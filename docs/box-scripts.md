# Box scripts: build, test, ship, deploy, upstream PRs, dependency scan

These replace steps that used to be done by hand with many tool calls. Each prints a short summary and keeps full logs in
`/tmp`. Install or refresh the box copies in `/home/box/tools/bin` after a merge with `scripts/install-box-tools.sh`.

| Script | What it does |
|---|---|
| `scripts/galatea-test.sh [--full\|--impact\|--csharp\|--python] [--no-build]` | Sets the .NET path. `--full` (default) = `build-all.sh` + xUnit + `pytest -q`, like CI. `--impact` = `impact_tests.py`. One line per suite. |
| `scripts/galatea-ship.sh [--title T] [--body-file F] [--cleanup] [WORKTREE]` | Push the branch, open or reuse the PR, wait for CI, `gh pr merge --merge --match-head-commit`, print the merge commit, then dry-run (or `--cleanup`: apply) `galatea-wt-cleanup.sh`. `--pr N` merges an existing PR. `--no-merge` only opens. |
| `scripts/pr-automerge.sh N` | Old name; now `galatea-ship.sh --pr N`. |
| `textclient/deploy.sh --tag prNNN [--dry-run] <worktree>/textclient` | Offline deploy of `app-staging` + `mcp-app-staging`: refuses while logged in or without `run/deliberate-stop`, backs up to `app-backups/deploy-<ts>-<tag>-<sha7>` (hash-checked), swaps and hash-checks every file, runs `restart-connector.sh` (never `--force`), checks the child through `/proc`, writes `notes-entry.md`. Renderer step (2026-11-22): syncs every repo-tracked `vision/` file at the staged commit into `/workspace/galatea-sl-repo/vision` (the live renderer look.py uses; Photographer's HD renders share it), after a backup to `<backup>/vision` + `vision-backup-sha256.json`, then sha256-checks them (`renderer-installed-sha256.json`); refuses (exit 2) on renderer files whose content is in no git history, and (exit 3) while a render from that tree is running; untracked files and the checkout's git state are never touched. `--vision DIR` / `--no-vision`. |
| `textclient/deploy.sh --rollback <backup-dir>` | Same checks; backs up the current live to `app-backups/rollback-pre-<ts>` and installs the backup's `app` + `mcp-app`, and its `vision/` files when the backup has them (same refusals). |
| `scripts/lmv-pr.sh new\|commit\|test\|open\|cleanup\|list` | LibreMetaverse upstream PRs from worktrees of `/workspace/wt/lmv-main`: commits as David Brooks, tests with the right filter, opens the PR from the fork against `cinderblocks/libremetaverse` only after a passing test of HEAD, cleans up after merge. |
| `scripts/dep-scan.sh [--tree DIR] [--baseline FILE]` | Read-only NuGet vulnerable/outdated scan, deployed-vs-source drift, LibreMetaverse pin vs upstream, .NET servicing, Python pins against OSV; diff against the last weekly baseline. |

Not scriptable: pausing/resuming the keep-awake routines (agent routine tool) and logging in/out (connector login/logout tools).
