"""2026-10-13: avatars are invisible to glossy rays (SL reflections are probes/sky, never avatars); source check, no Blender."""
import os, re
SRC = open(os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "cloud-render", "render_mesh.py")).read()

def test_avatars_hidden_from_reflections_by_default():
    assert re.search(r'if group\.startswith\("avatar"\) and not SELF_REFLECT: ob\.visible_glossy = False', SRC)
    assert 'SELF_REFLECT = os.environ.get("GT_SELF_REFLECT") == "1"' in SRC

def test_hd_no_longer_forces_shadow_off_path():   # shadow default untouched (PR #152): only GT_STUDIO_SHADOW=1 enables it
    assert 'L.data.use_shadow = os.environ.get("GT_STUDIO_SHADOW", "0") == "1"' in SRC
