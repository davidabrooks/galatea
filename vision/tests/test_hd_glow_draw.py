"""2026-10-13 graphics audit: glow/bloom (Firestorm Glow, strength = iterations) and draw distance per profile."""
import os, sys, types
import numpy as np
HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE); sys.path.insert(0, os.path.join(HERE, "cloud-render"))
for _m in ("handler", "make_job"):
    sys.modules.setdefault(_m, types.ModuleType(_m))
import look  # noqa: E402
import hdr_post  # noqa: E402


def test_gauss_blur_keeps_energy_and_spreads():
    g = np.zeros((61, 121, 3)); g[30, 60] = 1.0
    b = hdr_post.gauss_blur(g, 4)
    assert abs(b.sum() - 3.0) < 1e-6 and b[30, 60, 0] > b[30, 66, 0] > 0


def test_bloom_scales_with_iterations_and_is_zero_without_glow():
    g = np.zeros((40, 80, 3)); g[20, 40] = 1.0
    assert hdr_post.bloom(np.zeros_like(g)).sum() == 0
    assert hdr_post.bloom(g, 0).sum() == 0
    assert hdr_post.bloom(g, 4).sum() > hdr_post.bloom(g, 2).sum() > 0
    s = hdr_post.settings_from_env({"GT_GLOW_ITERATIONS": "2"}); assert s["glow_iterations"] == 2 and s["glow_strength"] == 0.325


def test_profiles_carry_glow_and_mesher_uses_export_radius():
    assert look.hd_settings("", "view")[1]["GT_GLOW_ITERATIONS"] == "2"
    assert look.mesher_args("d", "view", (1, 2, 3), True, 128)[4] == "128"
    assert look.mesher_args("d", "view", (1, 2, 3), True)[4] == "96"
