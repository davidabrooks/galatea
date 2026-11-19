#!/usr/bin/env bash
# dep-scan.sh (2026-11-18): read-only dependency scan of a Galatea tree, compared with the last weekly baseline.
#
#   dep-scan.sh [--tree DIR] [--baseline FILE|none] [--out FILE] [--live DIR] [--offline]
#
# Collects, as JSON (default /tmp/dep-scan-<time>.json, path printed):
#   nuget        per project (GalatayText, GalatayMcp, GalatayText.Tests, SceneMesher, LMV source generators):
#                `dotnet list package --include-transitive` resolved versions, --vulnerable, --outdated (latest) and
#                --outdated --highest-minor; merged into vulnerabilities / outdated / outdated_highest_patch_or_minor
#                in the baseline's {package: [current, latest]} shape
#   drift        deployed <live>/app/galatay-text.deps.json + mcp-app/galatay-mcp.deps.json packages vs this tree's restore
#   lmv          pin in scripts/ci-setup-libremetaverse.sh vs upstream latest release + commits on master past the pin
#   dotnet       installed runtimes/SDKs vs the 10.0 channel's latest (releases.json), security flag
#   python       == pins in scripts/requirements.txt + requirements-ci.txt checked against OSV (api.osv.dev)
# then prints what changed against the baseline (newest sl-dependency-security-and-update-check/baseline-*.json).
# Read-only: never edits the tree's tracked files, the baseline, or anything live. `dotnet restore` writes obj/ in --tree
# (use a scratch worktree from galatea-wt-new.sh; it needs textclient/src-libremetaverse). --offline skips network checks
# other than NuGet restore. Exit: 0 scan done (findings are in the output, not the exit code), 1 scan errors, 2 usage.
set -uo pipefail
export PATH="/home/box/.dotnet:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-/home/box/.dotnet}" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 GH_PROMPT_DISABLED=1
exec python3 - "$@" <<'PY'
import glob, json, os, re, subprocess, sys, time, urllib.request
a = sys.argv[1:]; opt = {"tree": ".", "baseline": None, "out": None, "live": "/home/box/viewers/textclient", "offline": False}
i = 0
while i < len(a):
    k = a[i]
    if k in ("--tree", "--baseline", "--out", "--live") and i + 1 < len(a): opt[k[2:]] = a[i + 1]; i += 1
    elif k == "--offline": opt["offline"] = True
    else: print("usage: dep-scan.sh [--tree DIR] [--baseline FILE|none] [--out FILE] [--live DIR] [--offline]"); sys.exit(2)
    i += 1
def sh(cmd, cwd=None, timeout=600):
    try:
        r = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, timeout=timeout); return r.returncode, r.stdout, r.stderr
    except Exception as e: return 99, "", str(e)
rc, top, _ = sh(["git", "-C", opt["tree"], "rev-parse", "--show-toplevel"])
if rc: print("not a git tree:", opt["tree"]); sys.exit(2)
T = top.strip(); LMV = f"{T}/textclient/src-libremetaverse"
if not os.path.isfile(f"{LMV}/LibreMetaverse/LibreMetaverse.csproj"): print(f"{LMV} missing: make the tree with galatea-wt-new.sh"); sys.exit(2)
TS = time.strftime("%Y%m%d-%H%M%S"); out_path = opt["out"] or f"/tmp/dep-scan-{TS}.json"
errors = []; R = {"checked_at": time.strftime("%Y-%m-%d %H:%M %Z"), "tree": T, "source_commit": sh(["git", "-C", T, "rev-parse", "HEAD"])[1].strip()}
say = lambda m: print(m, flush=True)

# ---------- NuGet ----------
projects = {"GalatayText": f"{T}/textclient/galatay-text/GalatayText.csproj", "GalatayMcp": f"{T}/textclient/galatay-mcp/GalatayMcp.csproj",
            "GalatayText.Tests": f"{T}/textclient/GalatayText.Tests/GalatayText.Tests.csproj", "SceneMesher": f"{T}/vision/scene-mesher/SceneMesher.csproj"}
for p in sorted(glob.glob(f"{LMV}/SourceGenerators/*/*.csproj")): projects["LMV:" + os.path.basename(p)[:-7]] = p
def listing(proj, *extra):
    rc, so, se = sh(["dotnet", "list", proj, "package", "--include-transitive", "--format", "json", *extra])
    try: return json.loads(so)
    except Exception: errors.append(f"dotnet list {os.path.basename(proj)} {' '.join(extra)}: rc {rc} {(se or so)[-300:]}"); return {}
def pkgs(js):
    for pr in js.get("projects", []):
        for fw in pr.get("frameworks", []):
            for kind in ("topLevelPackages", "transitivePackages"):
                for p in fw.get(kind, []): yield kind, p
resolved, vulns, outdated, minor, per = {}, {}, {}, {}, {}
for name, proj in projects.items():
    say(f"nuget: {name}")
    extra = [f"-p:LMV={LMV}"] if name == "SceneMesher" else []
    rc, so, se = sh(["dotnet", "restore", proj, "--nologo", "-v", "q", *extra])
    if rc: errors.append(f"restore {name}: {(se or so)[-300:]}"); continue
    res = {p["id"]: p.get("resolvedVersion") for _, p in pkgs(listing(proj))}
    per[name] = res
    for k, v in res.items():
        if v: resolved.setdefault(k, v)   # runtime projects come first, so they win
    for _, p in pkgs(listing(proj, "--vulnerable")):
        for v in p.get("vulnerabilities", []): vulns.setdefault(f"{p['id']} {p.get('resolvedVersion')}", set()).add(f"{v.get('severity')} {v.get('advisoryurl')}")
    gen = name.startswith("LMV:")       # generators are build-time only: top-level packages, like the baseline
    for _, p in pkgs(listing(proj, "--outdated")):
        if not (gen and _ != "topLevelPackages"): outdated.setdefault(p["id"], [p.get("resolvedVersion"), p.get("latestVersion")])
    for _, p in pkgs(listing(proj, "--outdated", "--highest-minor")):
        if not (gen and _ != "topLevelPackages"): minor.setdefault(p["id"], [p.get("resolvedVersion"), p.get("latestVersion")])
R.update(projects_scanned={k: len(v) for k, v in per.items()}, resolved_packages=len(resolved),
         vulnerabilities=[{"package": k, "advisories": sorted(v)} for k, v in sorted(vulns.items())],
         outdated=dict(sorted(outdated.items())), outdated_highest_patch_or_minor=dict(sorted(minor.items())))

# ---------- deployed vs source ----------
drift = {}; deployed_ids = set()
for d, deps in (("app", "galatay-text.deps.json"), ("mcp-app", "galatay-mcp.deps.json")):
    f = os.path.join(opt["live"], d, deps)
    try: libs = json.load(open(f)).get("libraries", {})
    except Exception as e: errors.append(f"read {f}: {e}"); continue
    dep = dict(k.split("/", 1) for k, v in libs.items() if v.get("type") == "package")
    src = per.get("GalatayText" if d == "app" else "GalatayMcp", {})
    diffs = {k: [v, src.get(k)] for k, v in dep.items() if src.get(k) != v}
    deployed_ids.update(dep)
    drift[d] = {"deployed_packages": len(dep), "differs_from_source": diffs}
R["deployed_vs_source_drift"] = drift
R["outdated_runtime_ids"] = sorted(k for k in outdated if k in deployed_ids)

# ---------- LibreMetaverse pin ----------
setup = open(f"{T}/scripts/ci-setup-libremetaverse.sh").read()
pin = (re.search(r'PIN="\$\{LIBREMETAVERSE_SHA:-([0-9a-f]+)\}"', setup) or [None, None])[1]
lmv = {"pinned_sha": pin, "patches": len(re.findall(r'^apply "\$TC/', setup, re.M))}
if not opt["offline"]:
    rc, so, se = sh(["gh", "api", "repos/cinderblocks/libremetaverse/releases/latest", "-q", "{tag: .tag_name, published: .published_at}"])
    if rc == 0: lmv["latest_release"] = json.loads(so)
    else: errors.append(f"gh releases/latest: {se[-200:]}")
    rc, so, se = sh(["git", "ls-remote", "--tags", "https://github.com/cinderblocks/libremetaverse.git"])
    tags = {}
    for line in so.splitlines():
        s, ref = line.split("\t"); t = ref.split("/")[-1]
        if t.endswith("^{}"): tags[t[:-3]] = s
        else: tags.setdefault(t, s)
    lmv["pinned_tag"] = next((t for t, s in tags.items() if s == pin), None)
    lt = (lmv.get("latest_release") or {}).get("tag"); lmv["latest_release_sha"] = tags.get(lt)
    rc, so, se = sh(["gh", "api", f"repos/cinderblocks/libremetaverse/compare/{pin}...master",
                     "-q", "{ahead: .ahead_by, commits: [.commits[] | (.sha[0:8] + \" \" + (.commit.message | split(\"\\n\")[0]))]}"])
    if rc == 0: lmv["master_past_pin"] = json.loads(so)
    else: errors.append(f"gh compare: {se[-200:]}")
R["source_pins"] = {"LibreMetaverse": lmv}

# ---------- .NET ----------
rt = sh(["dotnet", "--list-runtimes"])[1].split("\n"); sdks = sh(["dotnet", "--list-sdks"])[1].split("\n")
dn = {"runtimes": sorted({l.split()[1] for l in rt if l.startswith("Microsoft.NETCore.App")}), "sdks": [l.split()[0] for l in sdks if l.strip()]}
if not opt["offline"]:
    try:
        rel = json.load(urllib.request.urlopen("https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json", timeout=30))
        latest = rel["releases"][0]
        dn.update(channel_latest_runtime=rel.get("latest-runtime"), channel_latest_sdk=rel.get("latest-sdk"),
                  latest_release_date=latest.get("release-date"), latest_is_security=latest.get("security"),
                  latest_cves=[c.get("cve-id") for c in latest.get("cve-list", []) or []])
        dn["runtime_current"] = rel.get("latest-runtime") in dn["runtimes"]; dn["sdk_current"] = rel.get("latest-sdk") in dn["sdks"]
    except Exception as e: errors.append(f"dotnet releases.json: {e}")
R["dotnet"] = dn

# ---------- Python pins (OSV) ----------
pins = {}
for f in ("scripts/requirements.txt", "requirements-ci.txt"):
    try:
        for line in open(f"{T}/{f}"):
            m = re.match(r"\s*([A-Za-z0-9_.\-\[\]]+)\s*==\s*([^\s;#]+)", line)
            if m: pins[re.sub(r"\[.*\]", "", m[1]).lower()] = m[2]
    except OSError: pass
py = {"pins": pins, "advisories": {}}
if pins and not opt["offline"]:
    try:
        q = {"queries": [{"package": {"name": n, "ecosystem": "PyPI"}, "version": v} for n, v in pins.items()]}
        req = urllib.request.Request("https://api.osv.dev/v1/querybatch", data=json.dumps(q).encode(), headers={"Content-Type": "application/json"})
        res = json.load(urllib.request.urlopen(req, timeout=60))["results"]
        for (n, v), r in zip(pins.items(), res):
            ids = [x["id"] for x in r.get("vulns", [])]
            if ids: py["advisories"][f"{n}=={v}"] = ids
    except Exception as e: errors.append(f"OSV: {e}")
R["python"] = py; R["errors"] = errors
json.dump(R, open(out_path, "w"), indent=1)

# ---------- report + baseline diff ----------
say(f"\nscan of {T} @ {R['source_commit'][:7]} -> {out_path}")
say(f"NuGet: {len(resolved)} resolved packages across {len(per)} projects; vulnerable: {len(R['vulnerabilities'])}; outdated (latest): {len(outdated)} ({len(R['outdated_runtime_ids'])} deployed at runtime); outdated within minor: {len(minor)}")
for v in R["vulnerabilities"]: say(f"  VULN {v['package']}: {'; '.join(v['advisories'])}")
for d, x in drift.items(): say(f"deployed {d}: {x['deployed_packages']} packages; differ from source: {x['differs_from_source'] or 'none'}")
mp = lmv.get("master_past_pin") or {}
say(f"LibreMetaverse pin {str(pin)[:8]} ({lmv.get('pinned_tag')}, {lmv['patches']} patches); latest release {(lmv.get('latest_release') or {}).get('tag')}; master {mp.get('ahead', '?')} commits past the pin")
say(f".NET runtimes {dn['runtimes']} SDKs {dn['sdks']}; channel latest {dn.get('channel_latest_runtime')}/{dn.get('channel_latest_sdk')} ({dn.get('latest_release_date')}, security {dn.get('latest_is_security')})")
say(f"Python pins with OSV advisories: {py['advisories'] or 'none'}")
bl = opt["baseline"]
if bl != "none":
    if not bl:
        c = sorted(f for f in glob.glob("/workspace/secondlife/sl-dependency-security-and-update-check/baseline-*.json") if re.search(r"baseline-\d{8}(-pending)?\.json$", f))
        bl = c[-1] if c else None
    if bl and os.path.isfile(bl):
        B = json.load(open(bl)); say(f"\n== compared with baseline {bl} ({B.get('checked_at')}) ==")
        bo, no = B.get("outdated", {}) or {}, outdated
        rt = lambda k: "runtime" if k in deployed_ids else "build/test-only"
        for k in sorted(set(no) - set(bo)): say(f"  newly outdated ({rt(k)}): {k} {no[k][0]} -> {no[k][1]}")
        for k in sorted(set(bo) - set(no)): say(f"  no longer outdated: {k} (baseline {bo[k][0]} -> {bo[k][1]}; now {resolved.get(k)})")
        moved = [k for k in sorted(set(bo) & set(no)) if list(bo[k]) != list(no[k])]
        groups = {}
        for k in moved: groups.setdefault((tuple(bo[k]), tuple(no[k])), []).append(k)
        for (b, n), ks in groups.items():
            say(f"  changed ({len(ks)}): baseline {b[0]}->{b[1]}, now {n[0]}->{n[1]}: " + (", ".join(ks) if len(ks) <= 4 else ", ".join(ks[:3]) + f" … (+{len(ks)-3})"))
        bv = {json.dumps(x, sort_keys=True) for x in B.get("vulnerabilities", []) or []}
        say(f"  vulnerabilities: baseline {len(bv)}, now {len(R['vulnerabilities'])}")
        bp = (B.get("source_pins") or {}).get("LibreMetaverse", {})
        if bp.get("pinned_sha") != pin: say(f"  LMV pin changed: {str(bp.get('pinned_sha'))[:8]} -> {str(pin)[:8]}")
        else: say(f"  LMV pin unchanged ({str(pin)[:8]})")
    else: say("\n(no baseline found to compare)")
if errors:
    say(f"\n{len(errors)} scan error(s):"); [say("  " + e) for e in errors]; sys.exit(1)
PY
