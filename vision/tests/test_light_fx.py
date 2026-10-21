"""2026-10-18 light effects (beams + halos): camera-only emissive geometry, never in a batch layer, switchable, subtle by default."""
import ast, math, os

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = open(os.path.join(HERE, "cloud-render", "render_mesh.py")).read()


def _ns(**env):
    for k, v in env.items(): os.environ[k] = v
    tree = ast.parse(SRC); keep = []
    for n in tree.body:
        if isinstance(n, ast.FunctionDef) and n.name in ("beam_params", "halo_params"): keep.append(n)
        elif isinstance(n, ast.Assign) and any(isinstance(t, ast.Name) and t.id in ("LIGHT_FX", "LIGHT_BEAM", "LIGHT_HALO", "BEAM_MAX_HALF") for t in n.targets): keep.append(n)
    ns = {"os": os, "math": math, "LAYER": os.environ.get("TEST_LAYER", "")}; exec(compile(ast.Module(keep, []), "rm", "exec"), ns)
    for k in env: del os.environ[k]
    return ns


def test_beam_only_for_real_projectors():
    ns = _ns(); f = ns["beam_params"]
    assert f({"intensity": 1, "radius": 10}) is None                                                    # a point light: halo only
    assert f({"intensity": 1, "radius": 10, "proj": {"fov": 2.8}}) is None                              # wide fill projector
    assert f({"intensity": 0.05, "radius": 10, "proj": {"fov": 0.8}}) is None                           # too dim
    half, length, k = f({"intensity": 1, "radius": 10, "proj": {"fov": 0.4}})
    assert abs(half - 0.2) < 1e-9 and length == 15 and 0 < k < 0.2                                       # cone = fov/2, throw = viewer size (radius x 1.5)
    assert f({"intensity": 1, "radius": 10, "proj": {"fov": 1.57}})[0] == ns["BEAM_MAX_HALF"]           # wide cones capped, tasteful
    assert f({"intensity": 1, "radius": 10, "proj": {"fov": 0.4}})[2] > f({"intensity": 1, "radius": 10, "proj": {"fov": 1.57}})[2]   # narrow = brighter


def test_halo_and_switches():
    f = _ns()["halo_params"]
    assert f({"intensity": 0.05, "color": [1, 1, 1]}) is None and f({"intensity": 1, "color": [0, 0, 0]}) is None
    r, k = f({"intensity": 1, "color": [1, 0, 0], "radius": 4}); assert 0.2 < r < 0.5 and k > 0
    assert _ns(GT_LIGHT_BEAM="0")["beam_params"]({"intensity": 1, "radius": 10, "proj": {"fov": 0.4}}) is None
    assert _ns(GT_LIGHT_HALO="0")["halo_params"]({"intensity": 1, "color": [1, 1, 1]}) is None
    assert _ns(GT_LIGHT_FX="0")["LIGHT_FX"] is False and _ns()["LIGHT_FX"] is True


def test_never_in_a_batch_layer_and_camera_only():
    assert _ns(TEST_LAYER="batch")["LIGHT_FX"] is False and _ns(TEST_LAYER="base")["LIGHT_FX"] is True
    assert 'LIGHT_FX = os.environ.get("GT_LIGHT_FX", "1") != "0" and LAYER != "batch"' in SRC and "if LIGHT_FX: light_fx(" in SRC
    for a in ("visible_diffuse", "visible_glossy", "visible_transmission", "visible_volume_scatter", "visible_shadow"): assert f'"{a}"' in SRC
    assert "ShaderNodeBsdfTransparent" in SRC and "ShaderNodeCameraData" in SRC   # surface shaders only (no volume), faded near the lens
