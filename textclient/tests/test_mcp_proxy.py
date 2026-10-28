"""galatay-mcp-proxy.py / galatay-mcp-child.sh / restart-connector.sh (2026-10-28): a connector rebuild is loaded by
restarting the proxy's child, without remove + re-add of the MCP server.

Everything runs against a private run dir, TMP and log paths; nothing live is touched and nobody logs in. Two layers:
- a stub MCP child (python) for the protocol edge cases (in-flight error, unexpected death, refusal while logged in);
- the real built connector (textclient/mcp-app-staging, skipped when not built) driven over stdio: initialize,
  tools/list, a harmless tool call, SIGHUP, list_changed, new child pid, same again, stdout carries only JSON-RPC.
"""
import json, os, pathlib, shutil, signal, subprocess, sys, threading, time, queue
import pytest

TC = pathlib.Path(__file__).resolve().parents[1]
PROXY = TC / "galatay-mcp-proxy.py"
CHILD_SH = TC / "galatay-mcp-child.sh"
RESTART = TC / "restart-connector.sh"
STAGING = TC / "mcp-app-staging"

STUB = r'''
import json, os, sys, time
sys.stderr.write("stub child started pid %d\n" % os.getpid()); sys.stderr.flush()
for line in sys.stdin:
    m = json.loads(line); meth = m.get("method"); i = m.get("id")
    def reply(r): sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": i, "result": r}) + "\n"); sys.stdout.flush()
    if meth == "initialize":
        sys.stderr.write("stub got initialize\n"); sys.stderr.flush()
        reply({"protocolVersion": "2025-06-18", "capabilities": {"tools": {}}, "serverInfo": {"name": "stub", "version": "1", "pid": os.getpid()}})
    elif meth == "notifications/initialized": sys.stderr.write("stub got initialized\n"); sys.stderr.flush()
    elif meth == "tools/list": reply({"tools": [{"name": "t"}]})
    elif meth == "tools/call": reply({"pid": os.getpid()})
    elif meth == "slow": pass            # never answers: in flight
    elif meth == "garbage": sys.stdout.write("not json at all\n"); sys.stdout.flush(); reply({"ok": True})
    elif meth == "die": os._exit(7)
'''


class Client:
    def __init__(self, tmp, child_cmd, extra_env=None, run_dir=None):
        self.tmp = pathlib.Path(tmp); self.run = pathlib.Path(run_dir or self.tmp / "run"); self.run.mkdir(exist_ok=True, parents=True)
        env = {k: v for k, v in os.environ.items() if k not in ("GT_AUTOLOGIN",)}
        t = str(self.tmp)
        env.update(GT_SOCK=str(self.run / "galatay.sock"), GT_RUN_DIR=str(self.run), GT_LOG=t + "/t.log", GT_OFFER_STORE=t + "/offers.json",
                   GT_WEBHOOK_URL_FILE=t + "/nourl", GT_WEBHOOK_FAILED_LOG=t + "/wf.log", GT_MUTE_FILE=t + "/mutes.json",
                   GT_RESTART_STATE=t + "/rs.json", GT_OBJECT_CACHE=t + "/objc", GT_TCD=t + "/tcd", GT_SWAP_LOG=t + "/swap.log",
                   HOME=t, TMPDIR=t, TMP=t, GT_PROXY_CHILD_CMD=child_cmd, GT_PROXY_STOP_S="10", DOTNET_ROOT="/home/box/.dotnet")
        env.update(extra_env or {})
        self.errf = open(self.tmp / "proxy.err", "w")
        self.p = subprocess.Popen([sys.executable, str(PROXY)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.errf, env=env)
        self.lines = queue.Queue(); self.garbage = []; self.notes = []; self.ids = []
        self.resp = {}; self.cv = threading.Condition(); self.nid = 0
        threading.Thread(target=self._read, daemon=True).start()

    def _read(self):
        for raw in iter(self.p.stdout.readline, b""):
            try: m = json.loads(raw)
            except Exception: self.garbage.append(raw); continue
            if not isinstance(m, dict) or m.get("jsonrpc") != "2.0": self.garbage.append(raw); continue
            with self.cv:
                if "id" in m and "method" not in m: self.resp[m["id"]] = m
                else: self.notes.append(m)
                self.cv.notify_all()

    def send(self, o): self.p.stdin.write((json.dumps(o) + "\n").encode()); self.p.stdin.flush()

    def start_req(self, method, params=None):
        self.nid += 1; msg = {"jsonrpc": "2.0", "id": self.nid, "method": method}
        if params is not None: msg["params"] = params
        self.send(msg); return self.nid

    def wait_resp(self, rid, timeout=60):
        end = time.time() + timeout
        with self.cv:
            while rid not in self.resp:
                if time.time() > end: raise AssertionError(f"no response to id {rid}; proxy stderr:\n{(self.tmp / 'proxy.err').read_text()[-1500:]}")
                self.cv.wait(0.2)
            return self.resp[rid]

    def call(self, method, params=None, timeout=60): return self.wait_resp(self.start_req(method, params), timeout)

    def handshake(self):
        r = self.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "pytest", "version": "0"}})
        self.send({"jsonrpc": "2.0", "method": "notifications/initialized"}); return r

    def wait_note(self, method, timeout=60, after=0):
        end = time.time() + timeout
        with self.cv:
            while True:
                hits = [n for n in self.notes[after:] if n.get("method") == method]
                if hits: return hits[0]
                if time.time() > end: raise AssertionError(f"no {method}; notes {self.notes}; proxy stderr:\n{(self.tmp / 'proxy.err').read_text()[-1500:]}")
                self.cv.wait(0.2)

    def state(self):
        return json.loads((self.run / f"connector-proxy-{self.p.pid}.json").read_text())

    def wait_state(self, pred, timeout=60):
        end = time.time() + timeout
        while time.time() < end:
            try:
                s = self.state()
                if pred(s): return s
            except Exception: pass
            time.sleep(0.2)
        raise AssertionError(f"state never matched: {self.state()}\n{(self.tmp / 'proxy.err').read_text()[-1500:]}")

    def close(self):
        try: self.p.stdin.close()
        except Exception: pass
        try: self.p.wait(40)
        except subprocess.TimeoutExpired: self.p.kill(); self.p.wait()
        return self.p.returncode

    def err(self): return (self.tmp / "proxy.err").read_text()


@pytest.fixture
def stub(tmp_path):
    f = tmp_path / "stub.py"; f.write_text(STUB)
    return f"{sys.executable} {f}"


def alive(pid):
    try: os.kill(pid, 0); return True
    except OSError: return False


def test_stub_restart_replays_handshake_and_fails_inflight(tmp_path, stub):
    c = Client(tmp_path, stub)
    try:
        init = c.handshake()
        assert init["result"]["capabilities"]["tools"]["listChanged"] is True   # proxy advertises list_changed
        pid1 = c.call("tools/call", {})["result"]["pid"]
        slow = c.start_req("slow")
        time.sleep(0.3)
        n0 = len(c.notes)
        c.p.send_signal(signal.SIGHUP)
        err = c.wait_resp(slow, 20)
        assert err["error"]["code"] == -32001 and "restart" in err["error"]["message"]
        c.wait_note("notifications/tools/list_changed", after=n0)
        r = c.call("tools/call", {}); pid2 = r["result"]["pid"]
        assert pid2 != pid1 and not alive(pid1)
        log = c.err()
        lines = log.splitlines()
        assert lines.count("stub got initialize") == 2 and lines.count("stub got initialized") == 2   # replayed to the new child
        assert not any("proxy-init" in json.dumps(v) for v in c.resp.values())                  # replay response swallowed
        assert c.garbage == []
        assert c.state()["restarts"] == 1 and c.state()["child_pid"] == pid2
    finally:
        c.close()


def test_stub_stdout_garbage_goes_to_stderr_and_stderr_passes_through(tmp_path, stub):
    c = Client(tmp_path, stub)
    try:
        c.handshake()
        assert c.call("garbage")["result"] == {"ok": True}
        assert c.garbage == []
        assert "[mcp-child-stdout] not json at all" in c.err() and "stub child started" in c.err()
    finally:
        c.close()


def test_stub_unexpected_death_respawns_once_then_gives_up(tmp_path, stub):
    c = Client(tmp_path, stub, extra_env={"GT_PROXY_RESPAWN_WINDOW_S": "30"})
    try:
        c.handshake()
        pid1 = c.state()["child_pid"]
        n0 = len(c.notes)
        r = c.call("die")
        assert r["error"]["code"] == -32001
        c.wait_note("notifications/tools/list_changed", after=n0)
        pid2 = c.call("tools/call", {})["result"]["pid"]
        assert pid2 != pid1
        assert "exited unexpectedly" in c.err()
        r = c.call("die")                                   # second death inside the window: proxy ends, non-zero
        assert r["error"]["code"] == -32001
        assert c.p.wait(20) == 1
        assert "giving up" in c.err() and c.garbage == []
    finally:
        c.close()


def test_stub_soft_restart_refused_while_logged_in_force_and_trigger_file(tmp_path, stub):
    c = Client(tmp_path, stub)
    try:
        c.handshake()
        pid1 = c.state()["child_pid"]
        (c.run / "sl-session.pid").write_text(str(pid1))     # the child holds the SL session
        c.p.send_signal(signal.SIGHUP)
        s = c.wait_state(lambda s: s["last_result"].startswith("refused"))
        assert s["child_pid"] == pid1 and alive(pid1)
        ref1 = s["last_refusal"]
        trig = c.run / "restart-connector"; trig.write_text("")  # touch = soft, also refused
        c.wait_state(lambda s: s["last_refusal"] > ref1)
        assert c.state()["child_pid"] == pid1
        c.p.send_signal(signal.SIGUSR1)                       # force
        s = c.wait_state(lambda s: s["child_pid"] != pid1 and s["last_result"].startswith("ok"))
        (c.run / "sl-session.pid").unlink()
        time.sleep(1.1); trig.write_text("")                  # logged out: touched file restarts (soft)
        s2 = c.wait_state(lambda s: s["restarts"] == 2)
        assert s2["child_pid"] != s["child_pid"]
        time.sleep(1.1); (c.run / "sl-session.pid").write_text(str(s2["child_pid"])); trig.write_text("force")
        s3 = c.wait_state(lambda s: s["restarts"] == 3)
        assert s3["child_pid"] != s2["child_pid"]
    finally:
        c.close()


def test_stub_client_eof_stops_child_and_removes_state(tmp_path, stub):
    c = Client(tmp_path, stub)
    c.handshake(); pid = c.state()["child_pid"]; statef = c.run / f"connector-proxy-{c.p.pid}.json"
    assert c.close() == 0
    assert not alive(pid) and not statef.exists()


def fake_app(tcd, name, marker):
    d = tcd / name; d.mkdir(parents=True)
    exe = d / "galatay-mcp"; exe.write_text(f"#!/usr/bin/env bash\necho {marker} >&2\nexec sleep 30\n"); exe.chmod(0o755)
    return d


def run_child_sh(tmp, tcd, pname, extra=None):
    env = {**os.environ, "GT_TCD": str(tcd), "GT_SOCK": str(tmp / "run" / "galatay.sock"), "GT_CHILD_PROC_NAME": pname,
           "GT_SWAP_LOG": str(tmp / "swap.log"), **(extra or {})}
    return subprocess.Popen(["bash", str(CHILD_SH)], env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)


def test_child_sh_swaps_staged_build_when_no_child_runs(tmp_path):
    tcd = tmp_path / "tcd"; fake_app(tcd, "mcp-app", "OLD"); nxt = fake_app(tcd, "mcp-app-next", "NEW"); (nxt / "READY").write_text("x")
    p = run_child_sh(tmp_path, tcd, "no-such-process-name")
    time.sleep(1.0); p.kill(); out, err = p.communicate()
    assert "NEW" in err and out == ""                     # new build started, stdout stays clean
    assert not (tcd / "mcp-app-next").exists() and list((tcd / "mcp-app-backups").glob("mcp-app-*"))
    assert "staged connector build installed" in (tmp_path / "swap.log").read_text()


def test_child_sh_waits_for_other_child_then_swaps_or_gives_up(tmp_path):
    other = tmp_path / "fakechild"; shutil.copy("/bin/sleep", other)
    busy = subprocess.Popen([str(other), "60"])
    try:
        tcd = tmp_path / "tcd"; fake_app(tcd, "mcp-app", "OLD"); nxt = fake_app(tcd, "mcp-app-next", "NEW"); (nxt / "READY").write_text("x")
        t0 = time.time()
        p = run_child_sh(tmp_path, tcd, "fakechild", {"GT_SWAP_WAIT_S": "2"})
        time.sleep(3.5); p.kill(); out, err = p.communicate()
        assert "OLD" in err and (tcd / "mcp-app-next").exists()   # gave up: old build, staged build kept
        assert "NOT installed" in (tmp_path / "swap.log").read_text()
        # now the other child exits while the launcher is waiting: swap happens
        p = run_child_sh(tmp_path, tcd, "fakechild", {"GT_SWAP_WAIT_S": "20"})
        time.sleep(1.0); busy.kill(); busy.wait(); time.sleep(2.0); p.kill(); out, err = p.communicate()
        assert "NEW" in err and not (tcd / "mcp-app-next").exists()
    finally:
        busy.kill()


@pytest.mark.skipif(not (STAGING / "galatay-mcp").exists(), reason="textclient/mcp-app-staging not built (build-all.sh)")
def test_real_connector_restart_via_sighup_and_restart_script(tmp_path):
    tcd = tmp_path / "tcd"; shutil.copytree(STAGING, tcd / "mcp-app", symlinks=True)
    # GT_CHILD_PROC_NAME: the swap looks for other running connectors by process name; the box's live connectors must not count here
    c = Client(tmp_path, str(CHILD_SH), extra_env={"GT_CHILD_PROC_NAME": "galatay-mcp-pytest-none"})
    try:
        c.handshake()
        tools = c.call("tools/list")["result"]["tools"]
        assert len(tools) >= 50 and "idle_pose" in [t["name"] for t in tools]
        assert "not logged in" in json.dumps(c.call("tools/call", {"name": "idle_pose", "arguments": {"action": "status"}}))
        s1 = c.state(); pid1 = s1["child_pid"]
        assert s1["child_dll_sha256"] and os.readlink(f"/proc/{pid1}/exe").startswith(str(tcd / "mcp-app"))

        # SIGHUP: new child, list_changed, same tool list, same call works
        n0 = len(c.notes)
        c.p.send_signal(signal.SIGHUP)
        c.wait_note("notifications/tools/list_changed", after=n0)
        s2 = c.state(); pid2 = s2["child_pid"]
        assert pid2 != pid1 and not alive(pid1) and s2["child_gen"] == s1["child_gen"] + 1
        assert len(c.call("tools/list")["result"]["tools"]) == len(tools)
        assert "not logged in" in json.dumps(c.call("tools/call", {"name": "idle_pose", "arguments": {"action": "status"}}))

        # restart-connector.sh: refuses while the session lock names a live galatay process, --force restarts
        env = {**os.environ}
        script = lambda *a: subprocess.run(["bash", str(RESTART), "--run-dir", str(c.run), "--timeout", "60", *a], capture_output=True, text=True, env=env, timeout=120)
        (c.run / "sl-session.pid").write_text(str(pid2))
        r = script()
        assert r.returncode == 3 and "REFUSED" in r.stdout and c.state()["child_pid"] == pid2 and alive(pid2)
        (c.run / "sl-session.pid").unlink()
        # a staged build is swapped in by the restart
        nxt = tcd / "mcp-app-next"; shutil.copytree(tcd / "mcp-app", nxt, symlinks=True); (nxt / "READY").write_text("x")
        n1 = len(c.notes)
        r = script()
        assert r.returncode == 0, r.stdout + r.stderr
        assert "OK: child pid" in r.stdout and "all connector children restarted and verified" in r.stdout
        s3 = c.state(); assert s3["child_pid"] not in (pid1, pid2) and not alive(pid2)
        assert not nxt.exists() and list((tcd / "mcp-app-backups").glob("mcp-app-*"))
        assert os.readlink(f"/proc/{s3['child_pid']}/exe").startswith(str(tcd / "mcp-app"))
        c.wait_note("notifications/tools/list_changed", after=n1)
        assert len(c.call("tools/list")["result"]["tools"]) == len(tools)
        # --force while "logged in"
        (c.run / "sl-session.pid").write_text(str(s3["child_pid"]))
        r = script("--force"); assert r.returncode == 0, r.stdout + r.stderr
        assert c.state()["child_pid"] != s3["child_pid"]
        # protocol hygiene over the whole run: stdout carried only JSON-RPC, no replay leakage
        assert c.garbage == [] and not any(str(k).startswith("proxy-init") for k in c.resp)
    finally:
        rc = c.close()
    assert rc == 0


def test_restart_script_without_proxy_exits_2(tmp_path):
    r = subprocess.run(["bash", str(RESTART), "--run-dir", str(tmp_path)], capture_output=True, text=True, timeout=30)
    assert r.returncode == 2 and "no running galatay-mcp-proxy" in r.stdout


LAUNCHER = TC / "galatay-mcp.sh"


def test_launcher_keeps_all_hooks_and_execs_proxy():
    txt = LAUNCHER.read_text()
    for hook in ("galatay-autostart.sh", "backup-loop.sh", "icloudwatch.sh", "galatea-tmp-clean-loop.sh"):
        assert hook in txt
    assert 'exec python3 "$HERE/galatay-mcp-proxy.py"' in txt and "mcp-app-next" not in txt   # swap moved to the child launcher
    assert "mcp-app-next/READY" in CHILD_SH.read_text() and 'exec "$TCD/mcp-app/galatay-mcp"' in CHILD_SH.read_text()


def test_launcher_runs_proxy_with_hooks_neutralised(tmp_path, stub):
    d = tmp_path / "bin"; d.mkdir()
    (d / "galatay-mcp.sh").write_text(LAUNCHER.read_text().replace("setsid -f", ": setsid-f-disabled"))
    shutil.copy(PROXY, d / "galatay-mcp-proxy.py")
    for f in (d / "galatay-mcp.sh",): f.chmod(0o755)
    stubf = tmp_path / "stub.py"; stubf.write_text(STUB)
    c = Client(tmp_path, stub)
    c.p.kill(); c.p.wait()                                  # reuse Client's env only; start the launcher instead
    env = {**os.environ, "GT_SOCK": str(c.run / "galatay.sock"), "GT_PROXY_CHILD_CMD": stub}
    p = subprocess.Popen(["bash", str(d / "galatay-mcp.sh")], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env)
    p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}}).encode() + b"\n"); p.stdin.flush()
    r = json.loads(p.stdout.readline())
    assert r["result"]["serverInfo"]["name"] == "stub"
    p.stdin.close(); p.wait(30)
    assert p.returncode == 0
