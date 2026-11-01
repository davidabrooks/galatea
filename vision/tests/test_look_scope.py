"""look self / hover check render options and presets (2026-10-26): pure option parsing, the env they set, the scene.json filter and
the wiring into look.py / render_mesh.py / handler.py. No Blender, no SL."""
import ast, math, os, sys, pytest

HERE = os.path.dirname(os.path.abspath(__file__)); VIS = os.path.join(HERE, "..")
sys.path.insert(0, VIS)
import look_scope as L

def src(*p): return open(os.path.join(VIS, *p)).read()

def opts(*a): return L.parse_options(list(a))[1]

# ---- defaults unchanged ------------------------------------------------------------------------------------------------
def test_no_options_is_the_old_render():
    rest, o = L.parse_options(["self", "--views=left"])
    assert rest == ["self", "--views=left"] and L.is_default(o) and not L.needs_filter(o)
    assert L.env_for(o) == ({}, False)

def test_a_bare_word_is_never_an_option():
    rest, o = L.parse_options(["at", "small", "scope=none", "floor=off"])
    assert rest == ["at", "small", "scope=none", "floor=off"] and L.is_default(o)

# ---- each option ---------------------------------------------------------------------------------------------------------
@pytest.mark.parametrize("v,want", [("full", "full"), ("nearby", "nearby"), ("seat", "seat"), ("none", "none"), ("self-only", "none"), ("seat-only", "seat"), ("NONE", "none")])
def test_scope(v, want): assert opts(f"--scope={v}")["scope"] == want

@pytest.mark.parametrize("v", ["auto", "on", "off"])
def test_floor_values(v): assert opts(f"--floor={v}")["floor"] == v

def test_floor_off_sets_env_and_on_auto_do_not():
    assert L.env_for(opts("--floor=off"))[0] == {"GT_FLOOR": "off"}
    assert L.env_for(opts("--floor=on"))[0] == {} and L.env_for(opts("--floor=auto"))[0] == {}
    assert L.floor_on("auto") and L.floor_on("on") and not L.floor_on("off")

def test_textures_and_materials_off():
    assert L.env_for(opts("--textures=off"))[0] == {"GT_NO_TEXTURES": "1"}
    assert L.env_for(opts("--materials=off"))[0] == {"GT_TEX_MAPS": "0"}
    assert L.env_for(opts("--textures=on", "--materials=on"))[0] == {}

def test_small_scale_size_samples_jpeg_exposure():
    e, fast = L.env_for(opts("--small")); assert e == {"GT_RES_SCALE": "0.5", "GT_HC_SAMPLES": "4"} and fast
    e, fast = L.env_for(opts("--small", "--scale=0.25", "--samples=2")); assert e["GT_RES_SCALE"] == "0.25" and e["GT_HC_SAMPLES"] == "2"   # explicit wins over small
    assert L.env_for(opts("--size=320x400"))[0] == {"GT_RES": "320x400"}
    assert L.env_for(opts("--samples=16"))[1] is True
    assert L.env_for(opts("--jpeg=70"))[0] == {"GT_JPEG_QUALITY": "70"}
    assert L.env_for(opts("--exposure=-0.5"))[0] == {"GT_EXPOSURE": "-0.5"}
    assert L.env_for(opts("--scale=1"))[0] == {}

def test_camera_options():
    e, _ = L.env_for(opts("--cam-margin=1.5", "--cam-lens=35", "--cam-height=0.4"))
    assert e == {"GT_CAM_MARGIN": "1.5", "GT_CAM_LENS": "35", "GT_CAM_HEIGHT": "0.4"}
    fov = opts("--cam-fov=40")["cam_lens"]; assert abs(fov - 18 / math.tan(math.radians(20))) < 0.01   # fov across the long side -> lens
    assert opts("--cam-fov=40", "--cam-lens=60")["cam_lens"] == 60.0                                  # an explicit lens wins

def test_hide_attach_names_and_numbers():
    assert opts("--hide-attach=skull,chin")["hide_attach"] == (2, 12)
    assert opts("--hide-attach=2,Skull,40")["hide_attach"] == (2, 40)
    with pytest.raises(ValueError): L.parse_options(["--hide-attach=elbow"])

def test_avatars():
    assert opts("--avatars=off")["avatars"] == "off"
    with pytest.raises(ValueError): L.parse_options(["--avatars=on"])   # a self look never draws them: not offered

@pytest.mark.parametrize("bad", ["--scope=huge", "--floor=maybe", "--textures=2", "--scale=0", "--scale=5", "--size=10x10", "--size=abc", "--samples=0", "--jpeg=101",
                                 "--exposure=9", "--cam-margin=0.05", "--cam-lens=5", "--cam-fov=200", "--cam-height=9", "--preset=nope", "--scale=abc", "--exposure=nan"])
def test_bad_values_are_refused_with_a_message(bad):
    with pytest.raises(ValueError) as e: L.parse_options([bad])
    assert str(e.value)

# ---- presets -----------------------------------------------------------------------------------------------------------
def test_hover_ground_is_her_plus_floor_flat_and_fast():
    o = opts("--preset=hover-ground")
    assert (o["scope"], o["floor"], o["textures"], o["materials"], o["samples"]) == ("none", "on", "off", "off", 8)
    assert L.needs_filter(o) and L.env_for(o)[1] is True

def test_hover_seat_is_her_plus_seat_without_floor():
    o = opts("--preset=hover-seat")
    assert (o["scope"], o["floor"], o["views"]) == ("seat", "off", "left")
    assert L.env_for(o)[0]["GT_FLOOR"] == "off"

def test_photo_fast_is_todays_quick_full_scene():
    o = opts("--preset=photo-fast"); assert o["scope"] == "full" and o["floor"] == "auto" and not L.needs_filter(o) and L.env_for(o) == ({}, True)

def test_outfit_is_her_alone_with_textures_front_and_back():
    o = opts("--preset=outfit"); assert (o["scope"], o["floor"], o["textures"], o["views"]) == ("none", "off", "on", "front,back")

def test_explicit_options_override_a_preset():
    o = opts("--preset=hover-seat", "--floor=on", "--scope=none", "--textures=on")
    assert (o["scope"], o["floor"], o["textures"]) == ("none", "on", "on")

def test_photo_hd_preset_is_the_clients_photo_hd_not_looks():
    with pytest.raises(ValueError) as e: L.parse_options(["--preset=photo-hd"])
    assert "photo hd" in str(e.value)

# ---- scene filter ------------------------------------------------------------------------------------------------------
def _p(lid, par=0, pos=(0, 0, 0), scale=(1, 1, 1), **kw):
    b = lambda n: list(n.to_bytes(4, "big"))
    return {"localid": b(lid), "parentid": b(par), "world_pos": list(pos), "scale": list(scale), "pcode": 9, **kw}

def _doc(sitting=0):
    me = (10.0, 10.0, 30.0)
    prims = [
        _p(1, pos=(10, 10, 29.5), scale=(1, 1, 0.5)),                       # 1 seat root (a chair)
        _p(2, par=1, pos=(10, 10.3, 29.9), scale=(0.5, 0.1, 0.6)),          # 2 chair back (same linkset)
        _p(3, pos=(10, 10, 28.4), scale=(8, 8, 0.2)),                       # 3 floor slab under her
        _p(4, pos=(10.1, 10, 27.0), scale=(0.4, 0.4, 0.4)),                 # 4 a rug-sized thing below her
        _p(5, pos=(40, 40, 28.4), scale=(8, 8, 0.2)),                       # 5 floor elsewhere
        _p(6, pos=(10, 10, 36.0), scale=(8, 8, 0.2)),                       # 6 ceiling well above
        _p(7, pos=(12, 10, 30), scale=(0.3, 0.3, 0.3)),                     # 7 a lamp beside her
        _p(100, attached_to_me=True, attach_point=2), _p(101, attached_to_me=True, attach_point=40),   # her hair / body
        _p(200, attached_to="other-agent", attach_point=2),                 # someone else's hat
    ]
    return {"me": {"pos": list(me), "sitting_on": sitting}, "agent_id": "me-agent", "prims": prims,
            "avatars": [{"agent_id": "other-agent", "name": "X"}]}

def ids(d): return sorted(L.uid(p["localid"]) for p in d["prims"])

def test_full_and_nearby_keep_every_scene_prim():
    for sc in ("full", "nearby"):
        d, info = L.filter_scene(_doc(), opts(f"--scope={sc}")); assert ids(d) == ids(_doc()) and d["avatars"] == _doc()["avatars"] and info["dropped"] == 0

def test_none_keeps_her_items_and_only_the_floor_under_her():
    d, info = L.filter_scene(_doc(), opts("--scope=none"))
    assert ids(d) == [1, 2, 3, 4, 100, 101] and info["kept_floor"] == 4 and d["avatars"] == []   # everything a ray down from her could hit (the chair too: the full scene's cast hits it); not the lamp, ceiling, far floor, or the hat
    assert all(L.uid(p["parentid"]) == 0 for p in d["prims"] if L.uid(p["localid"]) in (1, 2, 3, 4))   # floor prims are detached (their linkset is gone)

def test_none_without_floor_is_only_her():
    d, info = L.filter_scene(_doc(), opts("--scope=none", "--floor=off")); assert ids(d) == [100, 101] and info["kept_floor"] == 0

def test_seat_keeps_the_whole_seat_linkset_not_the_rest():
    d, info = L.filter_scene(_doc(sitting=2), opts("--scope=seat", "--floor=off"))   # sitting on a child prim: its root's linkset
    assert ids(d) == [1, 2, 100, 101] and info["seat_found"] and info["kept_seat"] == 2
    d, info = L.filter_scene(_doc(sitting=2), opts("--scope=seat", "--floor=on"))
    assert ids(d) == [1, 2, 3, 4, 100, 101] and info["kept_floor"] == 2 and info["kept_seat"] == 2

def test_seat_scope_when_not_seated_is_just_her():
    d, info = L.filter_scene(_doc(sitting=0), opts("--scope=seat", "--floor=off")); assert ids(d) == [100, 101] and not info["seat_found"]

def test_the_floor_cast_only_needs_prims_it_can_hit():
    d, _ = L.filter_scene(_doc(), opts("--scope=none")); kept = ids(d)
    assert 5 not in kept and 6 not in kept and 7 not in kept   # elsewhere / above / beside

def test_hide_attach_drops_only_those_points():
    d, _ = L.filter_scene(_doc(), opts("--hide-attach=skull")); assert 100 not in ids(d) and 101 in ids(d) and 200 in ids(d) and 1 in ids(d)

def test_avatars_off_drops_other_avatars_and_their_items_only():
    d, _ = L.filter_scene(_doc(), opts("--avatars=off")); assert 200 not in ids(d) and 100 in ids(d) and 3 in ids(d) and d["avatars"] == []

def test_the_input_doc_is_not_modified():
    doc = _doc(); before = ids(doc); L.filter_scene(doc, opts("--scope=none")); assert ids(doc) == before

def test_uid_accepts_ints_and_byte_arrays():
    assert L.uid([10, 188, 225, 217]) == 0x0abce1d9 and L.uid(5) == 5 and L.uid(None) == 0

def test_nearby_mesher_args_use_a_6_m_focus():
    assert L.nearby_mesher_args("d", [1, 2, 3]) == ["d", "12", "all", "1,2,3", "6.0", "--roots=32"]

def test_the_automatic_hover_check_preset_follows_the_posture():
    assert L.hovercheck_preset({"me": {"sitting_on": 0}}) == "hover-ground" and L.hovercheck_preset({"me": {}}) == "hover-ground"
    assert L.hovercheck_preset({"me": {"sitting_on": [0, 0, 1, 2]}}) == "hover-seat"

# ---- wiring in the renderer ----------------------------------------------------------------------------------------------
def test_look_py_applies_the_options_and_defaults_the_hover_check_to_the_minimal_preset():
    l = src("look.py")
    assert "look_scope.parse_options(a)" in l and "look_scope.filter_scene(doc, opts)" in l and "look_scope.env_for(opts)" in l
    assert "look_scope.hovercheck_preset(doc)" in l and 'GT_HC_FULL' in l          # automatic hover / seat check: minimal preset, GT_HC_FULL=1 = old full scene
    assert 'if not scoped and not look_scope.is_default(opts): sys.exit(' in l      # photo hd / around / at / view reject the options
    assert 'look_scope.nearby_mesher_args(d, me)' in l
    assert "if not vw and opts[\"views\"]" in l

def test_render_mesh_reads_the_new_env_and_keeps_old_defaults():
    r = src("cloud-render", "render_mesh.py")
    assert 'FLOOR_ON = os.environ.get("GT_FLOOR") != "off"' in r and "if FLOOR_ON: self_floor_line(deg)" in r and "and FLOOR_ON and not sc.ray_cast" in r
    assert 'GT_RES_SCALE' in r and 'os.environ.get("GT_JPEG_QUALITY", "90")' in r
    assert 'GT_CAM_MARGIN' in r and 'GT_CAM_LENS' in r and 'GT_CAM_HEIGHT' in r
    assert "QUICK_EXPOSURE = -1.5" in r and 'return QUICK_EXPOSURE if (not hd and kind == "scene") else 0.0' in r   # the -1.5 stop quick exposure is untouched

def test_handler_skips_textures_only_on_request():
    h = src("cloud-render", "handler.py"); assert 'if os.environ.get("GT_NO_TEXTURES") == "1": want = {}' in h

def _camera_spec():
    s = src("cloud-render", "render_mesh.py"); ns = {"math": math}
    for n in ast.parse(s).body:
        if isinstance(n, ast.FunctionDef) and n.name in ("self_body_camera_spec", "self_body_lens"): exec(ast.get_source_segment(s, n), ns)
    return ns["self_body_camera_spec"], ns["self_body_lens"]

def test_camera_defaults_are_unchanged_and_options_move_the_camera():
    cam, lens = _camera_spec()
    pts = [(x, y, z) for x in (-.2, .2) for y in (-.2, .2) for z in (28.05, 29.95)]
    base = cam(0.0, 0.0, 0.0, 90.0, pts, 28.033)
    assert cam(0.0, 0.0, 0.0, 90.0, pts, 28.033, margin=1.08, lens=50.0, height=None) == base
    far = cam(0.0, 0.0, 0.0, 90.0, pts, 28.033, margin=2.0); assert far[2] > base[2]                  # margin: farther
    wide = cam(0.0, 0.0, 0.0, 90.0, pts, 28.033, lens=25.0); assert wide[2] < base[2]                 # wider lens: closer for the same framing
    low = cam(0.0, 0.0, 0.0, 90.0, pts, 28.033, height=0.4); assert abs(low[0][2] - 28.433) < 1e-9 and low[0][2] == low[1][2]   # height above the floor
    assert lens(2.0, 3.0, 35.0) == 35.0 and lens(2.0, 1.0, 35.0) == pytest.approx(17.5)

def test_a_filtered_render_keeps_the_original_export_for_the_next_render():
    l = src("look.py")
    assert 'full_json = f"{d}/scene.full.json"' in l and "shutil.copyfile(f\"{d}/scene.json\", full_json)" in l
    assert 'json.load(open(full_json if os.path.exists(full_json) else f"{d}/scene.json"))' in l


def test_hover_check_detail_line_reports_viewer_formula_gap_without_bias():
    """2026-10-26: the hover check prints its bias, whether David's viewer ever confirmed it, and the gap the viewer's own placement formula
    gives (render_mesh.py needs bpy, so the pure function is lifted out of the source)"""
    import ast, pathlib
    src = (pathlib.Path(__file__).resolve().parents[1] / "cloud-render" / "render_mesh.py").read_text()
    fn = next(n for n in ast.parse(src).body if isinstance(n, ast.FunctionDef) and n.name == "hover_check_detail")
    ns = {}; exec(compile(ast.Module([fn], []), "render_mesh", "exec"), ns)
    # agent z 26.2647, viewer offset -0.8896, hover -0.075, lowest vertex 0.0808 above mesh z 0, floor 25.290
    line = ns["hover_check_detail"]({"GT_HOVER_BIAS": "0.2315", "GT_HOVER_CAL": "calibrated"}, 26.2647, -0.8896, 0.0808, 25.290, -0.075)
    assert line.startswith("GT: hover check detail:") and "bias" not in line and "hover drawn -0.075" in line   # 2026-10-31: no render bias
    assert "lowest-vertex gap +0.091 m" in line
    assert "lowest-vertex gap n/a" in ns["hover_check_detail"]({}, 26.0, None, 0.0, 25.0, 0.0)


# ---- the contact close-up (2026-10-26, David: "zooming in closer helps judging the hover"; sits: frame the hips, not the feet) -----------------------
def _rm():
    """contact_points / feet_camera_spec lifted out of render_mesh.py (the file needs bpy)"""
    import ast, math, pathlib
    import numpy as np
    src = (pathlib.Path(__file__).resolve().parents[1] / "cloud-render" / "render_mesh.py").read_text()
    want = {"contact_points", "feet_camera_spec"}; tree = ast.parse(src)
    body = [n for n in tree.body if isinstance(n, ast.Assign) and any(getattr(t, "id", "") == "CONTACT_BAND" for t in n.targets)] + \
           [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in want]
    ns = {"np": np, "math": math}; exec(compile(ast.Module(body, []), "render_mesh", "exec"), ns)
    return ns

def _body(rng, n, x0, x1, z0, z1):
    import numpy as np
    return np.column_stack([rng.uniform(x0, x1, n), rng.uniform(-0.1, 0.1, n), rng.uniform(z0, z1, n)])

def test_contact_stand_frames_the_soles_not_the_body():
    import numpy as np
    ns = _rm(); rng = np.random.default_rng(1)
    verts = np.vstack([_body(rng, 400, -0.1, 0.2, 0.0, 0.10), _body(rng, 3000, -0.1, 0.1, 0.1, 1.5)])   # soles + ankles at the floor, the rest above
    zlow, cp = ns["contact_points"](verts)
    assert abs(zlow) < 0.01 and cp and max(p[2] for p in cp) <= zlow + 0.1 + 1e-9                         # only the lowest 10 cm: no knees, no head
    loc, tgt, d, width = ns["feet_camera_spec"](0.0, 0.0, 0.0, 90.0, cp, zlow, 0.0)
    assert abs(width - 0.6) < 1e-6                                                                         # about 0.6 m wide
    assert abs(tgt[2] - loc[2]) < 1e-9 and loc[2] < 0.25                                                   # level and low (frame starts 6 cm under the floor)
    assert abs(loc[0] - tgt[0]) < 1e-9 and loc[1] > tgt[1]                                                 # from her left (+Y at yaw 0)
    assert abs(tgt[0] - 0.05) < 0.1                                                                        # centred on the feet (x -0.1..0.2), not on her whole body

def test_contact_ground_sit_frames_the_hips_not_the_feet():
    import numpy as np
    ns = _rm(); rng = np.random.default_rng(2)
    hips = _body(rng, 800, -0.2, 0.2, 0.0, 0.10)          # butt on the floor
    thighs = _body(rng, 600, 0.2, 0.45, 0.05, 0.12)       # thighs along the floor
    feet = _body(rng, 300, 0.55, 0.65, 0.30, 0.45)        # V-sit: the feet are in the air
    torso = _body(rng, 2000, -0.2, 0.0, 0.12, 1.0)
    zlow, cp = ns["contact_points"](np.vstack([hips, thighs, feet, torso]))
    xs = [p[0] for p in cp]
    assert max(xs) < 0.5 and min(xs) < -0.15                                                                # hips and thighs yes, the airborne feet no
    loc, tgt, d, width = ns["feet_camera_spec"](0.0, 0.0, 0.0, 90.0, cp, zlow, 0.0)
    assert width > 0.6 and width <= 1.4                                                                    # wider: hips + thighs, not 0.6
    assert abs(tgt[0] - 0.12) < 0.15                                                                       # centred on the hips + thighs (x -0.2..0.45), nowhere near the feet (x 0.6)
    assert abs(tgt[0] - 0.6) > 0.3

def test_contact_kneel_frames_shins_and_knees_not_the_hips():
    import numpy as np
    ns = _rm(); rng = np.random.default_rng(3)
    shins = _body(rng, 700, 0.0, 0.4, 0.0, 0.09)           # shins / knees on the floor
    toes = _body(rng, 150, -0.3, -0.1, 0.03, 0.09)         # toes touching
    hips = _body(rng, 800, -0.1, 0.1, 0.30, 0.5); torso = _body(rng, 1500, -0.1, 0.1, 0.5, 1.2)
    zlow, cp = ns["contact_points"](np.vstack([shins, toes, hips, torso]))
    assert max(p[2] for p in cp) < 0.15                                                                    # no hips (z >= 0.30)
    xs = [p[0] for p in cp]; assert min(xs) < -0.2 and max(xs) > 0.3                                       # shins and toes both in the window
    loc, tgt, d, width = ns["feet_camera_spec"](0.0, 0.0, 0.0, 90.0, cp, zlow, 0.0)
    assert width >= 0.6 + 0.3 and loc[2] < 0.3

def test_contact_ignores_a_stray_low_vertex_and_follows_the_floor_when_she_hovers():
    import numpy as np
    ns = _rm(); rng = np.random.default_rng(4)
    verts = np.vstack([_body(rng, 3000, -0.1, 0.2, 0.0, 0.10), _body(rng, 3000, -0.1, 0.1, 0.1, 1.5), [[0.0, 0.0, -1.0]]])
    zlow, cp = ns["contact_points"](verts); assert zlow > -0.01                                           # the 0.2 % quantile, not the minimum
    # floating 5 cm above a floor at -0.05: the window starts under the FLOOR so the gap is in the picture
    loc, tgt, d, width = ns["feet_camera_spec"](0.0, 0.0, 0.0, 90.0, cp, zlow, -0.05)
    assert loc[2] - (width * 0.625 / 2) <= -0.05 - 0.05                                                    # frame bottom below the floor
    assert ns["contact_points"](np.zeros((0, 3))) == (None, [])
    assert len(ns["contact_points"](verts, cap=100)[1]) <= 100

def test_hovercheck_renders_one_view_the_close_up_instead_of_the_wide_side_view():
    assert L.hovercheck_view({}) == "feet" and ";" not in L.hovercheck_view({})                           # ONE view = one image = one render
    assert L.hovercheck_view({"GT_HC_ZOOM": "off"}) == "side"                                              # the old wide view is still available
    import pathlib
    lk = (pathlib.Path(__file__).resolve().parents[1] / "look.py").read_text()
    assert 'look_scope.hovercheck_view(os.environ)' in lk and '"GT_VIEW_OVERRIDE": "side"' not in lk

def test_zoom_option_parses_and_sets_the_env():
    assert opts("--zoom=feet")["zoom"] == "feet" and opts("--zoom=contact")["zoom"] == "feet" and opts("--zoom=off")["zoom"] == "off"
    assert L.env_for(opts("--zoom=feet"))[0]["GT_ZOOM"] == "feet" and "GT_ZOOM" not in L.env_for(opts())[0]
    with pytest.raises(ValueError): L.parse_options(["--zoom=macro"])
    assert L.is_default(opts("--zoom=off")) and not L.is_default(opts("--zoom=feet"))
    assert opts("--cam-margin=0.15")["cam_margin"] == 0.15                                                 # the 2026-10-26 11:57 call's value is valid now

def test_body_views_accepts_feet_and_its_aliases():
    import ast, pathlib
    src = (pathlib.Path(__file__).resolve().parents[1] / "look.py").read_text(); tree = ast.parse(src)   # look.py imports PIL: lift the pure function
    body = [n for n in tree.body if isinstance(n, ast.Assign) and any(getattr(t, "id", "") in ("SIDES", "VIEW_WORDS") for t in n.targets)] + \
           [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == "body_views"]
    ns = {}; exec(compile(ast.Module(body, []), "look", "exec"), ns); bv = ns["body_views"]
    assert bv("feet") == "feet" and bv("contact") == "feet" and bv("side,feet") == "left;feet" and bv("all") == "front;back;left;right"
    with pytest.raises(ValueError): bv("toes")
