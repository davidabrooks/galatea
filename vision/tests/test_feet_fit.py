"""2026-10-28: feet_fit.py judges the FOOT in a `look self feet` close-up: near-black = invalid (retake), skin-coloured pixels in the foot zone = bare foot."""
import os, sys
import numpy as np
from PIL import Image
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import feet_fit

W, H, LINE = 400, 250, 220
SKIN, BOOT, FLOOR, RED = (235, 180, 140), (40, 30, 28), (190, 190, 195), (255, 80, 80)

def make(tmp_path, name="a.jpg", bg=FLOOR, boot=True, skin_box=None, dark=False):
    a = np.zeros((H, W, 3), np.uint8); a[:] = bg
    if dark: a[:] = (4, 3, 3)
    if boot and not dark: a[150:LINE, 120:220] = BOOT
    if skin_box and not dark: y0, y1, x0, x1 = skin_box; a[y0:y1, x0:x1] = SKIN
    a[LINE - 1:LINE + 2, :] = RED
    p = tmp_path / name; Image.fromarray(a).save(p, quality=100); return str(p)

def test_near_black_render_is_invalid_not_judged(tmp_path):
    r = feet_fit.analyze(make(tmp_path, dark=True))
    assert r["ok"] is False and "near-black" in r["why"] and "retake" in r["why"]

def test_unreadable_file_is_invalid(tmp_path):
    p = tmp_path / "x.jpg"; p.write_text("not an image")
    assert feet_fit.analyze(str(p))["ok"] is False

def test_boots_only_has_no_skin_in_the_foot_zone(tmp_path):
    r = feet_fit.analyze(make(tmp_path))
    assert r["ok"] and r["skin_foot_frac"] < 0.004 and r["line_row"] is not None

def test_bare_toes_beside_the_boot_are_counted(tmp_path):
    r = feet_fit.analyze(make(tmp_path, skin_box=(190, 215, 220, 270)))   # toes poking out in front of the shoe
    assert r["ok"] and r["skin_foot_frac"] > 0.004

def test_flat_bare_foot_beside_heeled_boot_is_counted(tmp_path):
    r = feet_fit.analyze(make(tmp_path, skin_box=(200, 218, 260, 380)))
    assert r["ok"] and r["skin_foot_frac"] > 0.004

def test_calf_skin_above_the_boot_top_is_not_the_foot(tmp_path):
    r = feet_fit.analyze(make(tmp_path, skin_box=(0, 100, 130, 210)))   # skin well above the foot zone
    assert r["ok"] and r["skin_foot_frac"] < 0.004
