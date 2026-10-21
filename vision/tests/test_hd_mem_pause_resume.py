import os, sys, types
sys.modules.setdefault("handler", types.ModuleType("handler")); sys.modules.setdefault("make_job", types.ModuleType("make_job"))
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import look

def test_pause_then_resume_then_kill():
    a = look.mem_pause_action
    assert a(3000, 1500, 750, False, 0, 1200, 500) is None
    assert a(1400, 1500, 750, False, 0, 1200, 500) == "pause"
    assert a(1700, 1500, 750, True, 10, 1200, 500) is None        # not enough margin yet
    assert a(2100, 1500, 750, True, 10, 1200, 500) == "resume"
    assert a(700, 1500, 750, True, 10, 1200, 500) == "kill"       # hard floor
    assert a(1400, 1500, 750, True, 1300, 1200, 500) == "kill"    # paused too long

def test_resume_key_ignores_memory_knobs():
    meta = {"batches": [{"group": "scene", "nv": 5, "tex": "t"}], "env": {"a": 1}}
    k = look.layer_resume_key(meta, {"GT_RES": "3840x2160", "GT_TEX_BIAS": "4"})
    assert k == look.layer_resume_key(meta, {"GT_RES": "3840x2160", "GT_TEX_BIAS": "6", "GT_HD_BANDS": "6", "GT_LAYER": "base"})
    assert k != look.layer_resume_key(meta, {"GT_RES": "960x540"})
