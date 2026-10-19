"""2026-10-18 spotlights: lights follow the viewer's own deferred-light rules (size = radius x 1.5, falloff x 0.5, shadows only on
point-free / <= 2 nearest projectors) instead of the raw prim values with every light shadowed by its own fixture."""
import ast, os

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = open(os.path.join(HERE, "cloud-render", "render_mesh.py")).read()


def _ns():
    """exec just the constants + light_shadow_ids out of render_mesh.py (the module itself needs bpy)"""
    tree = ast.parse(SRC); keep = []
    for n in tree.body:
        if isinstance(n, ast.FunctionDef) and n.name == "light_shadow_ids": keep.append(n)
        elif isinstance(n, ast.Assign) and any(isinstance(t, ast.Name) and t.id in ("SPOT_SHADOWS", "LIGHT_RADIUS_SCALE", "LIGHT_FALLOFF_FUDGE") for t in n.targets): keep.append(n)
    ns = {"os": os}; exec(compile(ast.Module(keep, []), "rm", "exec"), ns); return ns


def test_viewer_size_and_falloff_factors():
    ns = _ns(); assert ns["LIGHT_RADIUS_SCALE"] == 1.5 and ns["LIGHT_FALLOFF_FUDGE"] == 0.5
    assert 'l.get("falloff", 0.75) * LIGHT_FALLOFF_FUDGE' in SRC and 'radius = l["radius"] * LIGHT_RADIUS_SCALE' in SRC
    assert 'max(radius, 0.01)' in SRC and 'max(l["radius"], 0.01)' not in SRC


def test_only_two_nearest_projectors_cast_shadows():
    ns = _ns(); f = ns["light_shadow_ids"]
    P = lambda x, proj=True: {"pos": [x, 0, 0], **({"proj": {"fov": 1}} if proj else {})}
    ls = [P(9), P(1), P(5), P(3), P(0.5, False)]
    ids = f(ls, [0, 0, 0], mode="sl")
    assert ids == {id(ls[1]), id(ls[3])}          # x=1 and x=3; the point light at 0.5 never casts
    assert f(ls, [0, 0, 0], n=0, mode="sl") == set()
    assert f(ls, [0, 0, 0], mode="all") == {id(l) for l in ls}


def test_renderer_uses_it():
    assert "d.use_shadow = id(l) in shadow_ids" in SRC and "light_shadow_ids(M.get(" in SRC
