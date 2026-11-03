"""Exposure sanity of a seat-adjust close-up (2026-11-02 tub 'shave': an overexposed close-up of thighs and torso, no tub, no floor, was nudged and SAVED).
`python3 render_quality.py <jpg>` prints "GT: render quality: overexposed 0.620; dark 0.010; contrast 0.180" for SeatAdjust.cs ParseQuality."""
import sys

def stats(pixels):
    """pixels: iterable of (r, g, b) 0..255 -> (overexposed share: every channel >= 245, dark share: every channel <= 12, contrast: luma stdev / 255)"""
    n = over = dark = 0; s = s2 = 0.0
    for r, g, b in pixels:
        n += 1; y = 0.2126 * r + 0.7152 * g + 0.0722 * b; s += y; s2 += y * y
        if min(r, g, b) >= 245: over += 1
        if max(r, g, b) <= 12: dark += 1
    if n == 0: return 1.0, 0.0, 0.0
    var = max(0.0, s2 / n - (s / n) ** 2)
    return over / n, dark / n, var ** 0.5 / 255

def line(o, d, c): return f"GT: render quality: overexposed {o:.3f}; dark {d:.3f}; contrast {c:.3f}"

if __name__ == "__main__":
    from PIL import Image
    im = Image.open(sys.argv[1]).convert("RGB"); im.thumbnail((160, 100))
    b = im.tobytes(); print(line(*stats(zip(b[0::3], b[1::3], b[2::3]))), flush=True)
