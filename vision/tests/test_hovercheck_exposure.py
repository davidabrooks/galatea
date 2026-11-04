"""2026-11-03 Lalou sofa: the hover / seat check pinned GT_EXPOSURE=0, which beat the -1.5 quick-look default, so seat close-ups
came out 36-53 % blown out and the seat-adjust gate refused them. The check must render at the quick-look exposure."""
import ast, os, pathlib, subprocess, sys

V = pathlib.Path(__file__).resolve().parents[1]

def _const(path, name):
    for n in ast.parse(path.read_text()).body:
        if isinstance(n, ast.Assign) and any(getattr(t, "id", None) == name for t in n.targets):
            return ast.literal_eval(n.value)

def test_hovercheck_exposure_matches_quick_look_default():
    assert _const(V / "look.py", "HOVERCHECK_EXPOSURE") == _const(V / "cloud-render" / "render_mesh.py", "QUICK_EXPOSURE") == -1.5

def test_hovercheck_never_pins_exposure_zero():
    src = (V / "look.py").read_text()
    assert 'os.environ.get("GT_EXPOSURE", "0")' not in src
    assert 'os.environ.get("GT_EXPOSURE", hovercheck_exposure())' in src

def test_hovercheck_exposure_string():
    ns = {}
    tree = ast.parse((V / "look.py").read_text())
    keep = [n for n in tree.body if (isinstance(n, ast.Assign) and any(getattr(t, "id", None) == "HOVERCHECK_EXPOSURE" for t in n.targets))
            or (isinstance(n, ast.FunctionDef) and n.name == "hovercheck_exposure")]
    exec(compile(ast.Module(body=keep, type_ignores=[]), "look.py", "exec"), ns)
    assert float(ns["hovercheck_exposure"]()) == -1.5
