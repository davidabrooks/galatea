"""2026-10-18: the crowd look (Warehouse 21, ~90 avatars / 10-28 k prims) died with OutOfMemoryException in scene-mesher.
Pure retry logic always runs; the real-mesher crowd test is optional (needs SCENE_MESHER + a crowd export)."""
import glob
import json
import os
import shutil
import subprocess
import sys
import tempfile
import types

import pytest

sys.modules.setdefault("handler", types.ModuleType("handler"))
sys.modules.setdefault("make_job", types.ModuleType("make_job"))
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)
import look  # noqa: E402


def cp(rc, err="", out=""):
    return subprocess.CompletedProcess([], rc, out, err)


def test_oom_detection():
    assert look.mesher_oom(cp(1, "System.OutOfMemoryException: Exception of type"))
    assert look.mesher_oom(cp(134, "Out of memory."))
    assert look.mesher_oom(cp(-9))          # the kernel OOM killer
    assert not look.mesher_oom(cp(0, "warning: 2 prim(s) skipped after errors; first: OutOfMemoryException"))   # a warning only
    assert not look.mesher_oom(cp(2, "usage: scene-mesher"))


def test_retry_args_replace_avatar_max():
    assert look.mesher_retry_args(["d", "12", "avatar"]) == ["d", "12", "avatar", "--avatar-max=4"]
    assert look.mesher_retry_args(["d", "--avatar-max=16", "--lod-factor=3"]) == ["d", "--lod-factor=3", "--avatar-max=4"]


def test_run_mesher_retries_once_one_avatar_at_a_time():
    calls = []

    def run(args, env):
        calls.append((list(args), env.get("GT_MESH_AVATARS")))
        return cp(1, "Out of memory.") if len(calls) == 1 else cp(0)
    r, retried = look.run_mesher(run, ["d", "12", "avatar"], {})
    assert r.returncode == 0 and retried and len(calls) == 2
    assert calls[1] == (["d", "12", "avatar", "--avatar-max=4"], "1") and calls[0][1] is None


def test_run_mesher_no_retry_when_fine_or_other_error():
    n = []
    r, retried = look.run_mesher(lambda a, e: (n.append(1), cp(0))[1], ["d"], {})
    assert not retried and len(n) == 1
    r, retried = look.run_mesher(lambda a, e: (n.append(1), cp(2, "usage"))[1], ["d"], {})
    assert not retried and r.returncode == 2 and len(n) == 2


MESHER = os.environ.get("SCENE_MESHER", "/home/box/tools/scene-mesher/SceneMesher.dll")
DOTNET = shutil.which("dotnet") or os.path.expanduser("~/.dotnet/dotnet")
SRC = os.environ.get("CROWD_EXPORT") or next(iter(sorted(glob.glob("/workspace/secondlife/vision/export-20261018-*"), reverse=True)), "")


@pytest.mark.skipif(not os.path.isfile(MESHER) or not os.path.isfile(f"{SRC}/scene.json"), reason="SCENE_MESHER / crowd export not available")
def test_crowd_under_small_heap_cap_degrades_instead_of_failing():
    """A crowd export under a 1 GB heap cap (the crowd needed > 3 GB before the streaming fix): exit 0, every avatar still listed,
    the mesher degrades far avatars (lowest LOD / stand-in) instead of crashing."""
    with tempfile.TemporaryDirectory() as d:
        shutil.copytree(SRC, d, dirs_exist_ok=True)
        doc = json.load(open(f"{d}/scene.json"))
        if len(doc.get("avatars", [])) < 10:
            pytest.skip("export is not a crowd")
        env = dict(os.environ, DOTNET_GCHeapHardLimit="0x40000000", DOTNET_gcServer="0")
        r = subprocess.run([DOTNET, MESHER, d, "12", "avatar"], capture_output=True, text=True, env=env, timeout=900)
        assert r.returncode == 0, r.stderr[-600:]
        meta = json.load(open(f"{d}/mesh.json"))
        assert len(meta["others"]) == len([a for a in doc["avatars"] if "agent_id" in a])
        assert meta["stats"].get("avatar_lowest_lod_memory", 0) + meta["stats"].get("avatar_skipped_memory", 0) > 0   # a 1 GB cap cannot hold this crowd in full

