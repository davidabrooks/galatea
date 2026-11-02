"""2026-11-02 VAMOSITG25 (David): a true average of the weight-bearing contacts (hips and feet higher than the hands) sinks the hands, but only so far:
no part sinks deeper than SINK_FRACTION of its own thickness, so some of each hand stays visible."""
import ast, pathlib, numpy as np
SRC = pathlib.Path(__file__).resolve().parents[1] / "cloud-render" / "render_mesh.py"

def _ns():
    t = ast.parse(SRC.read_text()); ns = {"np": np}
    keep = [n for n in t.body if (isinstance(n, ast.FunctionDef) and n.name in ("contact_level", "part_thickness")) or (isinstance(n, ast.Assign) and getattr(n.targets[0], "id", "") == "SINK_FRACTION")]
    exec(compile(ast.Module(keep, []), "rm", "exec"), ns); return ns

def test_unclamped_true_average():
    z, c = _ns()["contact_level"]({"hips": 0.00, "l hand": -0.01, "l foot": 0.01}, {"hips": 0.3, "l hand": 0.04, "l foot": 0.05})
    assert abs(z - 0.0) < 1e-9 and not c

def test_thin_hand_clamps_the_average():
    ns = _ns(); touch = {"hips": 0.03, "l hand": -0.05, "r hand": -0.05, "l foot": 0.02}; thick = {"hips": 0.3, "l hand": 0.04, "r hand": 0.04, "l foot": 0.05}
    z, c = ns["contact_level"](touch, thick)
    assert c and abs(z - (-0.05 + ns["SINK_FRACTION"] * 0.04)) < 1e-9   # average -0.0125 would sink the hand 3.75 of its 4 cm
    assert z - touch["l hand"] <= ns["SINK_FRACTION"] * thick["l hand"] + 1e-12

def test_thickness_is_the_thin_axis():
    r = np.random.default_rng(0); slab = np.c_[r.uniform(0, .08, 4000), r.uniform(0, .10, 4000), r.uniform(0, .03, 4000)]
    assert abs(_ns()["part_thickness"](slab) - 0.027) < 0.003

def test_a_quarter_of_each_part_stays_above_the_floor():
    """2026-11-02 12:25 David at 0.5: "you can sink more of your hands into the floor"; deeper, but some of the hand must show"""
    f = _ns()["SINK_FRACTION"]; assert 0.5 < f <= 0.75
