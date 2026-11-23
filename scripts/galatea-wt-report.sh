#!/usr/bin/env bash
# Read-only report of Galatea working copies under /workspace.
# Prints size, kind, branch, PR state, unpushed/uncommitted, last modified,
# and whether the tree looks safe to delete under the mechanical cleanup checks
# (merged/closed PR, no unique work, not in use). Does not delete anything.
#
#   galatea-wt-report.sh
set -euo pipefail

echo "galatea-wt-report.sh  $(date '+%Y-%m-%d %H:%M:%S %Z')  (read-only)"
echo

python3 - << 'PY'
import os, sys, json, time, subprocess
from pathlib import Path
from collections import defaultdict

WS = Path("/workspace")
HOSTS = [
    WS / "galatea",
    WS / "galatea-sl-repo",
]
IGNORE_PREFIXES = (
    "mcp-app-next/", "textclient/mcp-app-next/",
    "app-staging/", "textclient/app-staging/",
    "mcp-app-staging/", "textclient/mcp-app-staging/",
    "textclient/app/",
)

def human(n):
    f=float(n or 0); i=0; u=["B","KB","MB","GB"]
    while f>=1024 and i<3:
        f/=1024; i+=1
    return f"{f:.1f} {u[i]}" if i else f"{int(n or 0)} B"

def run(args, cwd=None, timeout=60):
    try:
        r=subprocess.run(args, cwd=str(cwd) if cwd else None, capture_output=True, timeout=timeout)
        return r.returncode, r.stdout.decode("utf-8","replace"), r.stderr.decode("utf-8","replace")
    except Exception as e:
        return 98, "", str(e)

def git(cwd, *args, timeout=60):
    return run(["git", *args], cwd=cwd, timeout=timeout)

def du(path):
    c,o,e=run(["du","-sb",str(path)], timeout=180)
    if c==0 and o.strip():
        return int(o.split()[0])
    return 0

def newest_src(path):
    newest=0.0
    try:
        for dirpath, dirnames, filenames in os.walk(path, followlinks=False):
            dp=Path(dirpath)
            dirnames[:]=[d for d in dirnames if d!=".git" and d not in ("bin","obj","app-staging","mcp-app-staging","mcp-app-next","TestResults") and not (dp/d).is_symlink()]
            rel=dp.relative_to(path).parts if dp!=path else ()
            if any(x in ("bin","obj","app-staging","mcp-app-staging","mcp-app-next","TestResults") for x in rel):
                continue
            for fn in filenames:
                fp=dp/fn
                try:
                    if fp.is_symlink(): continue
                    mt=fp.stat().st_mtime
                except OSError:
                    continue
                if mt>newest: newest=mt
    except OSError:
        pass
    if not newest:
        return ""
    return time.strftime("%Y-%m-%d %H:%M PT", time.localtime(newest))

def in_use(path):
    prefix=str(path)
    for pid in os.listdir("/proc"):
        if not pid.isdigit(): continue
        try:
            cwd=os.readlink(f"/proc/{pid}/cwd")
        except Exception:
            continue
        if cwd==prefix or cwd.startswith(prefix+"/"):
            return True
        try:
            for fd in os.listdir(f"/proc/{pid}/fd"):
                try:
                    t=os.readlink(f"/proc/{pid}/fd/{fd}")
                except Exception:
                    continue
                if t==prefix or t.startswith(prefix+"/"):
                    return True
        except Exception:
            pass
    return False

def dirty(cwd):
    c,o,e=git(cwd,"status","--porcelain")
    if c!=0:
        return [f"status-failed:{(e or o).strip()[:80]}"]
    out=[]
    for ln in o.splitlines():
        path=ln[3:] if len(ln)>3 else ln
        if " -> " in path: path=path.split(" -> ",1)[-1]
        path=path.strip().strip('"')
        if any(path==p.rstrip("/") or path.startswith(p) for p in IGNORE_PREFIXES):
            continue
        if "/bin/" in f"/{path}/" or "/obj/" in f"/{path}/" or "TestResults" in path:
            continue
        out.append(ln)
    return out

def discover():
    found=[]
    for root, dirs, files in os.walk(WS):
        p=Path(root)
        rel=p.relative_to(WS)
        if len(rel.parts)>3:
            dirs[:]=[]
            continue
        if any(x in p.parts for x in (".git","node_modules","src-libremetaverse","bin","obj")):
            dirs[:]=[]
            continue
        dirs[:]=[d for d in dirs if d not in {".git","node_modules","bin","obj","src-libremetaverse","app-staging","mcp-app-staging","mcp-app-next"}]
        if (p/".git").exists():
            found.append(p)
    return found

def is_galatea(p):
    c,o,e=git(p,"remote","get-url","origin")
    if "davidabrooks/galatea" in (o or "").lower():
        return True
    gitfile=p/".git"
    if gitfile.is_file():
        try:
            t=gitfile.read_text()
        except Exception:
            t=""
        if "galatea" in t.lower() or "galatea-sl-repo" in t:
            return True
    if (p/"textclient").is_dir() and (p/"AGENTS.md").exists():
        return True
    return False

# --- classify helpers (tested) ---
def is_deploy_branch(branch):
    return bool(branch) and branch != "(detached)" and branch.startswith("deploy/")

def newest_deploy_branch(branches):
    deps = [b for b in branches if is_deploy_branch(b)]
    return max(deps) if deps else None

def pr_for(branch, detached, head, by_branch, by_sha):
    # Named branches: match like `gh pr list --head <branch>` only. SHA matching
    # is only for detached HEADs, because a branch created from origin/main
    # shares the latest merge-commit SHA and must not be treated as "that PR".
    if not detached and branch in by_branch:
        return by_branch[branch]
    if detached and head in by_sha:
        return by_sha[head]
    return None

def classify(r, newest_deploy):
    hostish = r["path"] in ("/workspace/galatea", "/workspace/galatea-sl-repo")
    if hostish:
        return "KEEP (host clone)"
    if is_deploy_branch(r["branch"]) and r["branch"] == newest_deploy:
        return "KEEP (live rollback)"
    if r["in_use"]:
        return "KEEP (in use)"
    if r["dirty"]:
        return "KEEP (uncommitted)"
    if r["unpushed"].startswith("none") is False and r["unpushed"] != "—":
        return "KEEP (unpushed)"
    if is_deploy_branch(r["branch"]):
        # superseded by a newer deploy/* tree; SAFE only if otherwise clean and pushed
        return "SAFE (mechanical)"
    if r["pr_state"] in ("MERGED", "CLOSED"):
        return "SAFE (mechanical)"
    if r["pr_state"] == "":
        return "LOOK (no PR match)"
    return f"KEEP (PR {r['pr_state']})"
# --- end classify helpers ---

# PRs once
c,o,e=run(["gh","pr","list","--repo","davidabrooks/galatea","--state","all","--limit","500","--json","number,state,headRefName,headRefOid,mergeCommit"], timeout=60)
prs=[]
try:
    prs=json.loads(o) if o.strip() else []
except Exception:
    prs=[]
by_branch={p["headRefName"]:p for p in prs}
by_sha={}
for p in prs:
    if p.get("headRefOid"): by_sha[p["headRefOid"]]=p
    mc=(p.get("mergeCommit") or {}).get("oid")
    if mc: by_sha[mc]=p

copies=[]
for p in discover():
    if not is_galatea(p):
        continue
    # skip broken submodule sliver
    if str(p).endswith("sl-texture-vision"):
        continue
    kind="standalone" if (p/".git").is_dir() else "worktree"
    c,head,e=git(p,"rev-parse","HEAD")
    head=head.strip() if c==0 else ""
    c,br,e=git(p,"rev-parse","--abbrev-ref","HEAD")
    branch=br.strip() if c==0 else ""
    detached = (branch=="HEAD" or not branch)
    if detached:
        branch="(detached)"
    pr=pr_for(branch, detached, head, by_branch, by_sha)
    pr_s="—"
    pr_state=""
    if pr:
        pr_s=f"#{pr['number']} {pr['state']}"
        pr_state=pr["state"]
    dty=dirty(p) if (p/".git").exists() and kind in ("standalone","worktree") else []
    # unpushed
    unpushed="—"
    if head:
        c,o,e=git(p,"log","--oneline","--not","--remotes","HEAD")
        commits=[ln for ln in o.splitlines() if ln.strip()]
        unpushed = f"{len(commits)} unique" if commits else "none"
        if detached and head in by_sha:
            unpushed="none (known PR sha)"
    used=in_use(p)
    size=du(p)
    copies.append({
        "path":str(p), "kind":kind, "size":size, "branch":branch,
        "head":head[:12], "pr":pr_s, "pr_state":pr_state,
        "unpushed":unpushed, "dirty":len(dty), "dirty_s":"; ".join(dty[:3]),
        "mtime":newest_src(p), "in_use":used,
    })

# classify mechanical safe
newest_deploy = newest_deploy_branch(r["branch"] for r in copies)
for r in copies:
    r["verdict"] = classify(r, newest_deploy)

copies.sort(key=lambda r: (-r["size"], r["path"]))
print(f"{'Size':>9}  {'Kind':<11}  {'PR':<18}  {'Unpushed':<16}  {'Dirty':>5}  {'In use':<6}  {'Source mtime':<18}  Verdict  Path  Branch")
print("-"*160)
tot=0
by_v=defaultdict(int)
by_n=defaultdict(int)
for r in copies:
    tot+=r["size"]
    by_v[r["verdict"].split()[0]] += r["size"]
    by_n[r["verdict"].split()[0]] += 1
    print(f"{human(r['size']):>9}  {r['kind']:<11}  {r['pr']:<18}  {r['unpushed']:<16}  {r['dirty']:>5}  {str(r['in_use']).lower():<6}  {r['mtime'] or '—':<18}  {r['verdict']}  {r['path']}  {r['branch']}")
    if r["dirty_s"]:
        print(f"           dirty: {r['dirty_s']}")

print("-"*160)
print(f"{len(copies)} copies, {human(tot)} total")
for k in ("KEEP","SAFE","LOOK"):
    if by_n[k]:
        print(f"  {k}: {by_n[k]}  {human(by_v[k])}")
# leftover non-git Galatea dirs
print()
print("Non-git leftovers (not in the table above):")
for name in ("galatea-run","galatea-branch-archive","galatea-git-broken-20261021"):
    q=WS/name
    if q.exists():
        print(f"  {human(du(q)):>9}  {q}  (not a git checkout)")
    else:
        print(f"  missing  {q}")
print()
print("Dated test/validation leftovers (flag if older than 7 days):")
import fnmatch, time as _time
now = _time.time()
patterns = ("*-validation-*", "*-review-*", "*-audit-*", "deploy-pr*")
# also match /workspace/sl-dependency-audit-* and nested deploy-pr*
found = []
skip_roots = {"secondlife", "secondlife-backups", "gdrive-write-mcp", "ios", "finances",
              "starcitizen", "app-ideas", "ebike-scout", "parlor", "galatea", "galatea-sl-repo",
              "galatea-git-broken-20261021", "remaining-three-validation-20261108"}
try:
    for child in sorted(WS.iterdir(), key=lambda q: q.name):
        if child.name in skip_roots or child.name.startswith("."):
            continue
        name = child.name
        match = any(fnmatch.fnmatch(name, pat) for pat in patterns)
        if not match:
            continue
        try:
            mtime = child.stat().st_mtime
        except OSError:
            continue
        age_days = (now - mtime) / 86400.0
        flag = "FLAG (>7d)" if age_days > 7 else "recent"
        found.append((du(child), child, age_days, flag))
except OSError:
    pass
if not found:
    print("  none")
else:
    for sz, path, age, flag in found:
        print(f"  {human(sz):>9}  {flag:<10}  {age:.1f}d  {path}")
    nflag = sum(1 for *_, flag in found if flag.startswith("FLAG"))
    print(f"  {len(found)} folder(s), {nflag} older than 7 days. Report only; delete after the result is in notes.")

print()
print("SAFE (mechanical) means: merged/closed PR (or a deploy/* worktree superseded by a newer one), no unique uncommitted or unpushed work, not in use, not a host clone. The newest deploy/* worktree is KEEP (live rollback).")
print("This is a report only. Confirm Look/Keep items (live vision tree, live rollback, unique work) before deleting.")
PY
