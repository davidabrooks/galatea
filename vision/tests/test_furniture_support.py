"""Furniture pose kind and weight-bearing contact read from the render (2026-11-02, David: standing, sitting, kneeling, lying or hand-supported
poses on furniture; average the weight-bearing parts against the surface they rest on; no per-pose assumptions). Synthetic geometry only."""
import ast, math, pathlib
import numpy as np

SRC = pathlib.Path(__file__).resolve().parents[1] / "cloud-render" / "render_mesh.py"

def _rm():
    tree = ast.parse(SRC.read_text())
    consts = {"CONTACT_BAND", "SEAT_PLANE_BELOW", "SEAT_TOP_NEAR", "SEAT_RAY_START", "BONE_PART_SEGS", "SUPPORT_SOFT"}
    body = [n for n in tree.body if isinstance(n, ast.Assign) and any(getattr(t, "id", "") in consts for t in n.targets)] + \
           [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in {"seat_top_under", "furniture_support_parts", "classify_support", "furniture_support_line"}]
    ns = {"np": np, "math": math}; exec(compile(ast.Module(body, []), "render_mesh", "exec"), ns); return ns

R = _rm()
SURF = 0.45
flat = lambda x, y, z: SURF if SURF <= z else None   # one flat bed / seat top at 0.45

def _part(rng, z0, z1, n=200, x=0.0):
    return np.column_stack([rng.uniform(x - 0.1, x + 0.1, n), rng.uniform(-0.1, 0.1, n), rng.uniform(z0, z1, n)])

def _info(lows, rng=None):
    rng = rng or np.random.default_rng(1)
    return R["furniture_support_parts"]({k: _part(rng, z, z + 0.15) for k, z in lows.items()}, flat)

def test_sitting_hips_thighs_bear_feet_dangle():
    inf = _info({"hips": SURF + 0.03, "thighs": SURF + 0.04, "shins": 0.25, "feet": 0.12, "chest": 0.8, "hands": SURF + 0.3})
    kind, bear, gap = R["classify_support"](inf)
    assert kind == "sitting" and bear == ["hips", "thighs"] and abs(gap - 0.035) < 0.01

def test_kneeling_on_a_bed():
    inf = _info({"shins": SURF + 0.0, "feet": SURF + 0.02, "hips": SURF + 0.35, "thighs": SURF + 0.2, "chest": SURF + 0.6})
    kind, bear, _ = R["classify_support"](inf)
    assert kind == "kneeling" and bear == ["shins", "feet"]

def test_standing_on_a_stage():
    inf = _info({"feet": SURF - 0.05, "shins": SURF + 0.3, "thighs": SURF + 0.6, "hips": SURF + 0.9})
    kind, bear, gap = R["classify_support"](inf)
    assert kind == "standing" and bear == ["feet"] and gap < 0   # sunk 5 cm: the correction lifts her

def test_lying_back_on_a_bed():
    inf = _info({"chest": SURF + 0.01, "hips": SURF + 0.02, "thighs": SURF + 0.03, "shins": SURF + 0.03, "feet": SURF + 0.05})
    kind, bear, _ = R["classify_support"](inf)
    assert kind == "lying" and bear == ["chest", "hips", "thighs"]

def test_kneeling_on_hands_counts_the_palms():
    inf = _info({"shins": SURF, "hands": SURF + 0.01, "hips": SURF + 0.3, "chest": SURF + 0.35})
    kind, bear, _ = R["classify_support"](inf)
    assert kind == "kneeling+hands" and "hands" in bear and "shins" in bear

def test_floating_pose_still_classified_by_what_is_lowest():
    inf = _info({"hips": SURF + 0.15, "thighs": SURF + 0.16, "feet": 0.0 + 0.2, "chest": SURF + 0.5})   # 15 cm over the seat
    kind, bear, gap = R["classify_support"](inf)
    assert kind == "sitting" and gap > 0.1

def test_no_surface_is_unknown():
    inf = R["furniture_support_parts"]({"hips": _part(np.random.default_rng(2), 0.5, 0.6)}, lambda x, y, z: None)
    assert R["classify_support"](inf) == (None, [], None)
    assert "unknown" in R["furniture_support_line"](inf, None, [], None)

def test_line_format():
    inf = _info({"hips": SURF + 0.03, "thighs": SURF + 0.04})
    line = R["furniture_support_line"](inf, *R["classify_support"](inf))
    assert line.startswith("GT: furniture support: sitting; bearing hips,thighs; gap +0.0") and "parts hips +0.0" in line
