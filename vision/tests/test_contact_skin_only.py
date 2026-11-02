"""2026-11-02 VAMOSITG24: the loose jeans hems were the low touchpoints (feet "5 cm under the floor"); David's photo had her bare feet on it.
Touchpoints come from the Bakes-on-Mesh skin only; clothing / attachments weigh 0; with no skin mesh everything counts."""
import ast, pathlib
SRC = pathlib.Path(__file__).resolve().parents[1] / "cloud-render" / "render_mesh.py"

def _f():
    t = ast.parse(SRC.read_text()); ns = {}
    exec(compile(ast.Module([n for n in t.body if isinstance(n, ast.FunctionDef) and n.name == "contact_obs"], []), "rm", "exec"), ns); return ns["contact_obs"]

class Ob(dict):
    type = "MESH"
    def __init__(self, n, skin): super().__init__({"gt_skin": 1} if skin else {}); self.data = type("D", (), {"vertices": [0] * n})()

def test_clothing_is_not_a_touchpoint():
    body, jeans = Ob(10, True), Ob(10, False); assert _f()([body, jeans]) == [body]

def test_no_skin_falls_back_to_everything():
    a, b = Ob(5, False), Ob(5, False); assert _f()([a, b]) == [a, b]

def test_build_tags_bake_textures_and_hover_check_uses_it():
    s = SRC.read_text()
    assert 'if b["tex"].startswith("bake:"): ob["gt_skin"] = 1' in s and "for ob in contact_obs(obs):" in s
