"""Hover check per body part (2026-10-27, David: look at the ground the way he does): the gap under hips / hands / chest / thighs ... against the floor drawn in the render.
Synthetic skeleton + vertices only (render_mesh.py needs bpy, so the pure functions are lifted out of it)."""
import ast, math, pathlib
import numpy as np

SRC = pathlib.Path(__file__).resolve().parents[1] / "cloud-render" / "render_mesh.py"

def _rm():
    tree = ast.parse(SRC.read_text())
    consts = {"BONE_PART_SEGS", "PART_MIN_VERTS"}
    body = [n for n in tree.body if isinstance(n, ast.Assign) and any(getattr(t, "id", "") in consts for t in n.targets)] + \
           [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in {"assign_body_parts", "body_part_gaps", "hover_parts_line"}]
    ns = {"np": np, "math": math}; exec(compile(ast.Module(body, []), "render_mesh", "exec"), ns)
    return ns

def _skeleton():
    """25 segments laid on the floor plane in x (each 0.2 m long, apart in y), so the part of a vertex is the segment index it sits on"""
    return [[(0.0, 0.3 * i, 0.0), (0.2, 0.3 * i, 0.0)] for i in range(25)]

def _cloud(rng, seg, z, n=60):
    return np.column_stack([rng.uniform(0.0, 0.2, n), 0.3 * seg + rng.uniform(-0.03, 0.03, n), z + rng.uniform(0.0, 0.01, n)])

def test_each_vertex_belongs_to_the_part_of_its_nearest_segment():
    rm = _rm(); rng = np.random.default_rng(1)
    v = np.vstack([_cloud(rng, 0, 0.30), _cloud(rng, 9, 0.05), _cloud(rng, 14, 0.07), _cloud(rng, 1, 0.10), _cloud(rng, 16, 0.04), _cloud(rng, 4, 1.5)])
    parts = rm["assign_body_parts"](v, _skeleton())
    assert set(parts) == {"hips", "hands", "chest", "thighs"}   # the head segment (4) is no supporting part
    gaps = rm["body_part_gaps"](parts, 0.0)
    assert abs(gaps["hips"] - 0.30) < 0.02 and abs(gaps["hands"] - 0.05) < 0.02 and abs(gaps["chest"] - 0.10) < 0.02 and abs(gaps["thighs"] - 0.04) < 0.02

def test_a_hand_sunk_into_the_floor_reads_negative_and_the_floor_is_the_one_drawn():
    rm = _rm(); rng = np.random.default_rng(2)
    v = np.vstack([_cloud(rng, 0, 0.0), _cloud(rng, 9, -0.04), _cloud(rng, 14, -0.04)])
    g = rm["body_part_gaps"](rm["assign_body_parts"](v, _skeleton()), 0.0)
    assert g["hands"] < -0.03 and abs(g["hips"]) < 0.02
    g2 = rm["body_part_gaps"](rm["assign_body_parts"](v, _skeleton()), -0.2)   # the same body over a floor 0.2 m lower: every gap is 0.2 larger (the floor in the render decides)
    assert abs((g2["hands"] - g["hands"]) - 0.2) < 1e-9

def test_a_part_with_too_few_vertices_is_not_reported_and_a_wrong_skeleton_gives_nothing():
    rm = _rm(); rng = np.random.default_rng(3)
    parts = rm["assign_body_parts"](np.vstack([_cloud(rng, 0, 0.0, 100), _cloud(rng, 9, 0.0, 5)]), _skeleton())
    assert "hips" in parts and "hands" not in parts
    assert rm["assign_body_parts"](_cloud(rng, 0, 0.0), _skeleton()[:10]) == {}
    assert rm["assign_body_parts"](np.zeros((0, 3)), _skeleton()) == {}

def test_the_line_the_client_parses_has_a_fixed_order_and_sign():
    rm = _rm()
    assert rm["hover_parts_line"]({"hands": -0.04, "hips": 0.012, "chest": 0.3}) == "GT: hover check parts: hips +0.012, chest +0.300, hands -0.040"
