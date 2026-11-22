"""Draft (fix/seat-dark-widen): widen_width() levels for a too-dark seat close-up (GT_WIDEN). Pure math from render_mesh.py; no Blender.
The xfail tests are the open problems that keep this PR a draft: they must pass before it can be merged."""
import ast, math, os
import pytest

SRC = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "cloud-render", "render_mesh.py")

def _widen():
    src = open(SRC).read(); ns = {"math": math}
    for n in ast.parse(src).body:
        if isinstance(n, ast.FunctionDef) and n.name == "widen_width": exec(ast.get_source_segment(src, n), ns)
    return ns["widen_width"]

def test_level_zero_keeps_the_close_up_limits():
    w = _widen()
    assert w(0.35, 0.8, 0) == (0.35, 0.8)     # part view
    assert w(0.55, 1.1, 0) == (0.55, 1.1)     # hips view

@pytest.mark.xfail(strict=True, reason="level 1 caps the wide retake (GT_SUPPORT_WIDE) at 1.6 m; main allows 1.6-3.0 m")
def test_level_one_matches_the_existing_wide_retake():
    assert _widen()(0.55, 1.1, 1) == (1.6, 3.0)

@pytest.mark.xfail(strict=True, reason="levels 2+ all give (1.6, 3.0): nothing wider than the existing wide retake")
def test_each_level_is_wider_than_the_last():
    w = _widen()
    assert w(0.55, 1.1, 3)[1] > w(0.55, 1.1, 2)[1] > w(0.55, 1.1, 1)[1]
