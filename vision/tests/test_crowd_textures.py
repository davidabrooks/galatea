"""Crowd looks (Warehouse 21, ~55 avatars): other avatars' texture size by distance, and which textures go first
when a crowd is still over GT_MAX_TEXTURES (no network, no Blender, no Pillow)."""
import importlib.util
import os
import sys
import types

if "PIL" not in sys.modules:
    try:
        import PIL.Image  # noqa: F401
    except ImportError:
        pil = types.ModuleType("PIL"); pil.Image = None; sys.modules["PIL"] = pil
# by path, under its own name: test_look_mesher_args stubs sys.modules["handler"] for look.py
_path = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "cloud-render", "handler.py")
_spec = importlib.util.spec_from_file_location("cloud_render_handler", _path)
handler = importlib.util.module_from_spec(_spec); _spec.loader.exec_module(handler)

T = "00000000-0000-0000-0000-0000000000%02d"


def meta():
    return {"me": [0, 0, 0],
            "others": [{"group": "avatar:aaaaaaaa", "pos": [3, 4, 0]},     # 5 m
                       {"group": "avatar:bbbbbbbb", "pos": [0, 20, 0]},    # 20 m
                       {"group": "avatar:cccccccc"}],                      # no position
            "batches": [{"group": "avatar", "tex": T % 1},                 # hers
                        {"group": "scene", "tex": T % 2},
                        {"group": "avatar:aaaaaaaa", "tex": T % 3},
                        {"group": "avatar:bbbbbbbb", "tex": T % 4, "mat": {"normal": T % 5}},
                        {"group": "avatar:bbbbbbbb", "tex": T % 3},        # shared with the near avatar
                        {"group": "avatar:cccccccc", "tex": T % 6}]}


def test_crowd_distances_only_positioned_others():
    d = handler.crowd_distances(meta())
    assert d == {"avatar:aaaaaaaa": 5.0, "avatar:bbbbbbbb": 20.0}
    assert handler.crowd_distances({"others": [{"group": "avatar:x", "pos": [1, 1, 1]}]}) == {}  # no "me"


def test_crowd_cap_by_distance():
    assert handler.crowd_cap(None, 512) == 512
    assert handler.crowd_cap(5.9, 512) == 512
    assert handler.crowd_cap(6.0, 512) == 256
    assert handler.crowd_cap(14.9, 1024) == 256
    assert handler.crowd_cap(15.0, 512) == 128
    assert handler.crowd_cap(30, 64) == 64  # never upsized


def test_crowd_drop_order_farthest_first_never_hers_or_scene():
    m = meta(); want = {T % i: 1 for i in range(1, 7)}
    order = handler.crowd_drop_order(m, handler.crowd_distances(m), want)
    assert order[:2] == [T % 4, T % 5]   # 20 m avatar's own textures first
    assert order[2] == T % 3             # shared texture ranks by its nearest wearer (5 m)
    assert T % 1 not in order and T % 2 not in order  # hers, scene
    assert T % 6 not in order            # wearer without a position: kept
