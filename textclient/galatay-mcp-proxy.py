#!/usr/bin/env python3
"""galatay-mcp-proxy: a thin stdio MCP proxy in front of the real connector (mcp-app/galatay-mcp).

Why (2026-10-28): the agent host only reloads a stdio connector on remove + re-add. The proxy is what the host launches
(galatay-mcp.sh -> this file); the real connector runs as its child (galatay-mcp-child.sh). A new connector build is
loaded by restarting only the child (restart-connector.sh, or SIGHUP / SIGUSR1 to the proxy); the host never notices
except for a notifications/tools/list_changed.

Protocol rules: stdout carries only MCP JSON-RPC lines; the child's stderr goes straight to our stderr; a non-JSON line
on the child's stdout is moved to stderr. The proxy remembers the client's `initialize` request and
`notifications/initialized` and replays them (response swallowed) to every new child. It advertises
capabilities.tools.listChanged = true to the client so the client honours the list_changed notification.

Restart (SIGHUP = soft, SIGUSR1 = force, or touch run/restart-connector, content 'force' = force):
  soft is refused while the child holds the Second Life session (run/sl-session.pid names the child's pid), because
  stopping the child logs Galatay out. Pending client requests get a JSON-RPC error (-32001). The child gets SIGTERM
  (graceful: logs out if needed), SIGKILL after GT_PROXY_STOP_S. An unexpected child exit is respawned once per
  GT_PROXY_RESPAWN_WINDOW_S; a second death inside the window ends the proxy (exit 1), so the host sees a dead connector.
State file: <run dir>/connector-proxy-<proxy pid>.json (restart-connector.sh reads it).
Env: GT_SOCK (run dir = its dirname), GT_PROXY_CHILD_CMD (default: galatay-mcp-child.sh beside this file; shlex-split),
GT_PROXY_STOP_S (30), GT_PROXY_INIT_TIMEOUT_S (60), GT_PROXY_RESPAWN_WINDOW_S (60).
"""
import hashlib, json, os, shlex, signal, subprocess, sys, threading, time

HERE = os.path.dirname(os.path.abspath(__file__))
SOCK = os.environ.get("GT_SOCK", "/home/box/viewers/textclient/run/galatay.sock")
RUN_DIR = os.path.dirname(SOCK)
CHILD_CMD = shlex.split(os.environ.get("GT_PROXY_CHILD_CMD", "")) or [os.path.join(HERE, "galatay-mcp-child.sh")]
STOP_S = float(os.environ.get("GT_PROXY_STOP_S", "30"))
INIT_TIMEOUT_S = float(os.environ.get("GT_PROXY_INIT_TIMEOUT_S", "60"))
RESPAWN_WINDOW_S = float(os.environ.get("GT_PROXY_RESPAWN_WINDOW_S", "60"))
TRIGGER = os.path.join(RUN_DIR, "restart-connector")
STATE = os.path.join(RUN_DIR, f"connector-proxy-{os.getpid()}.json")
LOCK_PID = os.path.join(RUN_DIR, "sl-session.pid")
ERR_RESTART = -32001

out_lock = threading.Lock()      # one writer on our stdout
child_lock = threading.RLock()   # guards child swap; client->child writes and restarts exclude each other
pend_lock = threading.Lock()     # guards `pending` only (the reader thread must never wait on child_lock: a restart joins it)
shutdown = threading.Event()
wake = threading.Event()         # restart request or child death
req_force = [False]
req_pending = [False]

child = None                     # subprocess.Popen of the current child
child_gen = 0
pending = {}                     # json.dumps(id) -> id: client requests the current child has not answered yet
init_line = None                 # raw bytes of the client's initialize request
initialized_line = None          # raw bytes of the client's notifications/initialized
replay_wait = {}                 # replay id -> (Event, [response])
state = {"restarts": 0, "last_restart": None, "last_result": "started", "last_refusal": None}
exit_code = [0]


def log(msg):
    sys.stderr.write(f"{time.strftime('%F %T')} [mcp-proxy] {msg}\n"); sys.stderr.flush()


def emit(line: bytes):
    with out_lock:
        sys.stdout.buffer.write(line if line.endswith(b"\n") else line + b"\n"); sys.stdout.buffer.flush()


def emit_obj(o):
    emit(json.dumps(o, separators=(",", ":")).encode())


def parse(line: bytes):
    try:
        o = json.loads(line)
        return o if isinstance(o, dict) else None
    except Exception:
        return None


def proc_start_time(pid):
    try:
        ticks = int(open(f"/proc/{pid}/stat").read().rsplit(")", 1)[1].split()[19])
        btime = next(int(l.split()[1]) for l in open("/proc/stat") if l.startswith("btime"))
        return btime + ticks / os.sysconf("SC_CLK_TCK")
    except Exception:
        return None


def child_dll_sha(pid):
    """sha256 of the galatay-mcp.dll next to the child's executable (what the process really runs)."""
    try:
        exe = os.readlink(f"/proc/{pid}/exe")
        if exe.endswith(" (deleted)"): return None
        dll = os.path.join(os.path.dirname(exe), "galatay-mcp.dll")
        with open(dll, "rb") as f: return hashlib.sha256(f.read()).hexdigest()
    except Exception:
        return None


state_lock = threading.Lock()


def write_state():
    with state_lock:
        _write_state()


def _write_state():
    c = child
    d = {"proxy_pid": os.getpid(), "proxy_start": proc_start_time(os.getpid()), "child_pid": c.pid if c else None,
         "child_start": proc_start_time(c.pid) if c else None, "child_gen": child_gen,
         "child_dll_sha256": child_dll_sha(c.pid) if c else None, "child_exe": None,
         "run_dir": RUN_DIR, "updated": time.time(), **state}
    try: d["child_exe"] = os.readlink(f"/proc/{c.pid}/exe") if c else None
    except Exception: pass
    tmp = STATE + ".tmp"
    try:
        with open(tmp, "w") as f: json.dump(d, f)
        os.replace(tmp, STATE)
    except Exception as e:
        log(f"state file write failed: {e}")


def session_held_by_child():
    """True when the Second Life session lives in our child (run/sl-session.pid, written at login, removed at logout)."""
    c = child
    try:
        return c is not None and int(open(LOCK_PID).read().strip()) == c.pid and c.poll() is None
    except Exception:
        return False


def fail_pending(why):
    with pend_lock:
        items = list(pending.values()); pending.clear()
    for rid in items:
        emit_obj({"jsonrpc": "2.0", "id": rid, "error": {"code": ERR_RESTART, "message": why}})
    if items: log(f"{len(items)} in-flight request(s) failed: {why}")


def child_reader(c):
    """Forward the child's stdout lines to the client until EOF."""
    for line in iter(c.stdout.readline, b""):
        msg = parse(line)
        if msg is None:
            sys.stderr.write("[mcp-child-stdout] " + line.decode(errors="replace").rstrip("\n") + "\n"); sys.stderr.flush()
            continue
        if "method" not in msg and "id" in msg:
            key = json.dumps(msg["id"])
            w = replay_wait.get(msg["id"]) if isinstance(msg["id"], str) else None
            if w is not None:   # response to our replayed initialize: swallow
                w[1].append(msg); w[0].set(); continue
            with pend_lock:
                if c is child: pending.pop(key, None)
            if "result" in msg and isinstance(msg["result"], dict) and "capabilities" in msg["result"] and "serverInfo" in msg["result"]:
                caps = msg["result"]["capabilities"]
                if isinstance(caps, dict):
                    t = caps.get("tools")
                    if isinstance(t, dict): t["listChanged"] = True
                    elif t is None: caps["tools"] = {"listChanged": True}
                emit_obj(msg); continue
        emit(line)
    if c is child and not shutdown.is_set():
        wake.set()   # died on its own (a restart waits for the reader itself and then swaps `child` first)


def spawn():
    global child, child_gen
    c = subprocess.Popen(CHILD_CMD, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=None, close_fds=True)
    child = c; child_gen += 1
    t = threading.Thread(target=child_reader, args=(c,), daemon=True); t.start(); c._reader = t
    log(f"child started: pid {c.pid} gen {child_gen} cmd {' '.join(CHILD_CMD)}")
    threading.Thread(target=settle_state, args=(c,), daemon=True).start()
    return c


def settle_state(c):
    """The child starts as the launcher (bash); once it has exec'd the real connector, rewrite the state file so child_exe
    and child_dll_sha256 describe the connector itself (restart-connector.sh checks them)."""
    for _ in range(100):
        if c is not child or c.poll() is not None: return
        try: exe = os.path.basename(os.readlink(f"/proc/{c.pid}/exe"))
        except OSError: return
        if exe not in ("bash", "sh", "dash"): break
        time.sleep(0.1)
    if c is child: write_state()


def stop_child(c, why):
    if c is None or c.poll() is not None:
        if c is not None: c._reader.join(5)
        return
    log(f"stopping child pid {c.pid} ({why})")
    try: c.send_signal(signal.SIGTERM)
    except Exception: pass
    try: c.wait(STOP_S)
    except subprocess.TimeoutExpired:
        log(f"child pid {c.pid} ignored SIGTERM for {STOP_S:g}s: SIGKILL"); c.kill(); c.wait(10)
    c._reader.join(5)
    try: c.stdin.close()
    except Exception: pass


def replay_handshake(c):
    """Replay initialize + initialized to a fresh child (response swallowed); the client already finished its handshake."""
    global init_line, initialized_line
    if init_line is None: return True
    rid = f"proxy-init-{child_gen}"
    ev = (threading.Event(), []); replay_wait[rid] = ev
    msg = parse(init_line); msg["id"] = rid
    try:
        c.stdin.write(json.dumps(msg, separators=(",", ":")).encode() + b"\n"); c.stdin.flush()
        if not ev[0].wait(INIT_TIMEOUT_S):
            log(f"replayed initialize got no response in {INIT_TIMEOUT_S:g}s"); return False
        if "error" in ev[1][0]: log(f"replayed initialize returned an error: {ev[1][0]['error']}"); return False
        if initialized_line is not None:
            c.stdin.write(initialized_line if initialized_line.endswith(b"\n") else initialized_line + b"\n"); c.stdin.flush()
        return True
    except (BrokenPipeError, OSError) as e:
        log(f"replay failed: {e}"); return False
    finally:
        replay_wait.pop(rid, None)


def do_restart(force, reason):
    """Stop the child, start a fresh one, replay the handshake, tell the client the tool list may have changed."""
    global child
    with child_lock:
        if not force and session_held_by_child():
            state["last_refusal"] = time.time(); state["last_result"] = "refused: Galatay is logged in (use force)"
            log("restart REFUSED: the child holds the Second Life session (force = SIGUSR1 / --force)"); write_state(); return False
        old = child
        stop_child(old, reason)
        fail_pending(f"connector restarted ({reason}); retry the call")
        c = spawn()
        ok = replay_handshake(c)
        state["restarts"] += 1; state["last_restart"] = time.time()
        state["last_result"] = f"ok ({reason})" if ok else f"handshake replay FAILED ({reason})"
        write_state()
    if ok and init_line is not None:
        emit_obj({"jsonrpc": "2.0", "method": "notifications/tools/list_changed"})
    log(f"restart done: {state['last_result']}, child pid {c.pid}")
    return ok


def stdin_loop():
    """Client -> child. Blocks on child_lock while a restart is running."""
    global init_line, initialized_line
    rd = sys.stdin.buffer
    for line in iter(rd.readline, b""):
        if not line.strip(): continue
        msg = parse(line)
        with child_lock:
            if msg is not None:
                m = msg.get("method")
                if m == "initialize" and "id" in msg: init_line = line
                elif m == "notifications/initialized": initialized_line = line
                if m is not None and "id" in msg:
                    with pend_lock: pending[json.dumps(msg["id"])] = msg["id"]
            try:
                child.stdin.write(line if line.endswith(b"\n") else line + b"\n"); child.stdin.flush()
            except (BrokenPipeError, OSError):
                if msg is not None and "method" in msg and "id" in msg:
                    with pend_lock: pending.pop(json.dumps(msg["id"]), None)
                    emit_obj({"jsonrpc": "2.0", "id": msg["id"], "error": {"code": ERR_RESTART, "message": "connector is restarting; retry the call"}})
    log("client closed stdin"); shutdown.set(); wake.set()


def on_hup(*_): req_pending[0] = True; wake.set()
def on_usr1(*_): req_force[0] = True; req_pending[0] = True; wake.set()
def on_term(*_): shutdown.set(); wake.set()


def trigger_mtime():
    try: return os.stat(TRIGGER).st_mtime_ns
    except OSError: return 0


def main():
    os.makedirs(RUN_DIR, exist_ok=True)
    signal.signal(signal.SIGHUP, on_hup); signal.signal(signal.SIGUSR1, on_usr1)
    signal.signal(signal.SIGTERM, on_term); signal.signal(signal.SIGINT, on_term)
    last_trig = trigger_mtime()
    with child_lock:
        spawn(); write_state()
    threading.Thread(target=stdin_loop, daemon=True).start()
    deaths = []
    while not shutdown.is_set():
        wake.wait(1.0); wake.clear()
        if shutdown.is_set(): break
        mt = trigger_mtime()
        if mt != last_trig:
            last_trig = mt
            try: req_force[0] = open(TRIGGER).read().strip() == "force"
            except OSError: req_force[0] = False
            req_pending[0] = True
        if req_pending[0]:
            force = req_force[0]; req_pending[0] = False; req_force[0] = False
            do_restart(force, "force restart" if force else "restart requested")
            continue
        c = child
        if c is not None and c.poll() is not None:
            c._reader.join(5)
            now = time.time(); deaths = [t for t in deaths if now - t < RESPAWN_WINDOW_S]
            log(f"child pid {c.pid} exited unexpectedly (code {c.returncode})")
            if deaths:
                log(f"second unexpected exit within {RESPAWN_WINDOW_S:g}s: giving up")
                fail_pending("connector process exited twice in a row; the connector is down"); exit_code[0] = 1; break
            deaths.append(now)
            with child_lock:
                fail_pending("connector process exited; it was restarted, retry the call")
                cn = spawn(); ok = replay_handshake(cn)
                state["last_result"] = "respawned after unexpected exit" if ok else "respawn handshake FAILED"; write_state()
            if ok and init_line is not None: emit_obj({"jsonrpc": "2.0", "method": "notifications/tools/list_changed"})
    # shutdown: graceful child stop (it logs out if logged in, like the old direct launch on stdin EOF)
    with child_lock:
        c = child
        if c is not None and c.poll() is None:
            try: c.stdin.close()
            except Exception: pass
            try: c.wait(STOP_S)
            except subprocess.TimeoutExpired: stop_child(c, "proxy shutdown")
    try: os.unlink(STATE)
    except OSError: pass
    return exit_code[0]


if __name__ == "__main__":
    rc = main()
    with out_lock:   # hard exit: daemon threads (client stdin reader, child readers) must not run during interpreter shutdown
        try: sys.stdout.flush(); sys.stderr.flush()
        except Exception: pass
        os._exit(rc)
