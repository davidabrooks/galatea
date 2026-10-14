"""photo hd (2026-10-13, David: "HD photos should use HDR"): Firestorm's post-process on a linear HDR render (pure numpy).

The render is scene-linear (Cycles -> 32-bit OpenEXR master). firestorm_ldr() reproduces the viewer's final pass:
  - pipeline.cpp LLPipeline::tonemap: exposure = RenderExposure (1.0) x the dynamic exposure (exposureF.glsl:
    s = mix(exp_max, exp_min, (clamp(L, 0, coeff) / coeff)^2), L = the frame's mean luminance, coeff =
    RenderDynamicExposureCoefficient), tonemap_type = RenderTonemapType (Firestorm default 1 = ACES Hill),
    tonemap_mix = the sky's getTonemapMix(): 0 for a legacy/"classic" sky (no reflection_probe_ambiance:
    "legacy settings do not support tonemaping", llsettingssky.cpp getTonemapMix), else RenderTonemapMix (0.7 in Firestorm).
  - exp_min / exp_max (pipeline.cpp, dynamic exposure on, RenderUseExposureSkySettings off): 1 / 1 for a classic sky;
    with probe ambiance > 0: hdr_scale = sqrt(gamma) x 2, range [1 / hdr_scale, hdr_scale] when hdr_scale > 1.
  - tonemapUtilF.glsl toneMap(): color = mix(exposed, tonemapped, mix), clamp 0..1; postDeferredTonemap.glsl:
    linear_to_srgb, then (classic skies) legacyGamma: 1 - (1 - c)^gamma.
ponytail: the exposure is settled (no eye adaptation over frames); L is the whole frame's mean, not the viewer's mip-8
centre sample."""
import numpy as np

ACES_IN = np.array([[0.59719, 0.35458, 0.04823], [0.07600, 0.90834, 0.01566], [0.02840, 0.13383, 0.83777]])
ACES_OUT = np.array([[1.60475, -0.53108, -0.07367], [-0.10208, 1.10813, -0.00605], [-0.00327, -0.07276, 1.07602]])
LUM = np.array([0.2126, 0.7152, 0.0722])
FS = {"exposure": 1.0, "tonemap_type": 1, "tonemap_mix": 0.7, "dyn_coeff": 0.5}   # Firestorm settings.xml defaults


def aces_hill(c):
    c = c @ ACES_IN.T
    a = c * (c + 0.0245786) - 0.000090537; b = c * (0.983729 * c + 0.4329510) + 0.238081
    return np.clip((a / b) @ ACES_OUT.T, 0.0, 1.0)


def khronos_neutral(c):
    c = c.copy(); start, desat = 0.8 - 0.04, 0.15
    x = c.min(axis=-1, keepdims=True); c -= np.where(x < 0.08, x - 6.25 * x * x, 0.04)
    peak = c.max(axis=-1, keepdims=True); d = 1 - start
    new = 1 - d * d / (peak + d - start); comp = peak >= start
    cc = c * np.where(comp, new / np.maximum(peak, 1e-9), 1.0)
    g = 1 - 1 / (desat * (peak - new) + 1)
    return np.where(comp, cc * (1 - g) + new * g, c)


def linear_to_srgb(c):
    c = np.clip(c, 0.0, 1.0)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * np.power(c, 1 / 2.4) - 0.055)


def exposure_range(env):
    """(exp_min, exp_max) as pipeline.cpp: classic sky 1/1; probe ambiance > 0: [1/hdr_scale, hdr_scale]"""
    if not env or env.get("classic", True) or not env.get("probe_ambiance"): return 1.0, 1.0
    h = (max(float(env.get("gamma", 1.0) or 1.0), 0.0) ** 0.5) * 2.0
    return (1.0 / h, h) if h > 1.0 else (1.0, 1.0)


def dynamic_exposure(rgb, env, coeff=FS["dyn_coeff"]):
    lo, hi = exposure_range(env)
    L = float(np.clip((rgb.reshape(-1, 3) @ LUM).mean(), 0.0, coeff)) / coeff
    return hi + (lo - hi) * L * L


def firestorm_ldr(rgb, env, exposure=1.0, settings=FS):
    """linear HDR rgb (..., 3) -> display sRGB 0..1 the way Firestorm draws it; -> (ldr, info dict)"""
    classic = not env or env.get("classic", True)
    mix = 0.0 if classic else settings["tonemap_mix"]
    s = dynamic_exposure(rgb, env)
    e = float(np.clip(settings["exposure"], 0.5, 4.0)) * s * exposure
    x = np.maximum(rgb, 0.0) * e
    tm = aces_hill(x) if settings["tonemap_type"] == 1 else khronos_neutral(x) if settings["tonemap_type"] == 0 else x
    c = np.clip(x + (tm - x) * mix, 0.0, 1.0)
    c = linear_to_srgb(c)
    g = float((env or {}).get("gamma", 1.0) or 1.0)
    if classic and abs(g - 1.0) > 1e-6: c = 1.0 - np.power(1.0 - c, g)
    c = cas_sharpen(c, float(settings.get("sharpen", 0.0)))
    return np.clip(c, 0.0, 1.0), {"classic": classic, "tonemap_mix": mix, "dyn_exposure": round(s, 4), "exposure": round(e, 4)}


def settings_from_env(environ):
    """render profile (look.py PROFILES: highplus / ultra) -> firestorm_ldr settings + CAS sharpness"""
    g = lambda k, d: float(environ.get(k, d))
    return {"exposure": g("GT_FS_EXPOSURE", FS["exposure"]), "tonemap_type": int(g("GT_TONEMAP_TYPE", FS["tonemap_type"])),
            "tonemap_mix": g("GT_TONEMAP_MIX", FS["tonemap_mix"]), "dyn_coeff": FS["dyn_coeff"], "sharpen": g("GT_SHARPEN", 0.0),
            "glow_iterations": int(g("GT_GLOW_ITERATIONS", 2)), "glow_strength": g("GT_GLOW_STRENGTH", 0.325)}


def _box(a, r, axis):
    """box blur of radius r along axis (edge-clamped), via a cumulative sum"""
    if r < 1: return a
    pad = [(0, 0)] * a.ndim; pad[axis] = (r + 1, r)
    c = np.cumsum(np.pad(a, pad, mode="edge"), axis=axis)
    n = a.shape[axis]
    hi = np.take(c, np.arange(2 * r + 1, 2 * r + 1 + n), axis=axis); lo = np.take(c, np.arange(0, n), axis=axis)
    return (hi - lo) / (2 * r + 1)


def gauss_blur(a, sigma):
    """~gaussian blur (3 box passes per axis; Blender's python has no scipy) of an (h, w, c) image"""
    r = int(round(np.sqrt(12 * sigma * sigma / 3 + 1) / 2 - 0.5)) if sigma > 0 else 0
    for _ in range(3):
        a = _box(_box(a, r, 0), r, 1)
    return a


def bloom(glow, iterations=2, strength=0.325, width=1.3, res=512):
    """LL glow (renderGlow, glowExtractF/glowF.glsl): the glow buffer (colour x face glow) at 2^RenderGlowResolutionPow
    (512) wide, blurred RenderGlowIterations x (horizontal + vertical) with RenderGlowWidth (1.3) tap spacing, each pass
    weighted by RenderGlowStrength (0.325), added to the frame. -> linear rgb to add. Firestorm's "Glow strength" slider
    is the iteration count (David's High+: 2). ponytail: gaussian kernel, not LL's 7 taps"""
    if iterations <= 0 or not np.any(glow > 0): return np.zeros_like(glow)
    k = glow.shape[1] / float(res)
    sigma = width * 2.0 * k * np.sqrt(iterations)
    b = gauss_blur(glow, sigma)
    return b * strength * 2.0 * iterations


def cas_sharpen(c, amount):
    """RenderCASSharpness (AMD FidelityFX CAS, 0..1) approximated: an unsharp mask whose strength falls where the 3x3
    neighbourhood already has high contrast (CAS's adaptive part), on display values 0..1"""
    if amount <= 0: return c
    p = np.pad(c, ((1, 1), (1, 1), (0, 0)), mode="edge")
    nb = [p[1 + dy:p.shape[0] - 1 + dy, 1 + dx:p.shape[1] - 1 + dx] for dy in (-1, 0, 1) for dx in (-1, 0, 1)]
    lo = np.minimum.reduce(nb); hi = np.maximum.reduce(nb)
    cross = (nb[1] + nb[3] + nb[5] + nb[7]) / 4
    adapt = np.sqrt(np.clip(np.minimum(lo, 1 - hi) / np.maximum(hi, 1e-4), 0, 1))
    return np.clip(c + (c - cross) * adapt * amount, 0.0, 1.0)


def write_rgbe(path, rgb):
    """Radiance .hdr (RGBE, flat scanlines; top row first). rgb float (h, w, 3)"""
    h, w, _ = rgb.shape; v = np.maximum(rgb, 0.0).astype(np.float64); m = v.max(axis=2)
    ex = np.where(m > 1e-32, np.floor(np.log2(np.maximum(m, 1e-32))) + 1, 0)
    sc = np.where(m > 1e-32, 256.0 / np.power(2.0, ex), 0.0)
    out = np.zeros((h, w, 4), np.uint8)
    out[..., :3] = np.clip(v * sc[..., None], 0, 255).astype(np.uint8); out[..., 3] = np.where(m > 1e-32, ex + 128, 0).astype(np.uint8)
    with open(path, "wb") as f:
        f.write(b"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n" + f"-Y {h} +X {w}\n".encode()); f.write(out.tobytes())
