import os, sys, types
sys.modules.setdefault("handler", types.ModuleType("handler")); sys.modules.setdefault("make_job", types.ModuleType("make_job"))
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import look

def test_geometry_shrinks_texture_budget():
    b = [{"group": "scene", "nv": 3_800_000}, {"group": "avatar", "nv": 270_000}, {"group": "avatar:x", "nv": 500_000}]
    g = look.hd_geom_mb(b, layered=True)
    assert g > 2500
    assert look.tex_budget_mb(7500, 1500, geom_mb=g) < look.tex_budget_mb(7500, 1500)
    assert look.hd_geom_mb(b, layered=False) > g
    assert look.hd_geom_mb([{"group": "scene", "nv": 500_000}], True) == 0

def test_oom_retry_escalates_bands(monkeypatch):
    monkeypatch.setenv("GT_HD_BANDS", "3")
    e = look.retry_env(-9, "portrait")
    assert e["GT_HD_BANDS"] == "6" and e["GT_BVH_COMPACT"] == "1"
    assert look.retry_env(-11, "portrait") == {}
