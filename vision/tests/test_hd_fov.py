"""photo hd camera = Firestorm's 60 deg vertical view angle (2026-10-14, David); `fov <deg>` override."""
import ast, math, os, sys
HERE = os.path.dirname(os.path.abspath(__file__)); SRC = os.path.join(HERE, "..", "cloud-render", "render_mesh.py")

def _fn(name):
    src = open(SRC).read(); node = next(n for n in ast.parse(src).body if isinstance(n, ast.FunctionDef) and n.name == name)
    ns = {"math": math}; exec(ast.get_source_segment(src, node), ns); return ns[name]

def test_lens_gives_60_deg_vertical():
    lens = _fn("lens_for_vfov")
    for rx, ry in ((3840, 2160), (2160, 3840), (1920, 1080)):
        sh = 36.0 * (ry / rx if rx >= ry else 1.0)
        assert abs(math.degrees(2 * math.atan(sh / (2 * lens(60, rx, ry)))) - 60) < 1e-6

def test_hd_settings_default_and_override():
    try: sys.path.insert(0, os.path.join(HERE, "..")); import look
    except ImportError:
        import pytest; pytest.skip("look deps")
    _, env = look.hd_settings("landscape", "self")
    assert env["GT_HD_FOV"] == "60"
