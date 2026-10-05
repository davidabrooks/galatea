"""PR #29/#33/#37: look.py mesher args — near scene vs --far (no SL login, no Blender)."""
import os
import sys
import types

# look.py imports handler/make_job at import time; stub them for unit tests.
sys.modules.setdefault("handler", types.ModuleType("handler"))
sys.modules.setdefault("make_job", types.ModuleType("make_job"))

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)
import look  # noqa: E402


def test_pr29_near_scene_default_roots32():
    me = [1, 2, 3]
    assert look.mesher_args("d", "around", me, False) == ["d", "12", "all", "1,2,3", "30", "--roots=32"]


def test_pr29_far_flag_uses_96():
    me = [1.0, 2.0, 3.0]
    args = look.mesher_args("d", "view", me, True)
    assert args[-2:] == ["96", "--far=30"]


def test_pr33_self_mode_avatar_only():
    me = [0, 0, 0]
    assert look.mesher_args("d", "self", me, True) == ["d", "12", "avatar"]
