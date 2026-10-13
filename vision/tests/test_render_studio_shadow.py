"""Studio lights in self/face renders cast no shadow (2026-10-13): with Cycles contact shadows the Kay ttchL/ttchR chest
cut-outs, open (alpha-hidden face) in-world, rendered as black panels that read as closed; the SL viewer's local lights
don't shadow. render_mesh.py needs bpy, so this checks the source."""
import ast, pathlib

SRC = pathlib.Path(__file__).resolve().parents[1] / "cloud-render" / "render_mesh.py"

def _func(name):
    tree = ast.parse(SRC.read_text())
    return next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == name)

def test_area_lights_shadowless_by_default():
    body = ast.unparse(_func("area"))
    assert "L.data.use_shadow" in body
    assert "os.environ.get('GT_STUDIO_SHADOW', '0') == '1'" in body   # opt back in with GT_STUDIO_SHADOW=1

def test_body_focus_closeup_is_parsed():
    src = SRC.read_text()
    assert 'os.environ.get("GT_BODY_FOCUS", "")' in src
