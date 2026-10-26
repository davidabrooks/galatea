"""The contact close-up when she sits ON FURNITURE (2026-10-26, David: "when you're sitting, you should be checking your hips, not your feet").
#313 framed the lowest body contact over the terrain floor: on a chair that was her dangling foot above the grass. Now: hips against the seat.
Synthetic geometry only (render_mesh.py needs bpy, so the pure functions are lifted out of it)."""
import ast, math, pathlib
import numpy as np
import pytest

SRC = pathlib.Path(__file__).resolve().parents[1] / "cloud-render" / "render_mesh.py"

def _rm():
    src = SRC.read_text(); tree = ast.parse(src)
    consts = {"CONTACT_BAND", "SEAT_HIPS_R", "SEAT_PLANE_BELOW", "SEAT_PLANE_ABOVE"}
    body = [n for n in tree.body if isinstance(n, ast.Assign) and any(getattr(t, "id", "") in consts for t in n.targets)] + \
           [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in {"contact_points", "feet_camera_spec", "seat_contact"}]
    ns = {"np": np, "math": math}; exec(compile(ast.Module(body, []), "render_mesh", "exec"), ns)
    return ns

def _box(rng, n, x0, x1, z0, z1, y=0.15):
    return np.column_stack([rng.uniform(x0, x1, n), rng.uniform(-y, y, n), rng.uniform(z0, z1, n)])

SEAT_Z, PELVIS = 0.45, (0.0, 0.0, 0.58)    # a chair: seat top 0.45 m over grass at z 0; her pelvis bone 13 cm over it

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
    zlow, cp, seat_z, gap = ns["seat_contact"](v, PELVIS, SEAT_Z)
    assert abs(zlow - SEAT_Z) < 0.01 and seat_z == SEAT_Z and abs(gap) < 0.01          # the hips' lowest point is on the seat top
    assert cp and max(p[0] for p in cp) < 0.3 and min(p[2] for p in cp) > SEAT_Z - 0.01 # hips only: no shins (x 0.5+), no feet (z 0.08)
    loc, tgt, d, width = ns["feet_camera_spec"](0.0, 0.0, 0.0, 90.0, cp, zlow, seat_z, min_width=0.8)
    assert width >= 0.8 and abs(tgt[0]) < 0.15 and abs(tgt[0] - 0.6) > 0.3             # centred on the hips (x 0), nowhere near the feet (x 0.6)
    assert tgt[2] > SEAT_Z - 0.06 and loc[2] > SEAT_Z - 0.06                           # camera level, at the seat: the frame starts 6 cm under the seat, not under the grass
    assert loc[1] > tgt[1] and abs(loc[2] - tgt[2]) < 1e-9                             # from her left, level

def test_gap_is_hips_over_the_seat_positive_floating_negative_sunk():
    ns = _rm(); rng = np.random.default_rng(11)
    for above in (0.02, 0.04, -0.03):
        zlow, cp, seat_z, gap = ns["seat_contact"](_sitter(rng, above), PELVIS, SEAT_Z)
        assert abs(gap - above) < 0.01 and seat_z == SEAT_Z
    # a floating sit: the frame still reaches down to the seat so the gap is in the picture
    zlow, cp, seat_z, gap = ns["seat_contact"](_sitter(rng, 0.05), PELVIS, SEAT_Z)
    loc, tgt, d, width = ns["feet_camera_spec"](0.0, 0.0, 0.0, 90.0, cp, zlow, seat_z, min_width=0.8)
    assert loc[2] - width * 0.625 / 2 <= SEAT_Z - 0.05

def test_no_seat_surface_falls_back_to_the_hips_bounding_area_not_the_feet():
    ns = _rm(); rng = np.random.default_rng(12); v = _sitter(rng)
    for bad in (None, 0.0, 1.5):                      # not found / the grass 45 cm down / a ceiling: not her seat
        zlow, cp, seat_z, gap = ns["seat_contact"](v, PELVIS, bad)
        assert seat_z is None and gap is None
        assert abs(zlow - SEAT_Z) < 0.01 and max(p[0] for p in cp) < 0.3               # still the hips, never the feet
        loc, tgt, d, width = ns["feet_camera_spec"](0.0, 0.0, 0.0, 90.0, cp, zlow, zlow, min_width=0.8)
        assert abs(tgt[0]) < 0.15 and loc[2] > SEAT_Z - 0.06

def test_seat_contact_ignores_a_stray_vertex_and_an_empty_body():
    ns = _rm(); rng = np.random.default_rng(13)
    v = np.vstack([_sitter(rng), [[0.0, 0.0, 0.1]]])                                  # one stray vertex under her hips
    zlow, cp, seat_z, gap = ns["seat_contact"](v, PELVIS, SEAT_Z); assert abs(zlow - SEAT_Z) < 0.02
    assert ns["seat_contact"](np.zeros((0, 3)), PELVIS, SEAT_Z) is None
    far = _box(rng, 200, 5.0, 5.2, 0.0, 1.0)                                          # nothing near the pelvis
    assert ns["seat_contact"](far, PELVIS, SEAT_Z) is None

def test_hips_window_excludes_legs_that_hang_under_the_pelvis_height():
    ns = _rm(); rng = np.random.default_rng(14)
    legs = _box(rng, 300, 0.0, 0.1, 0.05, 0.27)         # a leg straight down at the pelvis xy: below the window (pelvis - 0.30 = 0.28)
    zlow, cp, seat_z, gap = ns["seat_contact"](np.vstack([_sitter(rng), legs]), PELVIS, SEAT_Z)
    assert abs(zlow - SEAT_Z) < 0.02 and seat_z == SEAT_Z

def test_ground_sit_and_stand_framing_are_unchanged_by_the_seat_work():
    ns = _rm(); rng = np.random.default_rng(15)
    stand = np.vstack([_box(rng, 400, -0.1, 0.2, 0.0, 0.10), _box(rng, 3000, -0.1, 0.1, 0.1, 1.5)])
    zlow, cp = ns["contact_points"](stand); loc, tgt, d, width = ns["feet_camera_spec"](0.0, 0.0, 0.0, 90.0, cp, zlow, 0.0)
    assert abs(width - 0.6) < 1e-6 and loc[2] < 0.25 and abs(tgt[0] - 0.05) < 0.1     # same numbers as test_look_scope.py: min_width default stays 0.6

def test_the_renderer_uses_the_seat_branch_only_when_seated_on_an_object():
    src = SRC.read_text()
    assert 'prim_under = sc.ray_cast(dg, Vector((x, y, g[2] + 0.5)), Vector((0, 0, -1)))[0]' in src
    assert 'sc_ = seat_contact(allv, (pel.x, pel.y, pel.z), floor if prim_under else None)' in src and "        if mst:" in src   # only a seated avatar; terrain is never the seat plane
    assert 'if sc_:' in src and 'zlow, cp = contact_points(allv)' in src                                                 # stand / ground sit / kneel: the #313 path
    assert 'elif SELF_FLOOR and mst: print("GT: self floor: seated on an object' in src                                   # no hover model on furniture
    look = (pathlib.Path(__file__).resolve().parents[1] / "look.py").read_text()
    assert '"GT_HOVER_CHECK": "0" if os.environ.get("GT_SEAT_CHECK") == "1" else "1"' in look and '"GT: seat contact"' in look
