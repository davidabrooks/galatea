"""2026-10-18 hair scalp cap: David: hide the flat scalp band on her own hair (hair texture 8d5ad85f, mask-material copy)."""
import ast, os
import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = open(os.path.join(HERE, "cloud-render", "render_mesh.py")).read()
TEX = "8d5ad85f-453b-ed86-31b3-c7356150164f"


def _ns(**env):
    for k, v in env.items(): os.environ[k] = v
    tree = ast.parse(SRC); keep = []
    for n in tree.body:
        if isinstance(n, ast.FunctionDef) and n.name == "scalp_cap_filter": keep.append(n)
        elif isinstance(n, ast.Assign) and any(isinstance(t, ast.Name) and t.id in ("SCALP_HIDE", "SCALP_CAPS") for t in n.targets): keep.append(n)
    ns = {"os": os, "np": np}; exec(compile(ast.Module(keep, []), "rm", "exec"), ns)
    for k in env: del os.environ[k]
    return ns


def _mesh():
    # 3 triangles: two strand triangles (u < 0.8) and one cap triangle (all u > 0.83)
    T = np.array([[0.1, 0.1], [0.2, 0.1], [0.1, 0.2], [0.5, 0.5], [0.6, 0.5], [0.5, 0.6], [0.85, 0.2], [0.95, 0.2], [0.9, 0.4]], np.float32)
    return T, np.arange(9, dtype=np.uint32)


def test_cap_triangles_removed_from_the_mask_batch_only():
    f = _ns()["scalp_cap_filter"]; T, I = _mesh()
    out = f({"tex": TEX, "mat": {"alpha": "mask"}}, T, I)
    assert out.tolist() == list(range(6))                                         # strand triangles kept in order
    assert f({"tex": TEX, "mat": {"alpha": "auto"}}, T, I) is I                    # the blended soft layer is untouched
    assert f({"tex": "other", "mat": {"alpha": "mask"}}, T, I) is I                # keyed by texture id
    assert f({"tex": TEX, "mat": {"alpha": "mask"}}, T[:6], np.arange(6, dtype=np.uint32)).tolist() == list(range(6))


def test_switch_and_her_own_avatar_only():
    T, I = _mesh(); b = {"tex": TEX, "mat": {"alpha": "mask"}}
    assert _ns(GT_HAIR_SCALP_HIDE="0")["scalp_cap_filter"](b, T, I) is I
    assert 'if b["group"] == "avatar" and SCALP_HIDE:' in SRC and "I = scalp_cap_filter(b, T, I); ni = len(I)" in SRC
