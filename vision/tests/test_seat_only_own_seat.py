"""2026-11-03 18:17 PT: on the Dutchie desk the seat check read 'hips 47.6 cm sunk INTO the seat' from David's office chair next to her.
The check now measures only the object she sits on (scene-mesher group "seat"); other furniture never counts, and no seat in frame = no gap."""
import ast, pathlib
import numpy as np

SRC = pathlib.Path(__file__).resolve().parents[1] / "cloud-render" / "render_mesh.py"
MESHER = pathlib.Path(__file__).resolve().parents[1] / "scene-mesher" / "Program.cs"

def _rm():
    tree = ast.parse(SRC.read_text())
    consts = {"SEAT_PLANE_BELOW", "SEAT_TOP_NEAR", "SEAT_RAY_START"}
    body = [n for n in tree.body if isinstance(n, ast.Assign) and any(getattr(t, "id", "") in consts for t in n.targets)] + \
           [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in {"seat_surface_z", "seat_top_under", "seat_top_hits"}]
    ns = {"np": np}; exec(compile(ast.Module(body, []), "render_mesh", "exec"), ns); return ns

def _cast(ns, surfaces):
    """surfaces = [(z, kind)]: a downward ray from z0 meets every surface at or below z0, top-down"""
    return lambda x, y, z0: ns["seat_surface_z"](sorted(((z, k) for z, k in surfaces if z <= z0), reverse=True))

def test_closer_chair_is_ignored_and_the_desk_is_measured():
    ns = _rm(); desk, chair, floor = 0.75, 0.80, 0.0   # another chair's seat lies CLOSER to her hips than the desk she sits on
    z = ns["seat_top_under"]([(0, 0, 0.76)] * 10, 0.76, _cast(ns, [(chair, "other"), (desk, "seat"), (floor, "other")]))
    assert z == desk

def test_no_seat_in_frame_means_not_measured():
    ns = _rm()
    assert ns["seat_top_under"]([(0, 0, 0.76)] * 10, 0.76, _cast(ns, [(0.80, "other"), (0.0, "other")])) is None

def test_her_own_body_is_passed_through():
    ns = _rm(); assert ns["seat_surface_z"]([(0.9, "me"), (0.8, "other"), (0.75, "seat")]) == 0.75

def test_mesher_groups_the_seated_linkset_and_render_tags_it():
    m = MESHER.read_text(); r = SRC.read_text()
    assert 'RootOf(o, byLocal) == seatRoot ? "seat"' in m and '["sitting_on"].AsUInteger()' in m
    assert 'build("seat")' in r and 'ob["gt_seat"] = 1' in r and "seat_surface_z(hits_())" in r
