"""photo hd terrain + sky (2026-10-13): region terrain composition, SL defaults for old exports, EEP clouds (no Blender)."""
import importlib.util, os, sys, types
if "PIL" not in sys.modules:
    try:
        import PIL.Image  # noqa: F401
    except ImportError:
        pil = types.ModuleType("PIL"); pil.Image = None; sys.modules["PIL"] = pil
sys.modules.setdefault("handler", types.ModuleType("handler"))
sys.modules.setdefault("make_job", types.ModuleType("make_job"))
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT); sys.path.insert(0, os.path.join(ROOT, "cloud-render"))
import look  # noqa: E402
import llsky  # noqa: E402

def test_old_export_gets_sl_default_terrain():
    c = look.terrain_composition({"region": "Peronaut"}, "/x", fetch=lambda u: "/x/" + u)
    assert len(c) == 1 and c[0]["default"] and c[0]["detail"] == look.SL_TERRAIN_DEFAULT["detail"]
    assert c[0]["paths"] == ["/x/" + u for u in look.SL_TERRAIN_DEFAULT["detail"]] and c[0]["offset"] == [0, 0, 0]

def test_exported_composition_kept_and_null_offsets_zero():
    d = ["11111111-1111-1111-1111-11111111111%d" % i for i in range(4)]
    doc = {"terrain_composition": [{"region": "A", "offset": [None, None, None], "detail": d, "start": [5, 5, 5, 5], "range": [30, 30, 30, 30]},
                                   {"region": "B", "offset": [256, None, None], "detail": [None] * 4, "start": [0] * 4, "range": [0] * 4}]}
    def boom(u):
        if u.endswith("2"): raise IOError("cdn down")
        return "/t/" + u
    c = look.terrain_composition(doc, "/t", fetch=boom)
    assert c[0]["detail"] == d and c[0]["offset"] == [0, 0, 0] and c[0]["paths"][2] is None and not c[0].get("default")
    assert c[1]["offset"] == [256, 0, 0] and c[1]["default"]   # zero texture ids -> SL defaults

def test_env_carries_clouds():
    p = llsky.params({"cloud_shadow": 0.27, "cloud_color": [0.2, 0.2, 0.2], "cloud_scale": 0.42, "cloud_pos_density1": [1, 0.5, 0.88]})
    assert abs(p["cloud_density"] - 0.88) < 1e-6 and p["cloud_shadow"] == 0.27 and p["cloud_color"] == [0.2, 0.2, 0.2]

def test_hd_only_paths_in_render_mesh():
    rm = open(os.path.join(ROOT, "cloud-render", "render_mesh.py")).read()
    assert 'if os.environ.get("GT_HD") == "1": col = sky_hd(' in rm
    assert 'comps = t.get("composition") if hd else None' in rm
