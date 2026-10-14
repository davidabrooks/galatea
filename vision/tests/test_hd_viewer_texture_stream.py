"""2026-10-14: photo hd memory = the SL viewer's texture streaming (discard from on-screen size, bias under pressure),
retry lighter after a memory-guard kill, and cancel kills every render child."""
import json, os, signal, struct, subprocess, sys, time, types
HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(HERE, "cloud-render"))
import importlib.util  # noqa: E402
_spec = importlib.util.spec_from_file_location("gt_real_handler", os.path.join(HERE, "cloud-render", "handler.py"))
handler = importlib.util.module_from_spec(_spec); _spec.loader.exec_module(handler)   # other tests stub `handler`
sys.modules.setdefault("make_job", types.ModuleType("make_job"))
sys.path.insert(0, HERE)
import look  # noqa: E402


def test_discard_cap_is_pow2_edge_of_virtual_size():
    assert handler.discard_cap(0) == handler.MIN_TEX
    assert handler.discard_cap(300 * 300) == 512
    assert handler.discard_cap(512 * 512) == 512
    assert handler.discard_cap(5000 * 5000) == 2048          # RenderMaxTextureResolution
    assert handler.discard_cap(5000 * 5000, 1024) == 1024


def test_near_faces_full_far_faces_small():
    near = handler.viewer_cap((2.0, 1.0, (1, 1)), 2048, 2160)
    mid = handler.viewer_cap((60.0, 8.0, (1, 1)), 2048, 2160)
    far = handler.viewer_cap((200.0, 0.5, (1, 1)), 2048, 2160)
    assert near == 2048 and mid < near and far <= 128 and far < mid
    # repeats: a 10x tiled floor needs each tile at a tenth of the size
    assert handler.viewer_cap((10.0, 5.0, (10, 10)), 2048, 2160) < handler.viewer_cap((10.0, 5.0, (1, 1)), 2048, 2160)
    assert handler.viewer_cap(None, 1024, 2160) == 1024


def test_bias_halves_edges_like_viewer():
    g = (30.0, 3.0, (1, 1))
    c0 = handler.viewer_cap(g, 2048, 2160, 0)
    assert handler.viewer_cap(g, 2048, 2160, 2.0) == max(handler.MIN_TEX, c0 // 2)


def _scene(tmp_path, items):
    """mesh.json/mesh.bin with one square face per (dist, half size, repeats, tex, group)"""
    batches, blob = [], b""
    for d, h, rep, tex, grp in items:
        P = [(d - h, -h, 0), (d + h, -h, 0), (d + h, h, 0), (d - h, h, 0)]
        N = [(0, 0, 1)] * 4; T = [(0, 0), (rep, 0), (rep, rep), (0, rep)]
        off = len(blob)
        blob += b"".join(struct.pack("3f", *v) for v in P) + b"".join(struct.pack("3f", *v) for v in N)
        blob += b"".join(struct.pack("2f", *v) for v in T) + struct.pack("6I", 0, 1, 2, 0, 2, 3)
        batches.append({"group": grp, "tex": tex, "nv": 4, "ni": 6, "offset": off})
    (tmp_path / "mesh.bin").write_bytes(blob)
    return {"batches": batches, "me": [0, 0, 0], "others": []}


def test_fetch_textures_streams_and_budget(tmp_path, monkeypatch):
    u = lambda i: f"{i:08x}-0000-0000-0000-000000000000"
    meta = _scene(tmp_path, [(1.0, 1.0, 1, u(1), "scene")] + [(100.0 + i, 1.0, 1, u(10 + i), "far") for i in range(20)]
                  + [(0, 0.3, 1, u(99), "avatar")])
    got = {}
    monkeypatch.setattr(handler, "fetch_tex", lambda t, d, c: (got.__setitem__(t, c), (t, f"{d}/{t}.{c}.png", None))[1])
    monkeypatch.setenv("GT_TEX_CAP_SCENE", "2048"); monkeypatch.setenv("GT_TEX_CAP_AVATAR", "2048")
    monkeypatch.setenv("GT_TEX_DIST", "1"); monkeypatch.setenv("GT_RES", "3840x2160"); monkeypatch.setenv("GT_TEX_MAPS", "0")
    out = {}; assert handler.fetch_textures(meta, out, str(tmp_path)) is None
    assert got[u(1)] == 2048 and got[u(99)] == 2048 and all(got[u(10 + i)] <= 256 for i in range(20))
    monkeypatch.setenv("GT_TEX_BUDGET_MB", "40")      # memory pressure: bias up, her avatar keeps full size
    out = {}; assert handler.fetch_textures(meta, out, str(tmp_path)) is None
    assert out["textures"]["discard_bias"] >= 1.5 and got[u(99)] == 2048 and got[u(1)] < 2048


def test_retry_gets_lighter_each_attempt(monkeypatch):
    monkeypatch.setenv("GT_TEX_BIAS", "0"); monkeypatch.setenv("GT_TEX_BUDGET_MB", "4000")
    r1 = look.retry_env(-9, True, 1)
    monkeypatch.setenv("GT_TEX_BIAS", r1["GT_TEX_BIAS"]); monkeypatch.setenv("GT_TEX_BUDGET_MB", r1["GT_TEX_BUDGET_MB"])
    r2 = look.retry_env(-9, True, 2)
    assert float(r2["GT_TEX_BIAS"]) > float(r1["GT_TEX_BIAS"]) >= 2
    assert float(r2["GT_TEX_BUDGET_MB"]) < float(r1["GT_TEX_BUDGET_MB"]) < 4000
    assert look.retry_env(-11, True) == {} and look.retry_env(-9, False) == {}


def test_tex_budget_from_free_memory():
    assert look.tex_budget_mb(11000, 1500) == 6000
    assert look.tex_budget_mb(7000, 1500) == 2500
    assert look.tex_budget_mb(3000, 1500) == 600
    assert look.tex_budget_mb(None) == 3000


def test_cancel_kills_render_children(tmp_path):
    """SIGTERM to look.py's process: its handler kills the render child's process group (grandchildren too)."""
    script = tmp_path / "parent.py"
    script.write_text(f"""
import sys; sys.path.insert(0, {HERE!r}); sys.path.insert(0, {os.path.join(HERE, 'cloud-render')!r})
import types; sys.modules.setdefault('make_job', types.ModuleType('make_job'))
import look
look.install_cancel_handlers()
look.run_mem_guarded(['bash', '-c', 'sleep 300 & echo $! > {tmp_path}/gc; sleep 300'], None, floor_mb=0)
""")
    p = subprocess.Popen([sys.executable, str(script)])
    for _ in range(100):
        if (tmp_path / "gc").exists() and (tmp_path / "gc").read_text().strip(): break
        time.sleep(0.1)
    gc = int((tmp_path / "gc").read_text())
    p.send_signal(signal.SIGTERM); p.wait(10)
    time.sleep(0.3)
    try:
        os.kill(gc, 0); alive = open(f"/proc/{gc}/stat").read().split()[2] != "Z"
    except (OSError, FileNotFoundError): alive = False
    assert not alive
