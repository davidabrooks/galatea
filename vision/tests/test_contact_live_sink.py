"""2026-11-02 12:13: the log said "r hand +0.017" sunk while the render showed the palm ON the floor: the sink was measured from the contact level,
not from the floor at the hover in force (gap +0.027). The live sink subtracts the gap."""
import ast, pathlib
SRC = pathlib.Path(__file__).resolve().parents[1] / "cloud-render" / "render_mesh.py"

def _f():
    t = ast.parse(SRC.read_text()); ns = {}
    exec(compile(ast.Module([n for n in t.body if isinstance(n, ast.FunctionDef) and n.name == "live_sink"], []), "rm", "exec"), ns); return ns["live_sink"]

def test_level_floating_above_floor_means_no_real_sink():
    d = _f()({"r hand": 0.017, "hips": -0.008}, 0.027)
    assert d["r hand"] < 0 and abs(d["r hand"] + 0.010) < 1e-9 and abs(d["hips"] + 0.035) < 1e-9

def test_level_on_the_floor_keeps_the_sink():
    assert _f()({"r hand": 0.017}, 0.0) == {"r hand": 0.017}

def test_logged_where_the_gap_is_known():
    s = SRC.read_text(); assert "live_sink(td_[\"sink\"], HC[1])" in s and "suggested hover {HC[2] - HC[1]:+.3f}" in s
