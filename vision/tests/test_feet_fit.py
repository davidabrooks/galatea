"""2026-10-28: feet_fit.py only says whether a `look self feet` close-up is usable (near-black = invalid, retake). It has no skin check: wood heels and
brown suede counted as skin, so it never judges the fit (the body's foot mesh is the proof, see FeetFitTests.cs)."""
import os, sys
import numpy as np
from PIL import Image
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import feet_fit

W, H, LINE = 400, 250, 220
SKIN, BOOT, FLOOR, RED, WOOD = (235, 180, 140), (40, 30, 28), (190, 190, 195), (255, 80, 80), (150, 100, 60)

def make(tmp_path, name="a.jpg", bg=FLOOR, dark=False, patch=None):
    a = np.zeros((H, W, 3), np.uint8); a[:] = bg
    if dark: a[:] = (4, 3, 3)
    if patch and not dark: y0, y1, x0, x1, col = patch; a[y0:y1, x0:x1] = col
    a[LINE - 1:LINE + 2, :] = RED
    p = tmp_path / name; Image.fromarray(a).save(p, quality=100); return str(p)

def test_near_black_render_is_invalid(tmp_path):
    r = feet_fit.analyze(make(tmp_path, dark=True))
    assert r["ok"] is False and "near-black" in r["why"] and "retake" in r["why"]

def test_unreadable_file_is_invalid(tmp_path):
    p = tmp_path / "x.jpg"; p.write_text("not an image")
    assert feet_fit.analyze(str(p))["ok"] is False

def test_a_lit_picture_is_usable_and_finds_the_floor_line(tmp_path):
    r = feet_fit.analyze(make(tmp_path))
    assert r["ok"] and r["line_row"] is not None

def test_no_skin_verdict_exists_wood_or_skin_coloured_patches_do_not_change_the_result(tmp_path):
    base = feet_fit.analyze(make(tmp_path, name="b.jpg"))
    wood = feet_fit.analyze(make(tmp_path, name="w.jpg", patch=(190, 218, 120, 300, WOOD)))   # a wood heel / suede boot
    skin = feet_fit.analyze(make(tmp_path, name="s.jpg", patch=(190, 218, 120, 300, SKIN)))
    for r in (wood, skin):
        assert r["ok"] and "skin_foot_frac" not in r and "skin_foot_frac" not in base
