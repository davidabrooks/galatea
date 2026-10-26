"""The contact close-up when she sits ON FURNITURE (2026-10-26, David: "when you're sitting, you should be checking your hips, not your feet").
#313 framed the lowest body contact over the terrain floor: on a chair that was her dangling foot above the grass. Now: hips against the seat.
Synthetic geometry only (render_mesh.py needs bpy, so the pure functions are lifted out of it)."""
import ast, math, pathlib
import numpy as np
import pytest

SRC = pathlib.Path(__file__).resolve().parents[1] / "cloud-render" / "render_mesh.py"

def _rm():
    src = SRC.read_text(); tree = ast.parse(src)
    consts = {"CONTACT_BAND", "SIT_HIPS_R", "SEAT_HIPS_R", "SEAT_PLANE_BELOW", "SEAT_TOP_NEAR", "SEAT_RAY_START"}
    body = [n for n in tree.body if isinstance(n, ast.Assign) and any(getattr(t, "id", "") in consts for t in n.targets)] + \
           [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in {"contact_points", "feet_camera_spec", "seat_contact", "seat_hips", "seat_top_under", "seat_cutaway", "cutaway_mask", "ground_sit_contact"}]
    ns = {"np": np, "math": math}; exec(compile(ast.Module(body, []), "render_mesh", "exec"), ns)
    return ns

def _box(rng, n, x0, x1, z0, z1, y=0.15):
    return np.column_stack([rng.uniform(x0, x1, n), rng.uniform(-y, y, n), rng.uniform(z0, z1, n)])

SEAT_Z, PELVIS = 0.45, (0.0, 0.0, 0.58)
flat = lambda z: (lambda x, y, zs: z if z is not None and z <= zs else None)   # a solid flat seat at z: the ray starts above it    # a chair: seat top 0.45 m over grass at z 0; her pelvis bone 13 cm over it

def _sitter(rng, hip_above_seat=0.0):
    """hips on the seat (lowest hip vertex = seat + hip_above_seat), thighs forward on the cushion, shins dangling, feet 0.08 m over the GRASS, torso up"""
    z = SEAT_Z + hip_above_seat
    hips = _box(rng, 900, -0.2, 0.2, z, z + 0.25); thighs = _box(rng, 600, 0.2, 0.5, z + 0.01, z + 0.15)
    shins = _box(rng, 500, 0.5, 0.58, 0.20, 0.44); feet = _box(rng, 300, 0.5, 0.7, 0.08, 0.2); torso = _box(rng, 2000, -0.2, 0.0, z + 0.25, 1.3)
    return np.vstack([hips, thighs, shins, feet, torso])

def test_furniture_sit_frames_the_hips_on_the_seat_not_the_dangling_feet():
    ns = _rm(); rng = np.random.default_rng(10); v = _sitter(rng)
    zlow_feet, cp_feet = ns["contact_points"](v)                          # what #313 framed: the feet, 8 cm over the grass
    assert zlow_feet < 0.1 and max(p[0] for p in cp_feet) > 0.45
    zlow, cp, seat_z, gap = ns["seat_contact"](v, PELVIS, flat(SEAT_Z))
    assert abs(zlow - SEAT_Z) < 0.01 and seat_z == SEAT_Z and abs(gap) < 0.01          # the hips' lowest point is on the seat top
    assert cp and max(p[0] for p in cp) < 0.3 and min(p[2] for p in cp) > SEAT_Z - 0.01 # hips only: no shins (x 0.5+), no feet (z 0.08)
    loc, tgt, d, width = ns["feet_camera_spec"](0.0, 0.0, 0.0, 90.0, cp, zlow, seat_z, min_width=0.8)
    assert width >= 0.8 and abs(tgt[0]) < 0.15 and abs(tgt[0] - 0.6) > 0.3             # centred on the hips (x 0), nowhere near the feet (x 0.6)
    assert tgt[2] > SEAT_Z - 0.06 and loc[2] > SEAT_Z - 0.06                           # camera level, at the seat: the frame starts 6 cm under the seat, not under the grass
    assert loc[1] > tgt[1] and abs(loc[2] - tgt[2]) < 1e-9                             # from her left, level

def test_gap_is_hips_over_the_seat_positive_floating_negative_sunk():
    ns = _rm(); rng = np.random.default_rng(11)
    for above in (0.02, 0.04, -0.03):
        zlow, cp, seat_z, gap = ns["seat_contact"](_sitter(rng, above), PELVIS, flat(SEAT_Z))
        assert abs(gap - above) < 0.01 and seat_z == SEAT_Z
    # a floating sit: the frame still reaches down to the seat so the gap is in the picture
    zlow, cp, seat_z, gap = ns["seat_contact"](_sitter(rng, 0.05), PELVIS, flat(SEAT_Z))
    loc, tgt, d, width = ns["feet_camera_spec"](0.0, 0.0, 0.0, 90.0, cp, zlow, seat_z, min_width=0.8)
    assert loc[2] - width * 0.625 / 2 <= SEAT_Z - 0.05

def test_no_seat_surface_falls_back_to_the_hips_bounding_area_not_the_feet():
    ns = _rm(); rng = np.random.default_rng(12); v = _sitter(rng)
    for bad in (None, flat(0.0), flat(None), lambda x, y, z: 1.5):   # no cast / only the grass 45 cm down / no hit / a ceiling: none is her seat
        zlow, cp, seat_z, gap = ns["seat_contact"](v, PELVIS, bad)
        assert seat_z is None and gap is None
        assert abs(zlow - SEAT_Z) < 0.01 and max(p[0] for p in cp) < 0.3               # still the hips, never the feet
        loc, tgt, d, width = ns["feet_camera_spec"](0.0, 0.0, 0.0, 90.0, cp, zlow, zlow, min_width=0.8)
        assert abs(tgt[0]) < 0.15 and loc[2] > SEAT_Z - 0.06

def test_seat_contact_ignores_a_stray_vertex_and_an_empty_body():
    ns = _rm(); rng = np.random.default_rng(13)
    v = np.vstack([_sitter(rng), [[0.0, 0.0, 0.1]]])                                  # one stray vertex under her hips
    zlow, cp, seat_z, gap = ns["seat_contact"](v, PELVIS, flat(SEAT_Z)); assert abs(zlow - SEAT_Z) < 0.02
    assert ns["seat_contact"](np.zeros((0, 3)), PELVIS, flat(SEAT_Z)) is None
    far = _box(rng, 200, 5.0, 5.2, 0.0, 1.0)                                          # nothing near the pelvis
    assert ns["seat_contact"](far, PELVIS, flat(SEAT_Z)) is None

def test_hips_window_excludes_legs_that_hang_under_the_pelvis_height():
    ns = _rm(); rng = np.random.default_rng(14)
    legs = _box(rng, 300, 0.0, 0.1, 0.05, 0.27)         # a leg straight down at the pelvis xy: below the window (pelvis - 0.30 = 0.28)
    zlow, cp, seat_z, gap = ns["seat_contact"](np.vstack([_sitter(rng), legs]), PELVIS, flat(SEAT_Z))
    assert abs(zlow - SEAT_Z) < 0.02 and seat_z == SEAT_Z

def test_ground_sit_and_stand_framing_are_unchanged_by_the_seat_work():
    ns = _rm(); rng = np.random.default_rng(15)
    stand = np.vstack([_box(rng, 400, -0.1, 0.2, 0.0, 0.10), _box(rng, 3000, -0.1, 0.1, 0.1, 1.5)])
    zlow, cp = ns["contact_points"](stand); loc, tgt, d, width = ns["feet_camera_spec"](0.0, 0.0, 0.0, 90.0, cp, zlow, 0.0)
    assert abs(width - 0.6) < 1e-6 and loc[2] < 0.25 and abs(tgt[0] - 0.05) < 0.1     # same numbers as test_look_scope.py: min_width default stays 0.6

def test_the_renderer_uses_the_seat_branch_only_when_seated_on_an_object():
    src = SRC.read_text()
    assert 'prim_under = sc.ray_cast(dg, Vector((x, y, g[2] + 0.5)), Vector((0, 0, -1)))[0]' in src
    assert 'sc_ = seat_contact(allv, (pel.x, pel.y, pel.z), seat_cast if prim_under else None)' in src and "        if mst:" in src   # only a seated avatar; terrain is never the seat plane
    assert 'if sc_:' in src and 'zlow, cp = gs_ if gs_ else contact_points(allv)' in src                                                 # stand / ground sit / kneel: the #313 path
    assert 'elif SELF_FLOOR and mst: print("GT: self floor: seated on an object' in src                                   # no hover model on furniture
    look = (pathlib.Path(__file__).resolve().parents[1] / "look.py").read_text()
    assert '"GT_HOVER_CHECK": "0" if os.environ.get("GT_SEAT_CHECK") == "1" else "1"' in look and '"GT: seat contact"' in look


# ---- #314 live test, Nutmeg Porch Rocking Chair (slatted, rocking, tilted ~2 deg): the hips were hidden by the near frame, and the gap came from ONE ray -------------
def _slatted(x, y, zs, top=0.45, pitch=0.12, slat_w=0.07, thick=0.035, slope=0.15, rail=0.0):
    """first surface under (x, y) from zs down on a slatted seat: slats of width slat_w every `pitch` m along y, top face z = top + slope * x (it rises toward the
    front like the real seat), a closed slat has a top and a bottom face (thick under it); rays between the slats fall through to a rail far below (`rail`)"""
    k = (y % pitch) / pitch
    if k < slat_w / pitch:
        t = top + slope * x
        return t if t <= zs else (t - thick if t - thick <= zs else None)   # a ray that starts inside the slat meets its underside
    return rail if rail <= zs else None

def test_slatted_seat_top_is_the_median_over_the_butt_patch_not_one_ray():
    ns = _rm(); rng = np.random.default_rng(20); v = _sitter(rng)
    cast = lambda x, y, zs: _slatted(x, y, zs)
    zlow, cp, seat_z, gap = ns["seat_contact"](v, PELVIS, cast)
    assert seat_z is not None and abs(seat_z - (0.45 + 0.15 * float(np.median([p[0] for p in cp if p[2] <= zlow + 0.03])))) < 0.02   # the slat tops under the patch
    assert seat_z > 0.4 and abs(gap - (zlow - seat_z)) < 1e-12                       # never the rail (z 0) the gaps let a ray through to
    # one ray through a gap would have found the rail 45 cm down: the seat is "not found" by that ray alone, but the patch has slat hits
    assert cast(0.0, 0.1 - 0.001, 0.55) == 0.0
    assert ns["seat_top_under"]([(0.0, 0.1 - 0.001, 0.45)], 0.45, cast) is None      # fewer than 3 hits: not found, never a rail

def test_ray_starts_over_the_hips_so_a_sunk_in_butt_still_finds_the_slat_top_not_its_underside():
    ns = _rm(); patch = [(x, y, 0.40) for x in np.linspace(-0.1, 0.1, 9) for y in np.linspace(0.0, 0.04, 5)]   # inside the slat band (y 0..0.07)
    cast = lambda x, y, zs: _slatted(x, y, zs, top=0.45, slope=0.0)
    # her lowest vertex is 5 cm under the slat top (z 0.40): the rays start at 0.50, above the top face
    assert abs(ns["seat_top_under"](patch, 0.40, cast) - 0.45) < 1e-9

def test_seat_rail_far_below_is_not_a_seat():
    ns = _rm(); patch = [(x, 0.0, 0.45) for x in np.linspace(-0.1, 0.1, 9)]
    assert ns["seat_top_under"](patch, 0.45, lambda x, y, zs: 0.0) is None            # 45 cm down: beyond SEAT_PLANE_BELOW
    assert ns["seat_top_under"](patch, 0.45, lambda x, y, zs: 0.08) is None
    assert abs(ns["seat_top_under"](patch, 0.45, lambda x, y, zs: 0.30) - 0.30) < 1e-9  # 15 cm down: the wider window (a deep gap, 15 cm) still counts when nothing is nearer
    mixed = lambda x, y, zs: 0.44 if x < 0.05 else 0.30                                 # most rays hit the slab 1 cm under, some a rail 15 cm under
    assert abs(ns["seat_top_under"](patch, 0.45, mixed) - 0.44) < 1e-9

def _frame(ns, tgt=(0.0, 0.0, 0.45), loc=(0.0, 1.4, 0.45)):
    hips = [(x, y, 0.45) for x in np.linspace(-0.2, 0.2, 5) for y in np.linspace(-0.2, 0.2, 5)]
    return ns["seat_cutaway"](tgt, loc, hips, 0.8, 0.39)

def test_cutaway_removes_the_near_frame_between_the_camera_and_the_hips_and_keeps_the_seat():
    ns = _rm(); box = _frame(ns)                                   # camera on her left (+y), looking along -y; hips y in -0.2..0.2
    pts = np.array([
        [0.0, 0.7, 0.55],     # near side frame / arm, 0.7 m toward the camera                -> cut
        [0.1, 0.5, 0.30],     # rocker runner (low, in front of the frame bottom? z 0.30 is under z_lo 0.37) -> kept
        [0.1, 0.5, 0.45],     # runner / rail at seat height                                 -> cut
        [0.0, 0.1, 0.45],     # the slat she sits on (under her hips)                        -> kept
        [0.0, 0.23, 0.45],    # the seat just beside her hips (within the 5 cm margin)       -> kept
        [0.0, -0.5, 0.55],    # the far frame behind her                                     -> kept
        [0.9, 0.7, 0.55],     # a post way off to the side of the corridor (|l| > half)      -> kept
        [0.0, 0.7, 1.40],     # the arm rest top far above the frame                         -> kept
        [0.0, 1.9, 0.55],     # behind the camera                                            -> kept
    ])
    m = ns["cutaway_mask"](pts, box)
    assert m.tolist() == [True, False, True, False, False, False, False, False, False]
    assert box["s_min"] > 0.2 and box["s_max"] > 1.4 and box["f"] == (0.0, 1.0)

def test_cutaway_follows_the_camera_direction():
    ns = _rm(); box = _frame(ns, loc=(1.4, 0.0, 0.45))          # camera in front of her (+x)
    m = ns["cutaway_mask"](np.array([[0.7, 0.0, 0.55], [0.0, 0.7, 0.55], [-0.7, 0.0, 0.55]]), box)
    assert m.tolist() == [True, False, False]

def test_the_renderer_cuts_the_corridor_before_the_wall_check_and_never_touches_her_body():
    src = SRC.read_text()
    assert "cut_corridor(seat_cutaway(tgt, loc, cp, width," in src and src.index("cut_corridor(seat_cutaway(") < src.index("# a wall behind the camera: stop in front of it")
    cc = src[src.index("    def cut_corridor(box):"):src.index("    def contact_closeup(")]
    assert "ob_ in obs" in cc and '"floor-line"' in cc and "bmesh.ops.delete" in cc and "me_.copy()" in cc   # her own meshes and the floor line stay; shared mesh data is copied before editing
    assert "def seat_cast(" in src and "id(ob_) not in mine" in src                                         # the seat rays skip her body


# ---- the AO ground sit (2026-10-26 live test): leaning back, legs forward, hands propped behind, heels on the floor: the close-up framed head to heels ------------
def _leaning_sit(rng):
    """z 0 = the floor. butt + thighs on the floor, lower back rising behind, extended legs forward with the HEELS on the floor 0.95 m away, hands propped on the floor
    0.45 m behind, torso leaning back, head high. pelvis bone at (0, 0, 0.12)"""
    butt = _box(rng, 900, -0.2, 0.2, 0.0, 0.12); thighs = _box(rng, 700, 0.2, 0.55, 0.03, 0.13); lower_back = _box(rng, 500, -0.35, -0.15, 0.05, 0.35)
    calves = _box(rng, 600, 0.55, 0.9, 0.06, 0.30); heels = _box(rng, 300, 0.88, 1.0, 0.0, 0.08, y=0.08)
    hands = np.vstack([_box(rng, 150, -0.5, -0.42, 0.0, 0.06, y=0.04) + [0, s_, 0] for s_ in (-0.3, 0.3)]); arms = _box(rng, 500, -0.55, -0.3, 0.06, 0.6, y=0.3)
    torso = _box(rng, 2000, -0.7, -0.3, 0.35, 0.8); head = _box(rng, 800, -0.85, -0.65, 0.85, 1.05)
    return np.vstack([butt, thighs, lower_back, calves, heels, hands, arms, torso, head])
GS_PELVIS = (0.0, 0.0, 0.12)

def test_whole_body_contact_of_a_leaning_back_sit_spans_heels_to_hands_the_bug():
    ns = _rm(); rng = np.random.default_rng(30); v = _leaning_sit(rng)
    zlow, cp = ns["contact_points"](v)
    xs = [p[0] for p in cp]; assert max(xs) > 0.85 and min(xs) < -0.4                # heels (x 0.9+) and hands (x -0.5) both in the lowest 10 cm
    loc, tgt, d, width = ns["feet_camera_spec"](0.0, 0.0, 0.0, 90.0, cp, zlow, 0.0)
    assert width == 1.4                                                              # capped: head to heels in the frame

def test_ground_sit_close_up_is_tight_on_the_hips_to_floor_contact():
    ns = _rm(); rng = np.random.default_rng(31); v = _leaning_sit(rng)
    zlow, cp = ns["ground_sit_contact"](v, GS_PELVIS)
    xs = [p[0] for p in cp]
    assert max(xs) < 0.6 and min(xs) > -0.45                                         # no heels (x 0.9), no hands (x -0.5), no head
    assert max(p[2] for p in cp) <= zlow + 0.1 + 1e-9 and abs(zlow) < 0.02          # the lowest 10 cm around the butt, on the floor
    assert max(xs) - min(xs) < 0.65                                                  # the patch itself is the butt + the backs of the thighs, not a leg's length
    loc, tgt, d, width = ns["feet_camera_spec"](0.0, 0.0, 0.0, 90.0, cp, zlow, 0.0, max_width=0.9)
    assert 0.6 <= width <= 0.9                                                       # about 0.6-0.8 m wide, not 1.4
    assert -0.1 < tgt[0] < 0.3 and abs(tgt[0] - 0.9) > 0.5                           # centred on the butt / thighs, nowhere near the heels
    assert loc[2] < 0.3 and abs(loc[2] - tgt[2]) < 1e-9 and loc[1] > tgt[1]         # level, low, from her left; the floor line is in the frame (starts 6 cm under it)
    assert loc[2] - width * 0.625 / 2 <= -0.05

def test_ground_sit_contact_follows_the_pelvis_not_the_world_origin():
    ns = _rm(); rng = np.random.default_rng(32); v = _leaning_sit(rng) + [5.0, -3.0, 20.0]
    zlow, cp = ns["ground_sit_contact"](v, (5.0, -3.0, 20.12))
    assert abs(zlow - 20.0) < 0.02 and max(p[0] for p in cp) < 5.6 and min(p[0] for p in cp) > 4.55

def test_ground_sit_contact_falls_back_when_the_pelvis_has_no_vertices_near():
    ns = _rm(); rng = np.random.default_rng(33)
    assert ns["ground_sit_contact"](_leaning_sit(rng), (50.0, 50.0, 0.1)) is None       # the caller keeps the whole-body contact
    assert ns["ground_sit_contact"](np.zeros((0, 3)), GS_PELVIS) is None

def test_stand_and_seat_framing_are_unchanged_by_the_ground_sit_fix():
    src = SRC.read_text()
    # only the AO ground sit (the client sends GT_HC_KIND=sit) uses the pelvis window; stand / kneel / unknown keep contact_points(allv); furniture keeps seat_contact
    assert 'os.environ.get("GT_HC_KIND") == "sit" and M.get("pelvis")' in src and "zlow, cp = gs_ if gs_ else contact_points(allv)" in src
    assert src.index("if mst:") < src.index('os.environ.get("GT_HC_KIND") == "sit"')            # the furniture branch comes first
    ns = _rm(); rng = np.random.default_rng(34)
    stand = np.vstack([_box(rng, 400, -0.1, 0.2, 0.0, 0.10), _box(rng, 3000, -0.1, 0.1, 0.1, 1.5)])
    zlow, cp = ns["contact_points"](stand); loc, tgt, d, width = ns["feet_camera_spec"](0.0, 0.0, 0.0, 90.0, cp, zlow, 0.0)
    assert abs(width - 0.6) < 1e-6                                                             # soles: 0.6 m as in #313
