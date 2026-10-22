import os
SRC = open(os.path.join(os.path.dirname(__file__), "..", "cloud-render", "render_mesh.py")).read()

def test_avatar_key_light_is_hd_indoor_and_avatar_only():
    assert 'AVATAR_KEY = float(os.environ.get("GT_AVATAR_KEY", "18"))' in SRC
    assert "if not HD or not sc.camera: return" in SRC and "if roof: k = AVATAR_KEY" in SRC and "elif headshot: k = HEADSHOT_KEY" in SRC
    assert "o.light_linking.receiver_collection = coll; o.light_linking.blocker_collection = coll" in SRC
    assert 'if group.startswith("avatar"): ob["gt_avatar"] = 1' in SRC
    assert "    avatar_key_light()\n" in SRC
