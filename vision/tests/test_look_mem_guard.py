"""2026-10-14: Blender memory guard (box OOM stalled the SL client and lost David's chat)."""
import os
import sys
import types

sys.modules.setdefault("handler", types.ModuleType("handler"))
sys.modules.setdefault("make_job", types.ModuleType("make_job"))
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)
import look  # noqa: E402


def test_mem_available_parse():
    assert look.mem_available_mb("MemTotal: 16386048 kB\nMemAvailable:    2048000 kB\n") == 2000
    assert look.mem_available_mb("MemTotal: 1 kB\n") is None


def test_trip_rule():
    assert look.mem_guard_trip(1000, 1500)
    assert not look.mem_guard_trip(2000, 1500)
    assert not look.mem_guard_trip(None, 1500)
    assert not look.mem_guard_trip(10, 0)


def test_runs_normally_and_captures():
    r = look.run_mem_guarded([sys.executable, "-c", "import sys; print('GT: ok'); print('e', file=sys.stderr)"], dict(os.environ), floor_mb=0)
    assert r.returncode == 0 and "GT: ok" in r.stdout and "e" in r.stderr


def test_kills_when_tripped():
    # floor far above any box's MemAvailable -> killed on the first poll, -9 takes retry_env's lighter path
    r = look.run_mem_guarded([sys.executable, "-c", "import time; time.sleep(30)"], dict(os.environ), floor_mb=10**9, poll_s=0.05)
    assert r.returncode == -9 and "memory guard" in r.stderr
    assert look.retry_env(r.returncode, True)["GT_TEX_LIMIT"] == "1024"
