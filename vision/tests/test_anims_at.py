"""2026-10-15: the client writes [anim] lines to anim-events.log (not textclient.log); anims_at.py must replay both."""
import json, os, subprocess, sys
HERE = os.path.dirname(os.path.abspath(__file__)); TOOL = os.path.join(HERE, "..", "scene-mesher", "anims_at.py")
A, B, C = "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222", "33333333-3333-3333-3333-333333333333"

def run(tmp_path, log_lines, events_lines, at="2026-10-15T12:00:30"):
    (tmp_path / "textclient.log").write_text("".join(l + "\n" for l in log_lines))
    if events_lines is not None: (tmp_path / "anim-events.log").write_text("".join(l + "\n" for l in events_lines))
    (tmp_path / "scene.json").write_text(json.dumps({"exported_at": at}))
    return subprocess.run([sys.executable, TOOL, str(tmp_path / "textclient.log"), str(tmp_path / "scene.json")], capture_output=True, text=True, check=True).stdout.strip()

def test_replays_anim_events_file_beside_the_log(tmp_path):
    out = run(tmp_path, ["2026-10-15 11:59:00 [login] OK as x", "2026-10-15 11:59:10 [chat] hello"],
              [f"2026-10-15 12:00:00 [anim] start {A} seq 5 from att @HUDCenter", f"2026-10-15 12:00:10 [anim] RESTART {B} seq 7 from object 'seat'",
               f"2026-10-15 12:00:20 [anim] stop {A} (was from att @HUDCenter)", "2026-10-15 12:00:21 [anim] start STAND seq 9 from none"])
    assert out == f"{B}@20.0"

def test_old_inline_anim_lines_still_work_and_merge_by_time(tmp_path):
    out = run(tmp_path, [f"2026-10-15 11:00:00 [anim] start {C} seq 1 from none", "2026-10-15 11:30:00 [login] OK as x", f"2026-10-15 11:31:00 [anim] start {A} seq 2 from none"],
              [f"2026-10-15 12:00:00 [anim] start {B} seq 3 from none"])
    assert out == f"{A}@1770.0,{B}@30.0"

def test_no_events_file_is_fine(tmp_path):
    assert run(tmp_path, [f"2026-10-15 12:00:00 [anim] start {A} seq 1 from none"], None) == f"{A}@30.0"
