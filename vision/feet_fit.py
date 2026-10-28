"""feet_fit.py <feet close-up .jpg>: is the `look self feet` close-up usable as a picture? (2026-10-28)
Prints ONE json line: {"ok": bool, "mean_luma": float, "p95_luma": float, "line_row": int|null, "why": str}
ok=false means the picture is unusable (near-black, unreadable): the caller retakes it with more exposure.
This is INFORMATIONAL only. It never says whether the foot fits the shoe: the old skin-hue check counted wood heels and brown suede as skin
(and cannot judge a non-skin body), so it is gone. The proof of the foot pose is the worn body's own foot mesh pair (b46/47 = f1 .. b54/55 = f5,
alpha > 0), read by the client (FeetAuto.FeetShownFromMesh)."""
import json, sys
import numpy as np
from PIL import Image

MIN_MEAN_LUMA, MIN_P95_LUMA = 0.10, 0.25   # below this the render is near-black: invalid

def analyze(path):
    try:
        im = Image.open(path).convert("RGB")
    except Exception as e:
        return {"ok": False, "mean_luma": 0.0, "p95_luma": 0.0, "line_row": None, "why": f"unreadable image: {e}"}
    rgb = np.asarray(im, dtype=np.float32) / 255.0
    h, w, _ = rgb.shape
    luma = 0.2126 * rgb[..., 0] + 0.7152 * rgb[..., 1] + 0.0722 * rgb[..., 2]
    # the floor reference line: bright saturated red, a thin full-width band (left out of the brightness figures)
    redline = (rgb[..., 0] > 0.8) & (rgb[..., 1] < 0.45) & (rgb[..., 2] < 0.45)
    rows = np.where(redline.sum(axis=1) > w * 0.25)[0]
    line_row = int(rows.max()) if rows.size else None
    keep = ~redline
    mean_luma = float(luma[keep].mean()) if keep.any() else 0.0
    p95 = float(np.percentile(luma[keep], 95)) if keep.any() else 0.0
    if mean_luma < MIN_MEAN_LUMA or p95 < MIN_P95_LUMA:
        return {"ok": False, "mean_luma": mean_luma, "p95_luma": p95, "line_row": line_row,
                "why": f"near-black render (mean luma {mean_luma:.3f}, p95 {p95:.3f}): invalid, retake with more exposure"}
    return {"ok": True, "mean_luma": mean_luma, "p95_luma": p95, "line_row": line_row, "why": "ok"}

if __name__ == "__main__":
    print(json.dumps(analyze(sys.argv[1])))
