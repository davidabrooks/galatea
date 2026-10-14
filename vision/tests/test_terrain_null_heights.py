"""2026-10-13: OSD JSON writes 0 as null, so a region corner with terrain start height 0 exported start [null, ...] and
render_mesh.terrain_layers crashed (TypeError) in photo hd."""
import os, sys
HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE); sys.path.insert(0, os.path.join(HERE, "cloud-render"))
import types
for _m in ("handler", "make_job"):   # look.py's PIL/imagecodecs deps aren't needed for terrain_composition
    sys.modules.setdefault(_m, types.ModuleType(_m))
import look  # noqa: E402


def test_null_start_and_range_become_zero():
    doc = {"terrain_composition": [{"region": "Peronaut", "offset": [0, 0, None], "detail": ["a", "b", "c", "d"],
                                    "start": [None, 10.0, None, 12.0], "range": [None, None, 30.0, 40.0]}]}
    c = look.terrain_composition(doc, "/tmp", fetch=lambda u: None)[0]
    assert c["start"] == [0.0, 10.0, 0.0, 12.0] and c["range"] == [0.0, 0.0, 30.0, 40.0] and c["offset"] == [0, 0, 0]


def test_missing_lists_fall_back_to_sl_defaults():
    c = look.terrain_composition({"terrain_composition": [{"offset": [0, 0, 0], "detail": ["a", "b", "c", "d"]}]}, "/tmp", fetch=lambda u: None)[0]
    assert c["start"] == look.SL_TERRAIN_DEFAULT["start"] and c["range"] == look.SL_TERRAIN_DEFAULT["range"]


def test_renderer_bilinear_is_null_safe():
    src = open(os.path.join(HERE, "cloud-render", "render_mesh.py")).read()
    assert "(w or 0.0) * f" in src


def test_hd_retry_after_oom_kill_is_lighter():
    r = look.retry_env(-9, True)
    assert r["GT_TEX_LIMIT"] == "1024" and r["GT_TILE"] == "512" and float(r["GT_TEX_BIAS"]) >= 2
    assert look.retry_env(-11, True) == {} and look.retry_env(-9, False) == {}
