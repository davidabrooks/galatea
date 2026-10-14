"""HD first attempt fits a box with 6-9 GB free (2026-10-14 measured peaks), and the post stays float32."""
import os, sys, numpy as np
HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, os.path.join(HERE, "..", "cloud-render"))
import hdr_post

def _look():
    import pytest
    try:
        sys.path.insert(0, os.path.join(HERE, "..")); import look; return look
    except ImportError as e: pytest.skip(f"look deps: {e}")

def test_first_try_peak_fits_6_to_9_gb():
    look = _look()
    for avail in (6000, 7000, 8000, 9000):
        b = look.tex_budget_mb(avail, 1500)
        peak = 2800 + 0.43 * b    # measured model: overhead + slope
        assert peak < avail - 1500, (avail, b, peak)
        assert b >= 2000        # still enough for the near textures at 4K

def test_post_stays_float32():
    rgb = np.random.rand(8, 8, 3).astype(np.float32) * 3
    out, _ = hdr_post.firestorm_ldr(rgb, {"classic": True})
    assert out.dtype == np.float32
    assert hdr_post.bloom(rgb, 2, 0.325).dtype == np.float32

def test_render_mesh_frees_and_no_float64_frame():
    src = open(os.path.join(HERE, "..", "cloud-render", "render_mesh.py")).read()
    body = src[src.index("def hd_render_hdr"):src.index("views = views or [None]")]
    assert "astype(np.float64)" not in body and "bpy.data.images.remove(img)" in body

def test_bands_only_when_tight():
    look = _look()
    assert look.hd_bands(11000, 1500) == 1 and look.hd_bands(8000, 1500) == 2 and look.hd_bands(5000, 1500) == 3
