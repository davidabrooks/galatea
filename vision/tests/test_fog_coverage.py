"""2026-10-21: the compositor fogs only the near surface's share of a pixel (no fog-coloured outline round blended hair against the sky)."""
import os
SRC = open(os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "cloud-render", "render_mesh.py")).read()


def test_fog_coverage_default_on_and_switchable():
    assert 'FOG_COVERAGE = os.environ.get("GT_FOG_COVERAGE", "1") == "1"' in SRC
    assert 'if LAYER != "batch" and FOG_COVERAGE:' in SRC


def test_coverage_math():
    # pixel = a near surface (mist m_n) covering share a, the rest open sky (mist 1): mist = a*m_n + (1-a)
    for a, mn in [(0.05, 0.001), (0.5, 0.001), (1.0, 0.3)]:
        mist = a * mn + (1 - a); cov = min(1.0, (1 - mist) / max(1 - mn, 1e-4))
        assert abs(cov - a) < 1e-6


def test_cut_out_avatar_parts_cast_no_shadow_by_default():
    assert 'os.environ.get("GT_MASK_HAIR_SHADOW", "0") != "1"' in SRC
