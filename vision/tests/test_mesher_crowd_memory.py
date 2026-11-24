"""2026-10-18: the crowd look (Warehouse 21, ~90 avatars / 10-28 k prims) died with OutOfMemoryException in scene-mesher.
Pure retry logic only. 2026-11-23: the real-mesher crowd case was removed: it needs a ~90-avatar export (no small fixture can
exercise a 1 GB heap cap) and the export-20261018-* it looked for is gone, so it had been skipping on every run."""
import os
import subprocess
import sys
import types


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
