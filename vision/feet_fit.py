"""feet_fit.py <feet close-up .jpg>: does the FOOT fit the shoe? (2026-10-28, David: judge the foot, not the boot.)
Reads one `look self feet` close-up (the soles framed over the red floor reference line) and prints ONE json line:
  {"ok": bool, "mean_luma": float, "p95_luma": float, "skin_foot_frac": float, "zone_px": int, "line_row": int|null, "why": str}
ok=false means the picture is unusable (near-black, unreadable): the caller must RETAKE it (more exposure), never judge from it.
skin_foot_frac = share of the foot zone (the ~0.09 m band just above the floor line, where soles / toes / heels are) that is skin-coloured:
bare foot or toes outside a heeled shoe, or a flat foot beside heeled boots. Skin-coloured shapes near the shoe are NEVER explained away as
"inside the opening": any real share counts.
ponytail: colour heuristic for human skin (hue 5-40 deg, sat 0.12-0.65, value >= 0.35; the red reference line and pale floors fall outside it);
thresholds are uncalibrated against David's eye; ceiling = a wood floor in the same hue band, or a non-skin (android) body, which this cannot judge."""
import json, sys
import numpy as np
from PIL import Image

MIN_MEAN_LUMA, MIN_P95_LUMA = 0.10, 0.25   # below this the render is near-black: invalid
ZONE_FRAC_OF_HEIGHT = 0.25                 # the foot zone: this share of the frame height, ending at the floor line

def analyze(path):
    try:
        im = Image.open(path).convert("RGB")
    except Exception as e:
        return {"ok": False, "mean_luma": 0.0, "p95_luma": 0.0, "skin_foot_frac": 0.0, "zone_px": 0, "line_row": None, "why": f"unreadable image: {e}"}
    rgb = np.asarray(im, dtype=np.float32) / 255.0
    h, w, _ = rgb.shape
    luma = 0.2126 * rgb[..., 0] + 0.7152 * rgb[..., 1] + 0.0722 * rgb[..., 2]
    hsv = np.asarray(im.convert("HSV"), dtype=np.float32)
    hue, sat, val = hsv[..., 0] * 360.0 / 255.0, hsv[..., 1] / 255.0, hsv[..., 2] / 255.0
    # the floor reference line: bright saturated red, a thin full-width band
    redline = (rgb[..., 0] > 0.8) & (rgb[..., 1] < 0.45) & (rgb[..., 2] < 0.45)
    rows = np.where(redline.sum(axis=1) > w * 0.25)[0]
    line_row = int(rows.max()) if rows.size else None
    keep = ~redline
    mean_luma = float(luma[keep].mean()) if keep.any() else 0.0
    p95 = float(np.percentile(luma[keep], 95)) if keep.any() else 0.0
    if mean_luma < MIN_MEAN_LUMA or p95 < MIN_P95_LUMA:
        return {"ok": False, "mean_luma": mean_luma, "p95_luma": p95, "skin_foot_frac": 0.0, "zone_px": 0, "line_row": line_row,
                "why": f"near-black render (mean luma {mean_luma:.3f}, p95 {p95:.3f}): invalid, retake with more exposure"}
    bottom = line_row if line_row is not None else int(h * 0.85)
    top = max(0, bottom - int(h * ZONE_FRAC_OF_HEIGHT))
    skin = (hue >= 5) & (hue <= 40) & (sat >= 0.12) & (sat <= 0.65) & (val >= 0.35) & keep
    zone = skin[top:bottom + 1]
    n = int(zone.size)
    frac = float(zone.sum()) / n if n else 0.0
    return {"ok": True, "mean_luma": mean_luma, "p95_luma": p95, "skin_foot_frac": frac, "zone_px": n, "line_row": line_row, "why": "ok"}

if __name__ == "__main__":
    print(json.dumps(analyze(sys.argv[1])))
