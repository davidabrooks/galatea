#!/usr/bin/env bash
# deploy.sh (2026-11-18): install a staged text-client + MCP-connector build into the live textclient dir, OFFLINE only.
# Replaces the per-deploy deploy.py copies that lived in (and were deleted with) evidence folders.
#
#   deploy.sh --tag prNNN [options] <staging-textclient-dir>     # deploy app-staging + mcp-app-staging
#   deploy.sh --rollback <backup-dir> [options]                   # put a deploy backup back live
#
# <staging-textclient-dir> is the textclient/ of a worktree where `build-all.sh --stage-mcp` ran (it must hold
# app-staging/ and mcp-app-staging/). Deploy steps, each checked before the next:
#   1. preflight: staging has both DLLs and patched LibreMetaverse (check-lmv-patches.sh); staged commit is merged into
#      origin/main and the tree is clean (else refuse unless --allow-unmerged); Galatay is OFFLINE (no live sl-session.pid
#      holder, no galatay-text process) and run/deliberate-stop exists (else exit 3); enough disk.
#   2. backup: app/, mcp-app/, routes/ -> app-backups/deploy-<ts>-<tag>-<sha7>/, hash-compared with live (backup-sha256.json).
#   3. swap: copy staging to a temp dir, hash-check, rename into app/ and mcp-app/, hash-check live == staging
#      (installed-sha256.json). Any mismatch puts the previous live dirs back and exits 1. A leftover live mcp-app-next/
#      (which the child launcher would swap in at restart) is moved into the backup first.
#   4. restart-connector.sh --run-dir <live>/run (never --force), then /proc check: each child execs <live>/mcp-app,
#      its galatay-mcp.dll hash = staged, NETCore.App version. Skipped with --no-restart.
#   5. deliberate-stop mtime unchanged; helper scripts and tracked routes that differ from the staged tree are LISTED
#      (never copied); notes entry written to <backup>/notes-entry.md and printed (and appended to --append-notes FILE).
# Rollback: same checks, backs up the current live to app-backups/rollback-pre-<ts>/, then installs <backup>/app and
# <backup>/mcp-app the same way (routes are not touched; restore <backup>/routes by hand if needed).
#
# Renderer step (2026-11-22, David t3470u): the live photo/seat renderer is the vision/ tree of /workspace/galatea-sl-repo (look.py reads it
# there; Photographer's HD renders use the same files). On a deploy, every file the repo tracks under vision/ at the staged commit is synced into
# that tree (files the checkout's HEAD tracks but the commit removed are removed); nothing else there is touched (untracked files, __pycache__,
# the checkout's git state). Before it: the renderer must be idle (no process running look.py / render_mesh.py / Blender from that tree, else
# exit 3) and every live renderer file that differs from the commit must be "accounted for", i.e. its content is in that file's git history
# (staged commit, or the renderer checkout's own history); anything else is unknown local work and the deploy refuses (exit 2), writing nothing.
# Backup: those files -> <backup>/vision/ + vision-backup-sha256.json (absent = null); after the sync every owned file is sha256-checked
# (renderer-installed-sha256.json); a mismatch restores the backup and exits 1 before the client is swapped. Rollback restores <backup>/vision
# the same way when the backup has one. --vision DIR picks another renderer tree; --no-vision skips the step (default for a scratch --live).
#
# Options: --dry-run         print the plan and every check; write nothing, restart nothing
#          --no-restart      swap but do not restart the connector (required when --live is not the real live dir)
#          --live DIR        live textclient dir (default /home/box/viewers/textclient)
#          --viewers DIR     dir with the live helper scripts (default: parent of --live)
#          --allow-unmerged  allow a staged commit not on origin/main or a dirty tree
#          --append-notes F  append the notes entry to F
#          --vision DIR      live renderer checkout (default /workspace/galatea-sl-repo for the real live dir; none for a scratch --live)
#          --no-vision       skip the renderer step
# Exit: 0 ok, 1 verify failed (rolled back where possible), 2 usage/preflight, 3 refused (logged in / no deliberate-stop),
#       4 swap ok but connector restart or /proc check failed (rollback command printed).
set -uo pipefail
exec python3 - "$(cd "$(dirname "$0")" && pwd)" "$@" <<'PY'
import hashlib, json, os, re, shutil, subprocess, sys, time

HERE = sys.argv[1]; args = sys.argv[2:]
REAL_LIVE = "/home/box/viewers/textclient"
REAL_VISION = "/workspace/galatea-sl-repo"
o = dict(dry=False, norestart=False, live=REAL_LIVE, viewers=None, unmerged=False, notes=None, tag=None, rollback=None, stage=None, vision=None, novision=False)
def usage(msg=""):
    if msg: print("usage error:", msg)
    print("usage: deploy.sh --tag TAG [--dry-run] [--no-restart] [--live DIR] [--viewers DIR] [--allow-unmerged] [--append-notes F] [--vision DIR | --no-vision] <staging-textclient-dir>")
    print("       deploy.sh --rollback BACKUP_DIR [--dry-run] [--no-restart] [--live DIR] [--append-notes F] [--vision DIR | --no-vision]")
    sys.exit(2)
i = 0
while i < len(args):
    a = args[i]
    def val():
        global i
        if i + 1 >= len(args): usage(f"{a} needs a value")
        i += 1; return args[i]
    if a == "--dry-run": o["dry"] = True
    elif a == "--no-restart": o["norestart"] = True
    elif a == "--allow-unmerged": o["unmerged"] = True
    elif a == "--live": o["live"] = os.path.abspath(val())
    elif a == "--viewers": o["viewers"] = os.path.abspath(val())
    elif a == "--append-notes": o["notes"] = os.path.abspath(val())
    elif a == "--tag": o["tag"] = val()
    elif a == "--rollback": o["rollback"] = os.path.abspath(val())
    elif a == "--vision": o["vision"] = os.path.abspath(val())
    elif a == "--no-vision": o["novision"] = True
    elif a in ("-h", "--help"): usage()
    elif a.startswith("-"): usage(f"unknown option {a}")
    elif o["stage"] is None: o["stage"] = os.path.abspath(a)
    else: usage("only one staging dir")
    i += 1
if bool(o["rollback"]) == bool(o["stage"]): usage("give exactly one of <staging-textclient-dir> or --rollback BACKUP_DIR")
if o["stage"] and not o["tag"]: usage("--tag is required for a deploy (e.g. pr469 or pr463-466)")
if o["tag"] and not re.fullmatch(r"[A-Za-z0-9._-]+", o["tag"]): usage("bad --tag")
LIVE = o["live"].rstrip("/"); RUN = os.path.join(LIVE, "run"); VIEWERS = o["viewers"] or os.path.dirname(LIVE)
DRY = o["dry"]; is_real_live = os.path.realpath(LIVE) == os.path.realpath(REAL_LIVE)
if not is_real_live and not o["norestart"]: usage("--live is not the real live dir: add --no-restart (the restart would hit the real connector)")
VISION = None if o["novision"] else (o["vision"] or (REAL_VISION if is_real_live else None))
if VISION: VISION = VISION.rstrip("/")
TS = time.strftime("%Y%m%d-%H%M%S"); NOW = time.strftime("%Y-%m-%d %-I:%M %p %Z")
log = []
def say(m): print(m); log.append(m)
def fail(code, m):
    print(("DRY RUN: " if DRY else "") + "STOP: " + m); sys.exit(code)

def manifest(root):
    out = {}
    for d, dirs, files in os.walk(root):
        dirs.sort()
        for f in sorted(files) + [x for x in dirs if os.path.islink(os.path.join(d, x))]:
            p = os.path.join(d, f); rel = os.path.relpath(p, root)
            if os.path.islink(p): out[rel] = "link:" + os.readlink(p)
            else:
                h = hashlib.sha256()
                with open(p, "rb") as fh:
                    for b in iter(lambda: fh.read(1 << 20), b""): h.update(b)
                out[rel] = h.hexdigest()
    return out
def mdiff(a, b):
    return sorted(set(b) - set(a)), sorted(set(a) - set(b)), sorted(k for k in a.keys() & b.keys() if a[k] != b[k])
def same(a, b): return a == b
def sha(p):
    try: return hashlib.sha256(open(p, "rb").read()).hexdigest()
    except OSError: return None
def short(h): return f"{h[:8]}…{h[-4:]}" if h else "missing"
def run(cmd, cwd=None):
    r = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True); return r.returncode, (r.stdout + r.stderr).strip()
def alive(pid):
    try: os.kill(pid, 0); return True
    except OSError: return False
def comm(pid):
    try: return open(f"/proc/{pid}/comm").read().strip()
    except OSError: return ""
def dsize(p):
    t = 0
    for d, _, fs in os.walk(p):
        for f in fs:
            try: t += os.lstat(os.path.join(d, f)).st_size
            except OSError: pass
    return t

# ---------- source of the new build ----------
if o["stage"]:
    ST = o["stage"]; SRC_APP = os.path.join(ST, "app-staging"); SRC_MCP = os.path.join(ST, "mcp-app-staging")
    for d, dll in ((SRC_APP, "galatay-text.dll"), (SRC_MCP, "galatay-mcp.dll")):
        if not os.path.isfile(os.path.join(d, dll)): fail(2, f"{d}/{dll} missing: run build-all.sh --stage-mcp in {ST}")
    rc, out = run([os.path.join(HERE, "check-lmv-patches.sh"), SRC_APP, SRC_MCP])
    say(out)
    if rc: fail(2, "staged LibreMetaverse.dll is unpatched")
    rc, head = run(["git", "-C", ST, "rev-parse", "HEAD"]); head = head if rc == 0 else "nogit"
    _, subj = run(["git", "-C", ST, "log", "-1", "--format=%s"])
    sha7 = head[:7]
    problems = []
    if head == "nogit": problems.append(f"{ST} is not a git checkout")
    else:
        run(["git", "-C", ST, "fetch", "-q", "origin", "main"])
        if run(["git", "-C", ST, "merge-base", "--is-ancestor", head, "origin/main"])[0]: problems.append(f"{sha7} is not on origin/main (unmerged)")
        _, st = run(["git", "-C", ST, "status", "--porcelain", "--", ".", ":!mcp-app-next", ":!app-staging", ":!mcp-app-staging"])
        if st: problems.append("uncommitted changes in the staging tree:\n    " + st.replace("\n", "\n    "))
        nxt = os.path.join(ST, "mcp-app-next")
        if os.path.isdir(nxt):
            a, r, c = mdiff(manifest(SRC_MCP), {k: v for k, v in manifest(nxt).items() if k != "READY"})
            if a or r or c: problems.append("mcp-app-next differs from mcp-app-staging (stale --stage-mcp?)")
    for p in problems: say(("WARN (allowed): " if o["unmerged"] else "PROBLEM: ") + p)
    if problems and not o["unmerged"] and not DRY: fail(2, "refusing; fix the above or pass --allow-unmerged")
    label = f"{o['tag']} @ {sha7}"
else:
    B0 = o["rollback"]; SRC_APP = os.path.join(B0, "app"); SRC_MCP = os.path.join(B0, "mcp-app")
    for d, dll in ((SRC_APP, "galatay-text.dll"), (SRC_MCP, "galatay-mcp.dll")):
        if not os.path.isfile(os.path.join(d, dll)): fail(2, f"{d}/{dll} missing: not a deploy backup")
    bm = os.path.join(B0, "backup-sha256.json")
    if os.path.isfile(bm):
        want = json.load(open(bm))
        for k, d in (("app", SRC_APP), ("mcp-app", SRC_MCP)):
            if k in want and manifest(d) != want[k]: fail(2, f"backup {d} no longer matches its backup-sha256.json")
        say(f"backup integrity OK against {bm}")
    else: say(f"note: {bm} absent (older backup); not integrity-checked")
    rc, out = run([os.path.join(HERE, "check-lmv-patches.sh"), SRC_APP, SRC_MCP]); say(out)
    if rc: fail(2, "backup LibreMetaverse.dll is unpatched")
    sha7 = "rollback"; head = None; subj = ""; label = f"rollback to {os.path.basename(B0)}"

# ---------- live state ----------
for d in ("app", "mcp-app"):
    if not os.path.isdir(os.path.join(LIVE, d)): fail(2, f"{LIVE}/{d} missing")
held = None
try:
    pid = int(open(os.path.join(RUN, "sl-session.pid")).read().strip())
    if alive(pid) and comm(pid).startswith("galatay"): held = pid
except Exception: pass
if held: fail(3, f"Galatay is logged in (session held by pid {held}); log out first (login/logout skill), then deploy")
if is_real_live:
    rc, out = run(["pgrep", "-x", "galatay-text"])
    if rc == 0: fail(3, f"a galatay-text process is running (pids {out.split()}); stop it first")
marker = os.path.join(RUN, "deliberate-stop")
if not os.path.exists(marker): fail(3, f"{marker} missing: create it at logout so the keep-awake check does not relog mid-deploy")
marker_m = os.stat(marker).st_mtime
say(f"offline: no session holder; deliberate-stop present ({time.strftime('%F %T', time.localtime(marker_m))})")

live_app_m, live_mcp_m = manifest(os.path.join(LIVE, "app")), manifest(os.path.join(LIVE, "mcp-app"))
src_app_m, src_mcp_m = manifest(SRC_APP), manifest(SRC_MCP)
old_text, old_mcp = live_app_m.get("galatay-text.dll"), live_mcp_m.get("galatay-mcp.dll")
new_text, new_mcp = src_app_m.get("galatay-text.dll"), src_mcp_m.get("galatay-mcp.dll")
say(f"live now : app {len(live_app_m)} files text {short(old_text)}; mcp-app {len(live_mcp_m)} files mcp {short(old_mcp)}")
say(f"incoming : app {len(src_app_m)} files text {short(new_text)}; mcp-app {len(src_mcp_m)} files mcp {short(new_mcp)}  ({label}{(': ' + subj) if subj else ''})")
for name, a, b in (("app", live_app_m, src_app_m), ("mcp-app", live_mcp_m, src_mcp_m)):
    ad, rm, ch = mdiff(a, b)
    say(f"  {name}: {len(ch)} changed, {len(ad)} added, {len(rm)} removed" + (f" (e.g. {', '.join((ch + ad + rm)[:6])})" if ch or ad or rm else ""))
if live_app_m == src_app_m and live_mcp_m == src_mcp_m: say("note: incoming build is identical to live")

helper_diffs = []
if o["stage"]:
    for f in ("galatay-mcp-proxy.py", "galatay-mcp-child.sh", "galatay-mcp.sh", "galatay-autostart.sh", "text-galatay.sh"):
        lp, sp = os.path.join(VIEWERS, f), os.path.join(ST, f)
        if os.path.isfile(sp) and sha(lp) != sha(sp): helper_diffs.append(f"{VIEWERS}/{f}")
    rc, tracked = run(["git", "-C", ST, "ls-files", "routes"])
    for rel in (tracked.split("\n") if rc == 0 and tracked else []):
        rel = os.path.relpath(os.path.join(ST, rel), ST) if not rel.startswith("routes/") else rel
        if sha(os.path.join(LIVE, rel)) != sha(os.path.join(ST, rel)): helper_diffs.append(f"{LIVE}/{rel}")
    say("helper/route files differing from the staged tree (NOT copied; review): " + (", ".join(helper_diffs) if helper_diffs else "none"))

# ---------- renderer (vision/) plan ----------
def gitb(cwd, *a):
    r = subprocess.run(["git", "-C", cwd, *a], capture_output=True); return r.returncode, r.stdout
def blob_of_file(p):
    data = open(p, "rb").read(); return hashlib.sha1(b"blob %d\0" % len(data) + data).hexdigest()
def known_blob(path, blob, repos):
    for repo, ref in repos:
        if not repo: continue
        rc, out = gitb(repo, "log", "--format=%H", *(ref if isinstance(ref, list) else [ref]), "--", ":/" + path)
        for c in (out.decode().split() if rc == 0 else []):
            rc2, b = gitb(repo, "rev-parse", f"{c}:{path}")
            if rc2 == 0 and b.decode().strip() == blob: return True
    return False
def renderer_busy(root):
    hits = []; me = os.getpid()
    for d in os.listdir("/proc"):
        if not d.isdigit() or int(d) in (me, os.getppid()): continue
        try:
            cmd = open(f"/proc/{d}/cmdline", "rb").read().replace(b"\0", b" ").decode(errors="replace")
            cwd = os.readlink(f"/proc/{d}/cwd")
        except OSError: continue
        if "deploy.sh" in cmd: continue
        vroot = os.path.join(root, "vision")
        if vroot in cmd or cwd == vroot or cwd.startswith(vroot + "/"): hits.append(f"pid {d}: {cmd.strip()[:120]}")
    return hits
vplan = None   # dict(target={rel: sha256 or None}, data={rel: bytes}, changed=[...], removed=[...], unknown=[...], busy=[...], untouched=n)
if VISION and o["stage"] and head != "nogit":
    if not os.path.isdir(os.path.join(VISION, "vision")): fail(2, f"renderer tree {VISION}/vision missing (pass --no-vision to skip the renderer step)")
    rc, ls = gitb(ST, "ls-tree", "--full-tree", "-r", "-z", head, "vision/")
    if rc: fail(2, f"cannot list vision/ at {sha7} in {ST}")
    target, data = {}, {}
    for ent in ls.split(b"\0"):
        if not ent: continue
        meta_, rel = ent.split(b"\t", 1); mode, typ, obj = meta_.decode().split()
        if typ != "blob" or mode == "120000": continue
        rel = rel.decode(); rc, blob = gitb(ST, "cat-file", "blob", obj); data[rel] = blob; target[rel] = hashlib.sha256(blob).hexdigest()
    rc, headls = gitb(VISION, "ls-files", "-z", "--", "vision/")
    tracked_live = set(x.decode() for x in headls.split(b"\0") if x) if rc == 0 else set()
    gone = sorted(r for r in tracked_live - set(target) if os.path.isfile(os.path.join(VISION, r)))
    repos = [(ST, head), (VISION, ["--all"])]
    changed, unknown = [], []
    for rel in sorted(target):
        lp = os.path.join(VISION, rel)
        if not os.path.isfile(lp): changed.append(rel); continue
        if sha(lp) == target[rel]: continue
        changed.append(rel)
        if not known_blob(rel, blob_of_file(lp), repos): unknown.append(rel)
    for rel in gone:
        if not known_blob(rel, blob_of_file(os.path.join(VISION, rel)), repos): unknown.append(rel)
    other = 0
    for d, dirs, files in os.walk(os.path.join(VISION, "vision")):
        dirs[:] = [x for x in dirs if x != "__pycache__"]
        for f in files:
            rel = os.path.relpath(os.path.join(d, f), VISION)
            if rel not in target and rel not in tracked_live and not f.endswith(".pyc"): other += 1
    vplan = dict(target=target, data=data, changed=changed, removed=gone, unknown=unknown, busy=renderer_busy(VISION), untouched=other)
    say(f"renderer: {VISION}/vision: {len(target)} repo files at {sha7}; {len(changed)} to update, {len(gone)} to remove"
        + (f" (e.g. {', '.join((changed + gone)[:6])})" if changed or gone else "") + f"; {other} untracked file(s) there left alone")
    if not changed and not gone: say("renderer: already identical to the commit")
    for u in unknown: say(f"PROBLEM: renderer {u} has local content that is in no git history (unknown local work): not overwriting it")
    for b in vplan["busy"]: say(f"PROBLEM: renderer busy: {b}")
    if vplan["busy"] and not DRY: fail(3, "the renderer is in use (a render from that tree is running, e.g. Photographer's HD render); wait for it to finish")
    if unknown and not DRY: fail(2, f"refusing: {len(unknown)} renderer file(s) with unaccounted local changes in {VISION}; commit or move them, or pass --no-vision")
elif VISION and not o["stage"]:
    vb = os.path.join(o["rollback"], "vision-backup-sha256.json")
    if os.path.isfile(vb):
        want = json.load(open(vb)); data = {}
        for rel, h in want.items():
            if h is None: continue
            bp = os.path.join(o["rollback"], rel)
            if sha(bp) != h: fail(2, f"backup {bp} no longer matches vision-backup-sha256.json")
            data[rel] = open(bp, "rb").read()
        unknown = []
        for rel, h in want.items():
            lp = os.path.join(VISION, rel)
            if os.path.isfile(lp) and sha(lp) != h and not known_blob(rel, blob_of_file(lp), [(VISION, ["--all"])]): unknown.append(rel)
        changed = [r for r, h in want.items() if h is not None and sha(os.path.join(VISION, r)) != h]
        removed = [r for r, h in want.items() if h is None and os.path.isfile(os.path.join(VISION, r))]
        vplan = dict(target=want, data=data, changed=changed, removed=removed, unknown=unknown, busy=renderer_busy(VISION), untouched=0)
        say(f"renderer: restore {len(changed)} file(s), remove {len(removed)} from {o['rollback']}/vision into {VISION}")
        for u in unknown: say(f"PROBLEM: renderer {u} has local content that is in no git history: not overwriting it")
        for b in vplan["busy"]: say(f"PROBLEM: renderer busy: {b}")
        if vplan["busy"] and not DRY: fail(3, "the renderer is in use; wait for it to finish")
        if unknown and not DRY: fail(2, "refusing: unaccounted renderer changes; pass --no-vision to roll back the client only")
    else: say("renderer: this backup has no vision/ (older deploy): renderer left as it is")
elif VISION and head == "nogit": say("renderer: staging is not a git checkout: renderer step skipped")
else: say("renderer: step skipped" + (" (--no-vision)" if o["novision"] else " (scratch --live; pass --vision DIR to include it)"))

stale_next = os.path.join(LIVE, "mcp-app-next")
if os.path.exists(stale_next): say(f"live {stale_next} exists: it will be moved into the backup (the child launcher would swap it in at restart)")
need = dsize(SRC_APP) + dsize(SRC_MCP) + dsize(os.path.join(LIVE, "app")) + dsize(os.path.join(LIVE, "mcp-app"))
free = shutil.disk_usage(LIVE).free
if free < need * 1.5: fail(2, f"not enough disk: need ~{need*1.5/1e6:.0f} MB, free {free/1e6:.0f} MB")

bname = (f"deploy-{TS}-{o['tag']}-{sha7}" if o["stage"] else f"rollback-pre-{TS}")
B = os.path.join(LIVE, "app-backups", bname)
restart_plan = "skip (--no-restart)" if o["norestart"] else f"{HERE}/restart-connector.sh --run-dir {RUN} (no --force)"
if DRY:
    say(f"DRY RUN plan: backup -> {B} (app, mcp-app, routes{', mcp-app-next' if os.path.exists(stale_next) else ''}); "
        f"swap app/mcp-app from {os.path.dirname(SRC_APP)}; renderer: {('sync ' + str(len(vplan['changed'])) + ' + remove ' + str(len(vplan['removed'])) + ' in ' + VISION + '/vision (backup first, sha256 check)') if vplan else 'not touched'}; "
        f"restart: {restart_plan}; notes entry -> {B}/notes-entry.md")
    say("DRY RUN: nothing written, nothing restarted")
    sys.exit(0)

# ---------- backup ----------
os.makedirs(B)
for d in ("app", "mcp-app", "routes"):
    s = os.path.join(LIVE, d)
    if os.path.isdir(s): shutil.copytree(s, os.path.join(B, d), symlinks=True)
bm_new = {"app": manifest(os.path.join(B, "app")), "mcp-app": manifest(os.path.join(B, "mcp-app"))}
if bm_new["app"] != live_app_m or bm_new["mcp-app"] != live_mcp_m:
    fail(1, f"backup {B} does not match live; nothing swapped (inspect and delete {B})")
json.dump(bm_new, open(os.path.join(B, "backup-sha256.json"), "w"))
meta = {"created": NOW, "kind": "deploy" if o["stage"] else "rollback-pre", "tag": o["tag"], "commit": head, "subject": subj,
        "source": os.path.dirname(SRC_APP), "prev_text_dll": old_text, "prev_mcp_dll": old_mcp, "new_text_dll": new_text, "new_mcp_dll": new_mcp}
json.dump(meta, open(os.path.join(B, "deploy-meta.json"), "w"), indent=1)
if os.path.exists(stale_next): shutil.move(stale_next, os.path.join(B, "mcp-app-next-stale"))
say(f"backup OK: {B} (app {len(live_app_m)} + mcp-app {len(live_mcp_m)} files hash-identical to live)")

# ---------- renderer backup + sync (before the client swap: a failure here leaves the client untouched) ----------
vision_line = None
if vplan:
    paths = sorted(set(vplan["target"]) | set(vplan["removed"]))
    vbm = {}
    for rel in paths:
        lp = os.path.join(VISION, rel)
        if os.path.isfile(lp):
            bp = os.path.join(B, rel); os.makedirs(os.path.dirname(bp), exist_ok=True); shutil.copy2(lp, bp)
            vbm[rel] = sha(lp)
            if sha(bp) != vbm[rel]: fail(1, f"renderer backup of {rel} does not match live; nothing changed in the renderer or the client")
        else: vbm[rel] = None
    json.dump(vbm, open(os.path.join(B, "vision-backup-sha256.json"), "w"), indent=0, sort_keys=True)
    def vrestore():
        for rel, h in vbm.items():
            lp = os.path.join(VISION, rel)
            if h is None:
                if os.path.isfile(lp): os.remove(lp)
            else: os.makedirs(os.path.dirname(lp), exist_ok=True); shutil.copy2(os.path.join(B, rel), lp)
    try:
        for rel in vplan["changed"]:
            lp = os.path.join(VISION, rel); os.makedirs(os.path.dirname(lp), exist_ok=True)
            tmp = lp + f".deploy-tmp-{TS}"
            with open(tmp, "wb") as fh: fh.write(vplan["data"][rel])
            if os.path.isfile(lp): shutil.copymode(lp, tmp)
            os.replace(tmp, lp)
        for rel in vplan["removed"]: os.remove(os.path.join(VISION, rel))
        inst_v = {rel: (sha(os.path.join(VISION, rel)) if os.path.isfile(os.path.join(VISION, rel)) else None) for rel in paths}
        want_v = {rel: vplan["target"].get(rel) for rel in paths}
        bad = sorted(r for r in paths if inst_v[r] != want_v[r])
        if bad: raise RuntimeError(f"{len(bad)} file(s) do not match after the sync, e.g. {bad[:3]}")
    except Exception as e:
        vrestore(); fail(1, f"renderer sync failed ({e}); renderer restored from {B}/vision, client not swapped")
    json.dump(inst_v, open(os.path.join(B, "renderer-installed-sha256.json"), "w"), indent=0, sort_keys=True)
    nfiles = sum(1 for v in inst_v.values() if v)
    vision_line = (f"renderer OK: {VISION}/vision {len(vplan['changed'])} updated, {len(vplan['removed'])} removed; {nfiles} repo files sha256-verified "
                   f"({os.path.relpath(B, LIVE)}/renderer-installed-sha256.json; backup {os.path.relpath(B, LIVE)}/vision)")
    say(vision_line)

# ---------- swap ----------
TMP = os.path.join(LIVE, f".deploy-tmp-{TS}"); os.makedirs(TMP)
try:
    for d, src, want in (("app", SRC_APP, src_app_m), ("mcp-app", SRC_MCP, src_mcp_m)):
        shutil.copytree(src, os.path.join(TMP, d), symlinks=True)
        if manifest(os.path.join(TMP, d)) != want: raise RuntimeError(f"copy of {src} does not match it")
except Exception as e:
    shutil.rmtree(TMP, ignore_errors=True); fail(1, f"staging copy failed ({e}); live untouched")
moved = []
try:
    for d in ("app", "mcp-app"):
        os.rename(os.path.join(LIVE, d), os.path.join(TMP, d + ".old")); moved.append(d)
        os.rename(os.path.join(TMP, d), os.path.join(LIVE, d))
    inst = {"app": manifest(os.path.join(LIVE, "app")), "mcp-app": manifest(os.path.join(LIVE, "mcp-app"))}
    if inst["app"] != src_app_m or inst["mcp-app"] != src_mcp_m: raise RuntimeError("installed files do not match the source")
except Exception as e:
    for d in moved:
        cur = os.path.join(LIVE, d)
        if os.path.exists(cur): os.rename(cur, os.path.join(TMP, d + ".failed"))
        os.rename(os.path.join(TMP, d + ".old"), cur)
    fail(1, f"swap failed ({e}); previous live dirs put back; leftovers in {TMP}")
json.dump(inst, open(os.path.join(B, "installed-sha256.json"), "w"))
shutil.rmtree(TMP)
say(f"swap OK: app {len(inst['app'])} + mcp-app {len(inst['mcp-app'])} files hash-verified against the source ({B}/installed-sha256.json)")

# ---------- restart + /proc ----------
rc_final = 0; restart_line = "restart skipped (--no-restart); the running connector keeps the old build until restart-connector.sh"
if not o["norestart"]:
    rc, out = run([os.path.join(HERE, "restart-connector.sh"), "--run-dir", RUN]); say(out)
    if rc: rc_final = 4; restart_line = f"restart-connector.sh FAILED (rc {rc})"
    else:
        rows = []
        for f in sorted(os.listdir(RUN)):
            if not re.fullmatch(r"connector-proxy-\d+\.json", f): continue
            try: d = json.load(open(os.path.join(RUN, f)))
            except Exception: continue
            if not alive(d.get("proxy_pid", -1)): continue
            pid = d["child_pid"]
            try: exe = os.readlink(f"/proc/{pid}/exe").replace(" (deleted)", "")
            except OSError: exe = "?"
            dll = sha(os.path.join(os.path.dirname(exe), "galatay-mcp.dll")) if exe != "?" else None
            try: rt = sorted(set(re.findall(r"Microsoft\.NETCore\.App/([0-9][^/]*)/", open(f"/proc/{pid}/maps").read())))
            except OSError: rt = []
            ok = exe == os.path.join(LIVE, "mcp-app", "galatay-mcp") and dll == new_mcp
            if not ok: rc_final = 4
            rows.append(f"proxy {d['proxy_pid']} child {pid} {'OK' if ok else 'FAILED'} exe {exe} dll {short(dll)} runtime {','.join(rt) or '?'}")
        if not rows: rc_final = 4; rows.append("no live proxy state file found")
        for r in rows: say("/proc: " + r)
        restart_line = "restart-connector.sh (no --force); " + "; ".join(rows)
if not os.path.exists(marker) or os.stat(marker).st_mtime != marker_m:
    say("WARNING: deliberate-stop changed during the deploy"); rc_final = rc_final or 1
held2 = None
try:
    pid = int(open(os.path.join(RUN, "sl-session.pid")).read().strip())
    if alive(pid) and comm(pid).startswith("galatay"): held2 = pid
except Exception: pass
say(f"after: deliberate-stop unchanged; logged in: {'YES pid %d' % held2 if held2 else 'no'}")

# ---------- notes entry ----------
title = (f"## {NOW} — {o['tag']} deployed offline (merge {sha7})" if o["stage"] else f"## {NOW} — rolled back to {os.path.basename(B0)}")
lines = [title]
if o["stage"]:
    lines.append(f"- Staged from {ST} @ {head} ({subj}): app-staging {len(src_app_m)} files, galatay-text.dll {new_text}; mcp-app-staging {len(src_mcp_m)} files, galatay-mcp.dll {new_mcp}; LMV patch check OK.")
else:
    lines.append(f"- Source {B0}: app {len(src_app_m)} files text {new_text}; mcp-app {len(src_mcp_m)} files mcp {new_mcp}.")
lines.append(f"- Backup: {os.path.relpath(B, LIVE)} (app, mcp-app, routes; hash-identical to live). Previous live: text {short(old_text)}, mcp {short(old_mcp)}.")
lines.append(f"- Swap by deploy.sh: app {len(src_app_m)} + connector {len(src_mcp_m)} files hash-verified ({os.path.relpath(B, LIVE)}/installed-sha256.json).")
if vision_line: lines.append(f"- {vision_line}.")
elif VISION is None: lines.append("- Renderer step skipped.")
lines.append(f"- {restart_line}.")
lines.append(f"- deliberate-stop ({time.strftime('%m-%d %H:%M', time.localtime(marker_m))}) left in place; no login.")
if helper_diffs: lines.append("- Not copied, differ from the staged tree: " + ", ".join(helper_diffs))
lines.append(f"- Rollback: `textclient/deploy.sh --rollback {B}`" + (" (then restore routes/ from the backup if they matter)." if o["stage"] else "."))
entry = "\n".join(lines) + "\n"
open(os.path.join(B, "notes-entry.md"), "w").write(entry)
if o["notes"]:
    with open(o["notes"], "a") as fh: fh.write("\n" + entry)
print("\n----- notes entry -----\n" + entry + "-----------------------")
if rc_final == 4: print(f"connector restart/verify FAILED; live files are the new build. Roll back: {HERE}/deploy.sh --rollback {B}")
sys.exit(rc_final)
PY
