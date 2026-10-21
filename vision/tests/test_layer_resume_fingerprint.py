import os, sys, types
sys.modules.setdefault("handler", types.ModuleType("handler")); sys.modules.setdefault("make_job", types.ModuleType("make_job"))
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import look

META = {"batches": [{"group": "scene", "nv": 5, "tex": "t"}], "env": {"a": 1}}

def _code(tmp_path, key_default):
    f = tmp_path / "render_mesh.py"
    f.write_text(f'AVATAR_KEY = float(os.environ.get("GT_AVATAR_KEY", "{key_default}"))\n'); return [str(f)]

def test_changed_code_default_invalidates(tmp_path):
    a = look.layer_resume_key(META, {"GT_RES": "3840x2160"}, _code(tmp_path, "25"))
    b = look.layer_resume_key(META, {"GT_RES": "3840x2160"}, _code(tmp_path, "18"))
    assert a != b
    assert look.effective_settings({}, _code(tmp_path, "18"))["GT_AVATAR_KEY"] == "18"

def test_unchanged_settings_still_resume(tmp_path):
    files = _code(tmp_path, "18")
    a = look.layer_resume_key(META, {"GT_RES": "3840x2160", "GT_TEX_BIAS": "4"}, files)
    assert a == look.layer_resume_key(META, {"GT_RES": "3840x2160", "GT_TEX_BIAS": "6", "GT_HD_BANDS": "6", "GT_LAYER": "base"}, files)
    assert a == look.layer_resume_key(META, {"GT_RES": "3840x2160"}, files)   # real repo files: deterministic too
    assert look.layer_resume_key(META, {}) == look.layer_resume_key(META, {})

def test_code_change_without_default_change_invalidates(tmp_path):
    files = _code(tmp_path, "18"); a = look.layer_resume_key(META, {}, files)
    with open(files[0], "a") as f: f.write("# tweak\n")
    assert a != look.layer_resume_key(META, {}, files)

def test_env_override_equals_code_default(tmp_path):
    files = _code(tmp_path, "18")
    assert look.layer_resume_key(META, {}, files) == look.layer_resume_key(META, {"GT_AVATAR_KEY": "18"}, files)
    assert look.layer_resume_key(META, {}, files) != look.layer_resume_key(META, {"GT_AVATAR_KEY": "25"}, files)

def test_real_renderer_defaults_are_seen():
    eff = look.effective_settings({})
    assert eff.get("GT_AVATAR_KEY") == "18" and "GT_INDOOR_LIGHT" in eff
