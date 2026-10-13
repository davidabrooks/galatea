"""Boot autostart honours run/deliberate-stop only once the box-store copy-in has restored it (2026-10-13: the 11:45 PT
reboot re-hydrated /home/box for ~7 min; the autostart ran at 11:49:41, before the marker was copied back, and logged her
in despite a deliberate stop). GT_AUTOSTART_TEST_STOP makes the script report its decision and exit before any start."""
import json, os, pathlib, subprocess, threading, time

SCRIPT = pathlib.Path(__file__).resolve().parents[1] / "galatay-autostart.sh"

def run(tmp, status=None, marker=False, extra=None, write_later=None):
    run_dir = tmp / "run"; run_dir.mkdir(exist_ok=True)
    if marker: (run_dir / "deliberate-stop").write_text("stopped deliberately (test)\n")
    hyd = tmp / "copy-in.json"
    if status is not None: hyd.write_text(json.dumps(status))
    env = {**os.environ, "GT_AUTOSTART_TEST_STOP": "1", "GT_RUN": str(run_dir), "GT_COPYIN_STATUS": str(hyd),
           "GT_AUTOSTART_LOG": str(tmp / "a.log"), "GT_AUTOSTART_GTLOG": str(tmp / "t.log"),
           "GT_AUTOSTART_STAMP": str(tmp / "stamp"), "GT_AUTOSTART_LOCK": str(tmp / "lock"), "GT_AUTOSTART_POLL_S": "0.2",
           **(extra or {})}
    if write_later:
        def later():
            time.sleep(1.0); write_later(run_dir, hyd)
        threading.Thread(target=later).start()
    r = subprocess.run(["bash", str(SCRIPT)], env=env, capture_output=True, text=True, timeout=60)
    return r, (tmp / "a.log").read_text() if (tmp / "a.log").exists() else ""

def test_marker_present_after_copy_in_blocks(tmp_path):
    r, _ = run(tmp_path, {"phase": "done"}, marker=True)
    assert r.returncode == 0 and "deliberate-stop marker present" in r.stdout

def test_no_marker_would_start(tmp_path):
    r, _ = run(tmp_path, {"phase": "done"})
    assert r.returncode == 1   # no down reason: the real run goes on to start her

def test_waits_for_copy_in_then_sees_restored_marker(tmp_path):
    # copy-in still running; the marker and "done" arrive 1 s later, as on the 11:45 reboot
    def finish(run_dir, hyd):
        (run_dir / "deliberate-stop").write_text("stopped deliberately (restored)\n"); hyd.write_text('{"phase":"done"}')
    r, log = run(tmp_path, {"phase": "copying"}, write_later=finish)
    assert r.returncode == 0 and "deliberate-stop marker present" in r.stdout
    assert "waiting for the box-store copy-in" in log

def test_copy_in_never_finishes_does_not_start(tmp_path):
    r, _ = run(tmp_path, {"phase": "copying"}, extra={"GT_AUTOSTART_HYDRATE_S": "1"})
    assert r.returncode == 0 and "copy-in not finished" in (tmp_path / "t.log").read_text()
    assert "copy-in unfinished" in (tmp_path / "stamp").read_text()

def test_supervisor_relaunch_checks_marker():
    src = (SCRIPT.parent / "text-galatay.sh").read_text()
    assert 'deliberate-stop marker present: not relaunching' in src and 'deliberate-stop marker set during backoff' in src
