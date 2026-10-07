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


def test_mesher_error_shows_exception_line_not_only_foreach_frames():
    trace = ("Unhandled exception. System.AggregateException: One or more errors occurred. "
             "(Index was outside the bounds of the array.)\n"
             " ---> System.IndexOutOfRangeException: Index was outside the bounds of the array.\n"
             "   at Mesher.<>c__DisplayClass.<Main>b__0(OSDMap o)\n" + "   at System.Threading.Tasks.Parallel.ForEach frame\n" * 20)
    msg = look.mesher_error(trace)
    assert msg.startswith("Unhandled exception. System.AggregateException")
    assert "Parallel.ForEach" in msg  # tail still included
    assert look.mesher_error("plain failure") == "plain failure"


def test_mesher_gets_its_own_heap_cap_not_the_clients_512mb():
    env = look.mesher_env({"DOTNET_GCHeapHardLimit": "0x20000000", "PATH": "/bin"})
    assert int(env["DOTNET_GCHeapHardLimit"], 16) >= 2 * 1024 ** 3
    assert env["PATH"] == "/bin" and env["DOTNET_gcServer"] == "0"


def test_body_views_all_list_and_degrees():
    import importlib, sys, os
    sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    look = importlib.import_module("look")
    assert look.body_views("") == ""
    assert look.body_views("all") == "front;back;left;right"
    assert look.body_views("back, left,back") == "back;left"
    assert look.body_views("135,-45deg,90.0") == "135;-45;90"
    for bad in ("top", "400", "nan", "back;left"):
        try:
            look.body_views(bad); assert False, bad
        except ValueError:
            pass
