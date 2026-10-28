#!/usr/bin/env bash
# restart-connector.sh (2026-10-28): load a new connector build WITHOUT removing and re-adding the MCP server.
# Signals every galatay-mcp-proxy (one per agent exec daemon) to restart its child (the real mcp-app/galatay-mcp),
# waits, and verifies for each proxy: new child pid, child start time after the signal, child alive, and that the
# child's own galatay-mcp.dll (via /proc/<pid>/exe) matches the state file and, with the staged build, mcp-app's dll.
# REFUSES (exit 3) while Galatay is logged in (run/sl-session.pid names a live galatay process) unless --force:
# stopping the child logs her out. --force sends SIGUSR1 (force restart) instead of SIGHUP.
# Usage: restart-connector.sh [--force] [--timeout S] [--run-dir DIR]   (--run-dir default: dirname of $GT_SOCK)
# Exit: 0 all proxies restarted and verified, 1 verify failed / timeout, 2 usage or no proxy found, 3 refused (logged in).
# Before: build with build-all.sh --stage-mcp (mcp-app-next + READY); the child launcher swaps it in on the restart.
set -uo pipefail
exec python3 - "$@" <<'PY'
import glob, hashlib, json, os, signal, sys, time

args = sys.argv[1:]; force = False; timeout = 90.0
run_dir = os.path.dirname(os.environ.get("GT_SOCK", "/home/box/viewers/textclient/run/galatay.sock"))
i = 0
while i < len(args):
    a = args[i]
    if a == "--force": force = True
    elif a == "--timeout" and i + 1 < len(args): timeout = float(args[i + 1]); i += 1
    elif a == "--run-dir" and i + 1 < len(args): run_dir = args[i + 1]; i += 1
    else: print("usage: restart-connector.sh [--force] [--timeout S] [--run-dir DIR]"); sys.exit(2)
    i += 1

def alive(pid):
    try: os.kill(pid, 0); return True
    except OSError: return False
def comm(pid):
    try: return open(f"/proc/{pid}/comm").read().strip()
    except OSError: return ""
def start_time(pid):
    try:
        ticks = int(open(f"/proc/{pid}/stat").read().rsplit(")", 1)[1].split()[19])
        btime = next(int(l.split()[1]) for l in open("/proc/stat") if l.startswith("btime"))
        return btime + ticks / os.sysconf("SC_CLK_TCK")
    except Exception: return None
def exe_dll_sha(pid):
    try:
        exe = os.readlink(f"/proc/{pid}/exe")
        with open(os.path.join(os.path.dirname(exe.replace(" (deleted)", "")), "galatay-mcp.dll"), "rb") as f:
            return hashlib.sha256(f.read()).hexdigest()
    except Exception: return None
def load(path):
    try: return json.load(open(path))
    except Exception: return None

def proxies():
    out = []
    for p in sorted(glob.glob(os.path.join(run_dir, "connector-proxy-*.json"))):
        d = load(p)
        if d and alive(d["proxy_pid"]) and "proxy" in open(f"/proc/{d['proxy_pid']}/cmdline").read():
            out.append(d)
    return out

ps = proxies()
if not ps:
    print(f"no running galatay-mcp-proxy found in {run_dir} (is the connector still the old direct launch? then only remove + re-add reloads it)"); sys.exit(2)

# logged-in check: the SL session lock file names the process that holds the session
held = None
try:
    pid = int(open(os.path.join(run_dir, "sl-session.pid")).read().strip())
    if alive(pid) and comm(pid).startswith("galatay"): held = pid
except Exception: pass
if held and not force:
    print(f"REFUSED: Galatay is logged in (session held by pid {held}); restarting the connector would log her out. Log out first, or use --force.")
    sys.exit(3)
if held: print(f"--force: Galatay is logged in (pid {held}); the restart will log her out")

before = {d["proxy_pid"]: d for d in ps}
t0 = time.time()
for d in ps:
    os.kill(d["proxy_pid"], signal.SIGUSR1 if force else signal.SIGHUP)
    print(f"signalled proxy {d['proxy_pid']} ({'SIGUSR1 force' if force else 'SIGHUP'}); old child pid {d['child_pid']} gen {d['child_gen']}")

done = {}
while time.time() - t0 < timeout and len(done) < len(before):
    time.sleep(0.5)
    for pp, old in before.items():
        if pp in done: continue
        new = load(os.path.join(run_dir, f"connector-proxy-{pp}.json"))
        if not new: continue
        if new.get("last_result", "").startswith("refused") and (new.get("last_refusal") or 0) >= t0 - 1:
            print(f"proxy {pp} REFUSED the restart: {new['last_result']}"); done[pp] = ("refused", new); continue
        if new["child_gen"] > old["child_gen"] and new["child_pid"] != old["child_pid"] and new.get("last_result", "").startswith("ok"):
            done[pp] = ("ok", new)

rc = 0
cur_dll = None
for pp, old in before.items():
    if pp not in done: print(f"proxy {pp}: TIMEOUT after {timeout:g}s (state: {(load(os.path.join(run_dir, f'connector-proxy-{pp}.json')) or {}).get('last_result')})"); rc = 1; continue
    kind, new = done[pp]
    if kind != "ok": rc = 3 if kind == "refused" else 1; continue
    pid = new["child_pid"]; st = start_time(pid)
    for _ in range(50):   # the proxy refreshes the state file once the child has exec'd the real connector
        new = load(os.path.join(run_dir, f"connector-proxy-{pp}.json")) or new
        sha = exe_dll_sha(pid)
        if sha == new.get("child_dll_sha256") and new.get("child_pid") == pid: break
        time.sleep(0.2)
    probs = []
    if not alive(pid): probs.append(f"child pid {pid} not alive")
    if pid == old["child_pid"]: probs.append("child pid unchanged")
    if st is None or st < t0 - 1: probs.append(f"child start {st} is not after the signal {t0:.0f}")
    if sha != new.get("child_dll_sha256"): probs.append(f"dll hash from /proc ({sha}) differs from the state file ({new.get('child_dll_sha256')})")
    if alive(old["child_pid"]) and old["child_pid"] != pid: probs.append(f"old child pid {old['child_pid']} still alive")
    status = "OK" if not probs else "FAILED"
    if probs: rc = 1
    print(f"proxy {pp}: {status}: child pid {old['child_pid']} -> {pid}, started {time.strftime('%F %T', time.localtime(st)) if st else '?'}, "
          f"dll sha256 {sha[:16] if sha else 'n/a'} (was {old.get('child_dll_sha256') and old['child_dll_sha256'][:16]})" + ("; " + "; ".join(probs) if probs else ""))
if rc == 0: print("all connector children restarted and verified")
sys.exit(rc)
PY
