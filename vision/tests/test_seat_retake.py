"""Seat-check retakes (David 2026-11-22: "Cap every retake at 1 m, then rotate 180"). Pure math from render_mesh.py; no Blender.
The retake camera is never more than RETAKE_MAX_CAM_M from her; a still-too-dark retake is taken again from the opposite side (GT_RETAKE_TURN=180)."""
import ast, math, os, re

SRC = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "cloud-render", "render_mesh.py")
NAMES = ("retake_turn", "cap_camera_distance", "feet_camera_spec", "self_body_lens")

def _ns():
    src = open(SRC).read(); ns = {"math": math}
    for n in ast.parse(src).body:
        if isinstance(n, ast.Assign) and any(getattr(t, "id", "") == "RETAKE_MAX_CAM_M" for t in n.targets):
            exec(ast.get_source_segment(src, n), ns)
    for n in ast.parse(src).body:
        if isinstance(n, ast.FunctionDef) and n.name in NAMES: exec(ast.get_source_segment(src, n), ns)
    return ns

def test_cap_is_one_metre():
    assert _ns()["RETAKE_MAX_CAM_M"] == 1.0

def test_a_far_retake_camera_is_pulled_in_to_one_metre_on_the_same_line():
    ns = _ns(); cap = ns["cap_camera_distance"]
    # the old wide retake: a 1.6-3.0 m frame at 50 mm puts the camera > 2 m away
    loc, tgt, d, w = ns["feet_camera_spec"](10.0, 20.0, 0.0, 90.0, [(10.1, 20.0, 5.0), (9.8, 20.1, 5.0)], 5.0, 4.9, min_width=1.6, max_width=3.0)
    assert math.hypot(loc[0] - 10.0, loc[1] - 20.0) > 2.0
    new, capped = cap(loc, tgt, 10.0, 20.0)
    assert capped and abs(math.hypot(new[0] - 10.0, new[1] - 20.0) - 1.0) < 1e-6
    assert new[2] == loc[2]                                                    # same height
    v0 = (loc[0] - tgt[0], loc[1] - tgt[1]); v1 = (new[0] - tgt[0], new[1] - tgt[1])
    assert abs(v0[0] * v1[1] - v0[1] * v1[0]) < 1e-9 and v0[0] * v1[0] + v0[1] * v1[1] > 0   # same line, same side: still aimed at the contact
    # the lens widens so the frame still fits (14 mm floor)
    lens = ns["self_body_lens"](d, math.hypot(v1[0], v1[1]), 50.0)
    assert 14.0 <= lens < 50.0

def test_a_near_camera_is_left_alone_and_odd_geometry_never_raises():
    cap = _ns()["cap_camera_distance"]
    assert cap((10.5, 20.0, 5.0), (10.0, 20.0, 5.0), 10.0, 20.0) == ((10.5, 20.0, 5.0), False)
    new, capped = cap((15.0, 20.0, 5.0), (12.0, 20.0, 5.0), 10.0, 20.0)       # the target itself is 2 m from her
    assert capped and abs(new[0] - 12.05) < 1e-9                              # stops just in front of the target
    assert cap((10.0, 20.0, 5.0), (10.0, 20.0, 5.0), 0.0, 0.0)[1] is False    # degenerate line

def test_opposite_side_is_the_same_distance():
    ns = _ns(); spec, cap = ns["feet_camera_spec"], ns["cap_camera_distance"]
    cp = [(10.05, 20.0, 5.0), (9.95, 20.0, 5.0)]
    a, ta, *_ = spec(10.0, 20.0, 0.0, 90.0, cp, 5.0, 4.9, min_width=1.6, max_width=3.0)
    b, tb, *_ = spec(10.0, 20.0, 0.0, 270.0, cp, 5.0, 4.9, min_width=1.6, max_width=3.0)
    a, _ = cap(a, ta, 10.0, 20.0); b, _ = cap(b, tb, 10.0, 20.0)
    ra, rb = math.hypot(a[0] - 10, a[1] - 20), math.hypot(b[0] - 10, b[1] - 20)
    assert abs(ra - rb) < 1e-6 and ra <= 1.0 + 1e-9 and a[2] == b[2]
    assert (a[0] - 10) * (b[0] - 10) + (a[1] - 20) * (b[1] - 20) < 0       # opposite sides of her

def test_retake_turn_validates_the_setting():
    t = _ns()["retake_turn"]
    assert t(None, True) == (0, None) and t("", True) == (0, None) and t("0", True) == (0, None)
    assert t("180", True) == (180, None) and t(" 540 ", True) == (180, None)
    for bad in ("abc", "90", "-45", "1.5"):
        n, note = t(bad, True); assert n == 0 and "normal retake side" in note   # a bad value falls back, never raises
    n, note = t("180", False); assert n == 0 and "ignored" in note              # only the retake turns

def test_no_extra_steps_back_and_the_render_is_wired():
    src = open(SRC).read()
    assert "GT_WIDEN" not in src and "widen_width" not in src                  # the 3.2-6 m / 6.4-8 m steps are gone
    assert 'retake_turn(os.environ.get("GT_RETAKE_TURN"), wide_)' in src
    assert "cap_camera_distance(tuple(loc), tuple(tgt), rx_, ry_)" in src
    i = src.index("if retake_cap[0] is not None:"); j = src.index("if hit: loc = tuple(Vector(hl) - u * 0.15)")
    assert j < i                                                                # the cap runs after the wall rule, so nothing moves it back out
