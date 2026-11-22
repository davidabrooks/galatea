"""Seat close-up widen levels for a too-dark render (GT_WIDEN, with GT_SUPPORT_WIDE=1). Pure math from render_mesh.py; no Blender.
Level 0 = the close-up, level 1 = the wide retake exactly as before (1.6-3.0 m), 2 and 3 step back further, capped."""
import ast, math, os, re

SRC = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "cloud-render", "render_mesh.py")
NAMES = ("widen_width", "widen_level")
CONSTS = ("WIDE_MIN_W", "WIDEN_MAX_LEVEL", "WIDEN_CAP_W")

def _ns():
    src = open(SRC).read(); ns = {"math": math}
    for n in ast.parse(src).body:
        if isinstance(n, ast.FunctionDef) and n.name in NAMES or isinstance(n, ast.Assign) and any(
                getattr(t, "id", "") in CONSTS or isinstance(t, ast.Tuple) and any(getattr(e, "id", "") in CONSTS for e in t.elts) for t in n.targets):
            exec(ast.get_source_segment(src, n), ns)
    return ns

def test_level_zero_keeps_the_close_up_limits():
    w = _ns()["widen_width"]
    assert w(0.35, 0.8, 0) == (0.35, 0.8)     # part view
    assert w(0.55, 1.1, 0) == (0.55, 1.1)     # hips view

def test_level_one_matches_the_existing_wide_retake():
    w = _ns()["widen_width"]
    assert w(0.55, 1.1, 1) == (1.6, 3.0) and w(0.35, 0.8, 1) == (1.6, 3.0)

def test_each_level_is_wider_than_the_last_up_to_the_cap():
    ns = _ns(); w = ns["widen_width"]
    lv = [w(0.55, 1.1, n) for n in range(0, ns["WIDEN_MAX_LEVEL"] + 1)]
    for a, b in zip(lv, lv[1:]):
        assert b[0] > a[0] and b[1] > a[1]                # both limits grow: the camera really steps back
    assert lv[-1] == (6.4, 8.0)
    assert all(lo <= hi <= ns["WIDEN_CAP_W"] for lo, hi in lv)
    assert w(0.55, 1.1, 99) == lv[-1]                     # past the cap = the cap

def test_never_narrower_than_the_wide_retake():
    w = _ns()["widen_width"]
    for n in range(1, 10):
        lo, hi = w(0.55, 1.1, n)
        assert lo >= 1.6 and hi >= 3.0

def test_widen_level_validates_the_setting():
    lvl = _ns()["widen_level"]
    assert lvl(None, True) == (1, None) and lvl("", True) == (1, None)
    assert lvl("2", True) == (2, None) and lvl(" 3 ", True) == (3, None)
    assert lvl("0", True) == (1, None)                                   # the wide retake is level 1 at least
    for bad in ("abc", "2.5", "-1", "1e3"):
        n, note = lvl(bad, True)
        assert n == 1 and "using level 1" in note                       # a bad value falls back, never raises
    n, note = lvl("7", True); assert n == 3 and "cap" in note
    assert lvl("2", False)[0] == 0 and "ignored" in lvl("2", False)[1]  # no wide retake, no widening
    assert lvl(None, False) == (0, None) and lvl("0", False) == (0, None)

def test_the_render_uses_the_validated_level():
    src = open(SRC).read()
    assert 'widen_level(os.environ.get("GT_WIDEN"), wide_)' in src
    assert not re.search(r'int\(os\.environ\.get\("GT_WIDEN"', src)    # no raw int() on the setting
