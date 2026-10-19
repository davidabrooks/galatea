"""2026-10-18: layered photo hd for crowds (vision/layers.py, look.render_layered, render_mesh GT_LAYER=base|batch).
One Blender process held the world + all ~90 avatars (~3M world vertices + ~7M avatar vertices) and was killed by the memory guard,
so the crowd is rendered as a base pass (world + her, the others as shadow-only stand-ins) plus holdout batch passes of a few avatars,
farthest batch first, alpha-over composited. Pure logic here; the offline end-to-end run is optional (needs a crowd export)."""
import json
import os
import re
import subprocess
import sys
import types

import numpy as np

sys.modules.setdefault("handler", types.ModuleType("handler"))
sys.modules.setdefault("make_job", types.ModuleType("make_job"))
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)
import layers  # noqa: E402
import look  # noqa: E402

RM = open(os.path.join(ROOT, "cloud-render", "render_mesh.py")).read()


def B(group, nv): return {"group": group, "nv": nv, "ni": 3}


CROWD = [B("scene", 2_000_000), B("far", 500_000), B("avatar", 150_000)] + [B(f"avatar:{i:02d}", 100_000) for i in range(40)]


def test_mode_parsing():
    assert layers.layers_mode({}) == "auto"
    assert layers.layers_mode({"GT_HD_LAYERS": "ON"}) == "on"
    assert layers.layers_mode({"GT_HD_LAYERS": "off"}) == "off"
    assert layers.layers_mode({"GT_HD_LAYERS": "banana"}) == "auto"


def test_needs_layers():
    assert layers.needs_layers(CROWD, 7500, 1500, "auto")                  # 6.6 M vertices do not fit 7.5 GB - guard floor
    assert not layers.needs_layers(CROWD[:3] + CROWD[3:5], 16000, 1500, "auto")
    assert not layers.needs_layers(CROWD, 7500, 1500, "off")
    assert layers.needs_layers(CROWD[:3], 99999, 1500, "on")
    assert not layers.needs_layers(CROWD, None, 1500, "auto")              # unknown memory: the old single pass


def test_world_vs_others():
    assert layers.world_verts(CROWD) == 2_650_000
    assert layers.others_verts(CROWD) == 4_000_000


def test_batch_budget_never_below_minimum():
    assert layers.batch_budget_verts(CROWD, 3000) == layers.MIN_BATCH_VERTS
    assert layers.batch_budget_verts(CROWD, 64000) > layers.batch_budget_verts(CROWD, 12000)
    assert layers.batch_budget_verts(CROWD, None) > 0


def plan(n=10, verts=100_000):
    return [{"group": f"avatar:{i}", "name": str(i), "cam_dist": float(i + 1), "visible": True, "verts": verts} for i in range(n)]


def test_batches_are_farthest_first_and_contiguous():
    bs = layers.plan_batches(plan(10), 250_000, max_per_batch=6)
    assert [g for b in bs for g in b] == [f"avatar:{i}" for i in range(9, -1, -1)]   # far -> near: paint order
    assert all(len(b) <= 2 for b in bs)                                              # 250 k budget = 2 x 100 k


def test_batches_respect_max_per_batch():
    assert [len(b) for b in layers.plan_batches(plan(14, 1000), 10 ** 9, max_per_batch=6)] == [6, 6, 2]


def test_oversized_avatar_gets_its_own_batch():
    p = plan(3); p[1]["verts"] = 5_000_000
    bs = layers.plan_batches(p, 300_000)
    assert ["avatar:1"] in bs and sum(len(b) for b in bs) == 3


def test_invisible_avatars_are_not_rendered():
    p = plan(4)
    p[2]["visible"] = False
    flat = [g for b in layers.plan_batches(p, 10 ** 9) for g in b]
    assert "avatar:2" not in flat and len(flat) == 3
    assert layers.plan_batches([], 1000) == []


def test_split_batch():
    assert layers.split_batch(["a", "b", "c"]) == [["a", "b"], ["c"]]
    assert layers.split_batch(["a"]) == [["a"]]


def test_alpha_over_premultiplied():
    bg = np.full((2, 2, 3), 0.5, np.float32)
    fg = np.zeros((2, 2, 4), np.float32)
    fg[0, 0] = (0.8, 0.2, 0.1, 1.0)         # opaque: replaces
    fg[0, 1] = (0.1, 0.1, 0.1, 0.5)         # half: 0.1 + 0.5 * 0.5
    out = layers.alpha_over(bg.copy(), fg)
    assert np.allclose(out[0, 0], (0.8, 0.2, 0.1))
    assert np.allclose(out[0, 1], 0.35)
    assert np.allclose(out[1, 0], 0.5) and np.allclose(out[1, 1], 0.5)   # alpha 0: untouched


def test_composite_order_matters_nearer_wins():
    base = np.ones((1, 1, 4), np.float32) * 0.2
    far = np.array([[[1.0, 0.0, 0.0, 1.0]]], np.float32)
    near = np.array([[[0.0, 1.0, 0.0, 1.0]]], np.float32)
    assert np.allclose(layers.composite(base, [far, near])[0, 0], (0, 1, 0))
    assert np.allclose(layers.composite(base, [near, far])[0, 0], (1, 0, 0))
    assert np.allclose(layers.composite(base, [lambda: far])[0, 0], (1, 0, 0))   # loaders are accepted (memory: one layer at a time)
    assert np.allclose(base[0, 0], 0.2)                                          # the base is not modified


def test_read_plan(tmp_path):
    p = tmp_path / "layer-plan.json"; p.write_text(json.dumps(plan(2)))
    assert [x["group"] for x in layers.read_plan(str(p))] == ["avatar:0", "avatar:1"]


def test_mem_guard_reports_peak(tmp_path):
    code = "import time; x = bytearray(150 * 2**20); [x.__setitem__(i, 1) for i in range(0, len(x), 4096)]; time.sleep(1.2)"
    r = look.run_mem_guarded([sys.executable, "-c", code], dict(os.environ), floor_mb=0, poll_s=0.1)
    assert r.returncode == 0 and r.peak_mb >= 150 and r.peak_anon_mb >= 140


def test_render_mesh_batch_is_holdout_and_premultiplied():
    # batch pass: transparent film, every non-batch mesh a holdout, the other avatars' stand-ins shadow-only (not holdout)
    assert "sc.render.film_transparent = True" in RM and "ob.is_holdout = True" in RM
    assert "ob not in keep" in RM and "visible_camera" in RM
    # the compositor haze must not tint anti-aliased holdout edges: batch passes leave haze to one scalar per layer (layers.layer_fog)
    assert "-haze.json" in RM and "layer_fog" in RM
    # world textures are shrunk to a silhouette + average colour in a batch pass
    assert "GT_LAYER_HOLD_PX" in RM and "mat_images(b, small=hold)" in RM and "emission_sampling" in RM


def test_render_mesh_layer_outputs_rgba_npy():
    assert 'np.save(npy, rgba)' in RM and 'GT_LAYER_NPY' in RM
    assert 'rgba[..., 3] = 1.0' in RM        # the base is opaque


def test_batch_layer_edges_carry_no_background_colour():
    """David's rule: a batch layer is avatar-only. A depth-of-field-blurred / anti-aliased silhouette (alpha ramp) must composite to the
    avatar's own colour at that alpha: unpremultiplied, the fringe equals the avatar (plus the tiny distance haze), not the sky/horizon colour."""
    avatar = np.array([0.05, 0.05, 0.06], np.float32)          # a dark-clad avatar
    sky = np.array([0.45, 0.55, 0.95], np.float32)             # the horizon/sky colour a flat quick-photo background would have
    alpha = np.linspace(0.0, 1.0, 21, dtype=np.float32)[None, :]
    layer = np.zeros((1, 21, 4), np.float32)
    layer[..., :3] = avatar * alpha[..., None]; layer[..., 3] = alpha          # premultiplied, nothing else in the pixel
    for fog in (0.0, 0.02):
        out = layers.alpha_over(np.zeros((1, 21, 3), np.float32), layer.copy(), fog, sky)
        # what the layer itself added at each pixel, divided by alpha = the fringe colour
        add = out[0, 1:]; un = add / alpha[0, 1:, None]
        want = avatar * (1 - fog) + sky * fog
        assert np.allclose(un, want, atol=1e-5), f"fringe tinted at fog={fog}"
        assert np.allclose(out[0, 0], 0)                                       # alpha 0: nothing
    # a 2 % haze nudges a dark avatar's fringe by 2 % of the sky colour; the old per-pixel edge mist (fog ~1 at the edge) gave ~ the sky colour
    assert (np.abs(un - avatar).max() < 0.02 * sky.max() + 1e-5)


def test_layer_fog_ignores_edge_mist():
    alpha = np.zeros((9, 9), np.float32); alpha[3:6, 3:6] = 1.0; alpha[2, 3:6] = 0.4
    mist = np.ones((9, 9), np.float32); mist[3:6, 3:6] = 0.05; mist[2, 3:6] = 0.8     # the edge's mist blends the sky in
    f = layers.layer_fog(alpha, mist, 0.0005, 4000.0)
    assert abs(f - (1 - np.exp(-0.0005 * 4000 * 0.05))) < 1e-6
    assert layers.layer_fog(np.zeros((3, 3), np.float32), np.ones((3, 3), np.float32), 0.0005, 4000.0) == 0.0
    assert layers.layer_fog(alpha, np.ones((9, 9), np.float32), 0.0005, 4000.0) == 0.0   # solid pixels at the sky: no fog


def test_haze_sidecar_roundtrip(tmp_path):
    p = str(tmp_path / "batch-0.npy")
    assert layers.read_haze(p) == (0.0, None)
    open(layers.haze_path(p), "w").write(json.dumps({"fog": 0.03, "col": [0.4, 0.5, 0.9]}))
    assert layers.read_haze(p) == (0.03, [0.4, 0.5, 0.9])


def test_composite_applies_per_layer_haze():
    base = np.zeros((1, 1, 4), np.float32)
    lay = np.array([[[0.5, 0.5, 0.5, 1.0]]], np.float32)
    out = layers.composite(base, [lambda: (lay, 0.1, [1.0, 0.0, 0.0])])
    assert np.allclose(out[0, 0], (0.5 * 0.9 + 0.1, 0.45, 0.45))


def test_render_mesh_batch_writes_raw_layer_and_no_compositor_haze():
    # the compositor's mist/haze mix is base-pass only; a batch pass writes its Mist to a file for one scalar fog and bakes nothing in
    assert "haze_batch" not in RM
    assert "def layer_haze_sidecar" in RM or "layers-haze" in RM or "-haze.json" in RM
