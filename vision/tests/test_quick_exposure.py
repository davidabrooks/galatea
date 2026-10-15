"""2026-10-14 David: quick (non-HD) scene renders were way too bright -> default exposure -1.5 stops; HD unchanged."""
import ast, os
HERE = os.path.dirname(os.path.abspath(__file__)); SRC = os.path.join(HERE, "..", "cloud-render", "render_mesh.py")

def _ns():
    src = open(SRC).read(); tree = ast.parse(src); ns = {}
    for n in tree.body:
        if (isinstance(n, ast.FunctionDef) and n.name == "view_exposure") or (isinstance(n, ast.Assign) and any(getattr(t, "id", "") == "QUICK_EXPOSURE" for t in n.targets)):
            exec(compile(ast.Module([n], []), SRC, "exec"), ns)
    return ns

def test_quick_scene_defaults_to_minus_1_5():
    f = _ns()["view_exposure"]
    assert f({}, False, "scene") == -1.5 and f({"GT_EXPOSURE": ""}, False, "scene") == -1.5

def test_explicit_exposure_wins():
    f = _ns()["view_exposure"]
    assert f({"GT_EXPOSURE": "0"}, False, "scene") == 0.0 and f({"GT_EXPOSURE": "0.0"}, False, "scene") == 0.0   # the Runpod handler always sets one
    assert f({"GT_EXPOSURE": "-0.5"}, False, "scene") == -0.5 and f({"GT_EXPOSURE": "1"}, True, "scene") == 1.0

def test_hd_and_studio_portraits_unchanged():
    f = _ns()["view_exposure"]
    assert f({}, True, "scene") == 0.0
    assert f({}, False, "face") == 0.0 and f({}, False, "body") == 0.0

def test_render_uses_view_exposure():
    src = open(SRC).read()
    assert "sc.view_settings.exposure = view_exposure(os.environ, HD, kind)" in src
