"""2026-11-02 VAMOSITG25: the hover check read her fingertips on the floor while the heels of her palms floated 1-2 cm above it (David: "look at your
palms; fingers are not strong enough to support us"). Fingers (past the wrist->middle-knuckle hand segment) weigh 0; the palm sets the hand's level."""
import ast, pathlib, numpy as np
SRC = pathlib.Path(__file__).resolve().parents[1] / "cloud-render" / "render_mesh.py"
NAMES = {"finger_mask", "touch_contact", "support_z", "com_inside", "assign_body_parts_idx"}

def _ns():
    t = ast.parse(SRC.read_text()); ns = {"np": np}
    keep = [n for n in t.body if (isinstance(n, ast.FunctionDef) and n.name in NAMES) or (isinstance(n, ast.Assign) and any(getattr(x, "id", "") in ("HAND_SEGS", "TOUCH_REGION_SEGS", "PART_MIN_VERTS") for x in n.targets))]
    exec(compile(ast.Module(keep, []), "rm", "exec"), ns); return ns

def _segs():
    S = np.zeros((25, 2, 3)); S[:, 1, 2] = 0.01   # tiny far-away segments
    for i in range(25): S[i] += (10 + i, 10, 1)
    S[9] = [(-0.5, 0.0, 0.10), (-0.4, 0.0, 0.10)]   # left hand: wrist -> middle knuckle (palm), 2 cm above the floor plane z 0.08
    S[0] = [(0, 0, 0.15), (0, 0, 0.30)]; S[15] = [(0, 0, 0.15), (0.1, 0.1, 0.15)]; S[20] = [(0, 0, 0.15), (0.1, -0.1, 0.15)]
    S[18] = [(0.6, 0, 0.12), (0.7, 0, 0.10)]; S[23] = [(0.6, 0.2, 0.12), (0.7, 0.2, 0.10)]
    return S

def _verts():
    r = np.random.default_rng(1); g = lambda c, n=60, z=0.0: np.array(c) + np.c_[r.uniform(-.02, .02, (n, 2)), np.full(n, z)]
    palm = g((-0.45, 0, 0.08)) + (0, 0, 0.02)       # heel of the hand at z 0.10
    fingers = g((-0.33, 0, 0.0)) + (0, 0, 0.06)     # fingertips past the knuckle, curled down to z 0.06
    seat = np.r_[g((0, 0, 0.10)), g((0.05, 0.25, 0.10)), g((0.05, -0.25, 0.10))]
    feet = np.r_[g((0.7, 0, 0.10)), g((0.7, 0.2, 0.10))]
    return np.r_[palm, fingers, seat, feet], fingers

def test_fingers_are_masked():
    ns = _ns(); v, f = _verts(); m = ns["finger_mask"](v, _segs())
    assert m[len(v) - len(f) - 300:][:0].size == 0 and m.sum() == len(f)

def test_fingertips_below_palm_do_not_set_the_level():
    ns = _ns(); v, _ = _verts(); r = ns["touch_contact"](v, _segs(), (0.0, 0.0))
    assert r is not None
    z, touch, _ = r
    assert all(abs(t - 0.10) < 1e-6 for t in touch.values()), touch   # palm heel 0.10, not the 0.06 fingertips
    assert abs(z - 0.10) < 1e-6
