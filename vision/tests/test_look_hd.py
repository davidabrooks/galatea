"""`photo hd` (look.py --hd) settings and the offline texture fallback (no Blender, no network, no Pillow)."""
import importlib.util, os, sys, types, pytest
if "PIL" not in sys.modules:
    try:
        import PIL.Image  # noqa: F401
    except ImportError:
        pil = types.ModuleType("PIL"); pil.Image = None; sys.modules["PIL"] = pil
sys.modules.setdefault("handler", types.ModuleType("handler"))
sys.modules.setdefault("make_job", types.ModuleType("make_job"))
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)
import look  # noqa: E402
_spec = importlib.util.spec_from_file_location("cloud_render_handler_hd", os.path.join(ROOT, "cloud-render", "handler.py"))
handler = importlib.util.module_from_spec(_spec); _spec.loader.exec_module(handler)

def test_no_hd_leaves_quick_look_alone():
    assert look.hd_settings(None, "self") == (None, {})

def test_self_defaults_portrait_scene_landscape():
    o, env = look.hd_settings("", "self"); assert o == "portrait" and env["GT_RES"] == "2160x3840"
    o, env = look.hd_settings("", "view"); assert o == "landscape" and env["GT_RES"] == "3840x2160"
    assert env["GT_HD"] == "1" and env["GT_DENOISE"] == "1" and int(env["GT_TEX_CAP_AVATAR"]) >= 4096

def test_explicit_and_bad_orientation():
    assert look.hd_settings("landscape", "self")[0] == "landscape"
    with pytest.raises(ValueError): look.hd_settings("square", "self")

def test_cached_fallback_picks_largest(tmp_path):
    u = "8c08ee8c-a5a0-0424-7708-688d5aaf351e"
    for c in (256, 1024, 512): (tmp_path / f"{u}.{c}.png").write_bytes(b"x")
    assert handler.cached_fallback(u, str(tmp_path)).endswith(".1024.png")
    assert handler.cached_fallback("nope", str(tmp_path)) is None

def test_hd_scene_samples_default():
    assert look.hd_settings("", "self")[1]["GT_SAMPLES"] == os.environ.get("GT_HD_SAMPLES", "24")

def test_hd_self_renders_the_scene_not_the_studio():
    src = open(os.path.join(ROOT, "look.py")).read()
    assert 'photo_self = bool(hd) and mode == "self"' in src and 'view = "portrait" if photo_self' in src
    assert 'if mode == "self" and not hd else ["scene"]' in src
    rm = open(os.path.join(ROOT, "cloud-render", "render_mesh.py")).read()
    assert 'if view.startswith("portrait"):' in rm
