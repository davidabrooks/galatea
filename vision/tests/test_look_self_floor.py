"""`look self <side>` draws her on the real floor (2026-10-26, David: "fix your renderer so it shows a floor when you need to see it"):
the full-body render is a scene render (GT_SELF_FLOOR) with the floor under her, a thin reference line at floor height and a low level
camera. Pure camera / line / view math from render_mesh.py + look.py's routing; no Blender, no SL."""
import ast, math, os, sys, types

HERE = os.path.dirname(os.path.abspath(__file__)); SRC = os.path.join(HERE, "..", "cloud-render", "render_mesh.py")

def _fns(*names):
    src = open(SRC).read(); ns = {"math": math}
    for n in ast.parse(src).body:
        if isinstance(n, ast.FunctionDef) and n.name in names or isinstance(n, ast.Assign) and any(getattr(t, "id", "") == "BODY_VIEW_DEG" for t in n.targets):
            exec(ast.get_source_segment(src, n), ns)
    return [ns[n] for n in names]

def test_floor_line_sits_at_floor_height_across_the_view():
    (spec,) = _fns("self_floor_line_spec")
    c, rot, (ln, dp, ht) = spec(10.0, 20.0, 15.0, 90.0, 28.033)
    assert c[2] - ht / 2 == 28.033 + 0 and abs(c[2] - 28.033 - ht / 2) < 1e-9   # the box stands on the floor: its base IS the floor
    assert ht <= 0.01 and dp <= 0.02 and ln >= 1.0                              # thin, long enough to span her
    assert rot == 15.0 + 90.0 + 90.0                                            # across the camera direction (her left view = yaw + 90)
    assert (c[0], c[1]) == (10.0, 20.0)

def test_camera_is_level_low_and_frames_the_feet_standing_and_sitting():
    cam, = _fns("self_body_camera_spec")
    stand = [(x, y, z) for x in (-0.2, 0.2) for y in (-0.2, 0.2) for z in (28.05, 29.95)]       # 1.9 m tall, soles 1.7 cm above the floor
    loc, tgt, d, res = cam(0.0, 0.0, 0.0, 90.0, stand, 28.033)
    assert loc[2] == tgt[2] and 28.033 + 0.5 < loc[2] < 28.033 + 1.2           # level, about hip height above the floor
    assert res == (640, 1024) and d > 1.8
    # a ground sit is wider than tall: landscape frame, still level and low
    sit = [(x, y, z) for x in (-0.6, 0.8) for y in (-0.8, 0.6) for z in (28.0, 28.96)]
    loc2, tgt2, d2, res2 = cam(0.0, 0.0, 0.0, 90.0, sit, 28.033)
    assert res2 == (1024, 640) and loc2[2] == tgt2[2] and loc2[2] < 28.033 + 0.8
    # the camera really is on the asked side: left = +90 deg from her front (+Y at yaw 0)
    assert loc[1] > 0 and abs(loc[0]) < 1e-6
    back, = [cam(0.0, 0.0, 0.0, 180.0, stand, 28.033)[0]]
    assert back[0] < 0

def test_wall_behind_the_camera_widens_the_lens_instead_of_cropping():
    (lens,) = _fns("self_body_lens")
    assert lens(3.0, 3.5) == 50.0
    assert lens(4.0, 2.0) == 25.0
    assert lens(4.0, 0.1) == 14.0                                               # floor

def test_views_map_to_labelled_files_and_default_front():
    f, = _fns("self_floor_views")
    # body_view_deg / body_view_label come from the same module
    ns = {}; src = open(SRC).read()
    for n in ast.parse(src).body:
        if isinstance(n, ast.Assign) and any(getattr(t, "id", "") == "BODY_VIEW_DEG" for t in n.targets) or isinstance(n, ast.FunctionDef) and n.name in ("body_view_deg", "body_view_label", "self_floor_views"):
            exec(ast.get_source_segment(src, n), ns)
    v = ns["self_floor_views"]
    assert v("") == ["body:0:"]
    assert v("left;back;45") == ["body:90:left", "body:180:back", "body:45:45deg"]

def test_self_floor_is_wired_into_the_scene_render_and_look_py():
    src = open(SRC).read()
    assert 'SELF_FLOOR = kind == "body" and os.environ.get("GT_SELF_FLOOR") == "1"' in src
    assert "if SCENE:" in src and "self_floor_line(deg)" in src
    assert 'view_exposure(os.environ, HD, "scene" if SCENE else kind)' in src            # scene lighting, not the studio rig
    look = open(os.path.join(HERE, "..", "look.py")).read()
    assert 'self_floor = mode == "self" and not hd' in look and 'os.environ["GT_SELF_FLOOR"] = "1"' in look
    assert 'mmode = "view" if photo_self or self_floor else mode' in look               # the scene mesher, not avatar-only
    # other modes unchanged: around / at / photo hd and the avatar-only mesher call for plain `self` keep working
    sys.modules.setdefault("handler", types.ModuleType("handler")); sys.modules.setdefault("make_job", types.ModuleType("make_job"))
    sys.path.insert(0, os.path.join(HERE, ".."))
    import look as L
    assert L.mesher_args("d", "self", [0, 0, 0], False) == ["d", "12", "avatar"]
    assert L.mesher_args("d", "around", [1, 2, 3], False) == ["d", "12", "all", "1,2,3", "30", "--roots=32"]
