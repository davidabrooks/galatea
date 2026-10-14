"""2026-10-13: David's Firestorm High+ (default for photo hd) and Firestorm Ultra profiles; night sky as displayed;
seated avatars at the sim's placement (Reiley Net Chair: him on her lap in the render)."""
import os, sys, types
import numpy as np
import pytest
HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE); sys.path.insert(0, os.path.join(HERE, "cloud-render"))
for _m in ("handler", "make_job"):
    sys.modules.setdefault(_m, types.ModuleType(_m))
import look  # noqa: E402
import hdr_post  # noqa: E402
SRC = open(os.path.join(HERE, "cloud-render", "render_mesh.py")).read()
ns = {}
exec(SRC[SRC.index("def srgb_to_linear"):SRC.index("def terrain_layers")], ns)


def test_highplus_default_and_ultra():
    _, e = look.hd_settings("", "self")
    assert e["GT_HD_PROFILE"] == "highplus" and e["GT_TONEMAP_MIX"] == "0.0" and e["GT_TEX_CAP_SCENE"] == "2048" and e["GT_SHARPEN"] == "0.4"
    _, u = look.hd_settings("", "self", "ultra")
    assert u["GT_TONEMAP_MIX"] == "0.7"
    with pytest.raises(ValueError): look.hd_settings("", "self", "low")


def test_settings_from_env_and_sharpen():
    s = hdr_post.settings_from_env({"GT_TONEMAP_MIX": "0.0", "GT_SHARPEN": "0.4"})
    assert s["tonemap_mix"] == 0.0 and s["sharpen"] == 0.4 and s["exposure"] == 1.0
    img = np.full((5, 5, 3), 0.4); img[2, 2] = 0.6
    out = hdr_post.cas_sharpen(img, 0.4)
    assert out[2, 2, 0] > 0.6 and np.allclose(out[0, 0], 0.4)
    assert hdr_post.cas_sharpen(img, 0.0) is img


def test_night_sky_display_values_to_linear():
    assert abs(ns["srgb_to_linear"](0.0589) - 0.00478) < 2e-4 and ns["srgb_to_linear"](None) == 0.0


def test_seated_uses_sim_placement():
    z0 = ns["z0_of"]
    assert z0(28.64, 27.85, 0.56, True) == 27.85          # seated: sim's placement, no snap to the seat cushion
    assert abs(z0(28.64, 28.2, 0.4) - 28.24) < 1e-9       # standing within 0.35 m: lowest vertex on the floor
