"""2026-10-31 14:34 login: the floor she stands on (z 28.03) was not in the export yet; the cast hit z 25.04 and the check read +3.004 m."""
import ast, pathlib
SRC = pathlib.Path(__file__).resolve().parents[1] / "cloud-render" / "render_mesh.py"

def _rm():
    tree = ast.parse(SRC.read_text())
    body = [n for n in tree.body if (isinstance(n, ast.Assign) and any(getattr(t, "id", "") == "HOVER_FLOOR_MISSING_M" for t in n.targets))
            or (isinstance(n, ast.FunctionDef) and n.name in {"hover_floor_missing", "hover_check_z0"})]
    ns = {}; exec(compile(ast.Module(body, []), "render_mesh", "exec"), ns); return ns

def test_login_1434_three_metre_gap_is_no_reading():
    rm = _rm()
    env = {"GT_HOVER_CHECK": "1", "GT_AGENT_SIZE": "1.572", "GT_HOVER": "-0.086", "GT_HOVER_BIAS": "0.1285"}
    z0, gap, *_ = rm["hover_check_z0"](env, 29.0, 0.0, 25.043)
    assert gap > 2.5 and rm["hover_floor_missing"](gap)
    _, gap2, *_ = rm["hover_check_z0"](env, 29.0, 0.0, 28.033)
    assert not rm["hover_floor_missing"](gap2)          # the real floor: a normal reading

def test_seated_and_small_gaps_still_read():
    rm = _rm()
    assert not rm["hover_floor_missing"](3.0, seated=True)
    assert not rm["hover_floor_missing"](0.4) and not rm["hover_floor_missing"](None)
