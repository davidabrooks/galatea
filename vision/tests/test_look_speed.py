"""Faster crowd looks (2026-10-05): decoded-bake cache keys, the alpha index that spares render_mesh a pixel scan,
and the complete-vs-stand-in count reported for a crowd (no network, no Blender)."""
import os
import sys
import types

sys.modules.setdefault("handler", types.ModuleType("handler"))
sys.modules.setdefault("make_job", types.ModuleType("make_job"))
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import look  # noqa: E402


def test_bake_cache_key_follows_content_and_cap():
    a = look.bake_cache_key(b"j2c bytes", 512)
    assert a == look.bake_cache_key(b"j2c bytes", 512)          # same bake, same cap: a hit
    assert a != look.bake_cache_key(b"j2c bytes!", 512)         # avatar re-baked: a miss
    assert a != look.bake_cache_key(b"j2c bytes", 256)          # different thumbnail size: a miss
    assert a.endswith(".512.png") and "/" not in a


def test_alpha_from_extrema_matches_render_mesh_threshold():
    assert look.alpha_from_extrema(255, 255) == [False, 1.0]    # opaque
    assert look.alpha_from_extrema(250, 255) == [False, 1.0]    # 250/255 = 0.980 is not < 0.98
    assert look.alpha_from_extrema(249, 255)[0] is True         # 249/255 < 0.98: cut-out
    assert look.alpha_from_extrema(0, 0) == [True, 0.0]         # fully blank: skipped in render_mesh


def test_index_fresh_needs_same_size_and_mtime():
    st = types.SimpleNamespace(st_size=100, st_mtime_ns=5)
    assert look.index_fresh([100, 5, False, 1.0], st)
    assert not look.index_fresh([101, 5, False, 1.0], st)
    assert not look.index_fresh([100, 6, False, 1.0], st)
    assert not look.index_fresh(None, st)


def test_write_manifest_scans_each_texture_once(tmp_path=None):
    import tempfile
    work = str(tmp_path) if tmp_path else tempfile.mkdtemp()
    f = os.path.join(work, "a.png"); open(f, "wb").write(b"x")
    calls = []
    real = look.alpha_stats_png
    look.alpha_stats_png = lambda p: calls.append(p) or [True, 0.5]
    try:
        assert look.write_manifest(work, {"uuid-a": f}) == 1
        assert look.write_manifest(work, {"uuid-a": f}) == 0     # second look: from the index
    finally:
        look.alpha_stats_png = real
    import json
    m = json.load(open(os.path.join(work, "tex-manifest.json")))
    assert m == {"paths": {"uuid-a": f}, "alpha": {"a.png": [True, 0.5]}} and calls == [f]


def test_crowd_summary_counts_stand_ins():
    others = [{"group": "avatar:a", "name": "Ann", "placeholder": False},
              {"group": "avatar:b", "name": "Bob", "placeholder": True, "dressed": "partial"},
              {"group": "avatar:c", "placeholder": True}]
    assert look.crowd_summary(others) == {"complete": 1, "stand_in": 2, "stand_in_names": ["Bob", "avatar:c"]}
    assert look.crowd_summary([]) == {"complete": 0, "stand_in": 0, "stand_in_names": []}


def test_export_stages_parses_client_substages():
    t = "prims=1.2(20696),bakes=3.4(260 wanted, 200 cached, 60 fetched, 0 failed),materials=0.1(3916 ids, 3916 cached, 0 fetched),env=0.0,write=0.6"
    assert look.export_stages(t) == {"prims": 1.2, "bakes": 3.4, "materials": 0.1, "env": 0.0, "write": 0.6}
    assert look.export_stages("") == {} and look.export_stages(None) == {}


if __name__ == "__main__":
    for k, v in list(globals().items()):
        if k.startswith("test_"): v(); print("ok", k)
