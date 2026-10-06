"""Region EEP sky at a moment, as the SL viewer computes it (pure: math only; make_job.eep / look.py use it).

env_at(doc): scene.json (environment + exported_at + sun_dir) -> the render's env dict:
  frac (day position, LL: (now + day_offset) % day_length), sun_dir / moon_dir (from the day cycle's sun_rotation /
  moon_rotation, as the EEP viewer does; the simulator's sun_dir only when the cycle has none), sunlight, ambient,
  cloud_shadow, horizon, zenith (EEP colours, lerped between the ground track's keyframes), moon (brightness), and at
  night night_sky {horizon, zenith, stars}: what the camera sees of the sky, from sky_rgb.
sky_rgb: the LL viewer's classic sky shader (class1/deferred/skyF.glsl; light settings from llsettingssky.cpp and
  llsettingsvo.cpp applySpecial) for one view elevation. ponytail: no clouds, rainbow, halo or moon/sun discs."""
import datetime, math

z0 = lambda v: [x or 0.0 for x in v]  # OSD JSON writes 0 as null

def qrot_x(q):
    """LL: LLVector3::x_axis * rotation (the sun/moon direction from its EEP rotation); q = [x, y, z, w]"""
    x, y, z, w = z0(q)
    return [1 - 2 * (y * y + z * z), 2 * (x * y + w * z), 2 * (x * z - w * y)]

def slerp(a, b, t):
    a, b = z0(a), z0(b); d = sum(p * q for p, q in zip(a, b))
    if d < 0: b, d = [-q for q in b], -d
    if d > 0.9995: r = [p + (q - p) * t for p, q in zip(a, b)]
    else:
        th = math.acos(d); s = math.sin(th)
        r = [p * math.sin((1 - t) * th) / s + q * math.sin(t * th) / s for p, q in zip(a, b)]
    n = math.sqrt(sum(c * c for c in r)) or 1.0
    return [c / n for c in r]

def params(f):
    """one sky keyframe -> the values the sky shader uses, with LL's defaults for keys a legacy conversion leaves out"""
    lh = f.get("legacy_haze") or {}
    return {"blue_density": z0(lh.get("blue_density", [0.2447, 0.4487, 0.76]))[:3],
            "blue_horizon": z0(lh.get("blue_horizon", [0.4954, 0.4954, 0.64]))[:3],
            "haze_density": lh.get("haze_density", 0.7) or 0.0, "haze_horizon": lh.get("haze_horizon", 0.19) or 0.0,
            "density_multiplier": lh.get("density_multiplier", 0.0001) or 0.0, "max_y": f.get("max_y", 1605) or 0.0,
            "glow": z0(f.get("glow", [5, 0.001, -0.48]))[:3], "cloud_shadow": f.get("cloud_shadow") or 0.0,
            "ambient": z0(f.get("ambient") or lh.get("ambient") or [0.25, 0.25, 0.25])[:3],
            "sunlight": z0(f.get("sunlight_color", [0.734, 0.782, 0.9]))[:3],
            "moon": f.get("moon_brightness") or 0.0, "stars": f.get("star_brightness") or 0.0}

def mix(a, b, t):
    return {k: ([x + (y - x) * t for x, y in zip(v, b[k])] if isinstance(v, list) else v + (b[k] - v) * t) for k, v in a.items()}

def sky_rgb(p, light_y, sun_up, glow_factor, elev_deg, light_dot=0.0):
    """skyF.glsl for a view `elev_deg` above the horizon. light_y = the (clamped) light direction's up component,
    light_dot = view . light (only the sun's haze glow uses it)."""
    vy = math.sin(math.radians(elev_deg))
    ln = p["max_y"] / vy if vy > 0 else 32000.0 / max(1e-6, -vy)   # rel_pos clamped to the dome
    bd, bh, hd, hh, dm = p["blue_density"], p["blue_horizon"], p["haze_density"], p["haze_horizon"], p["density_multiplier"]
    sl = [c * (1.0 if sun_up else 0.7) for c in p["sunlight"]]      # moonlight = sunlight colour x 0.7 ("match legacy")
    atten = [(b + hd * 0.25) * (dm * p["max_y"]) for b in bd]
    comb = [max(abs(b) + abs(hd), 1e-6) for b in bd]
    bw = [b / c for b, c in zip(bd, comb)]; hw = [hd / c for c in comb]
    off = 1.0 / max(1e-6, max(0.0, vy) + light_y)
    sl = [s * math.exp(-a * off) for s, a in zip(sl, atten)]
    tr = [math.exp(-c * ln * dm) for c in comb]
    g = (max(1.0 - light_dot, 0.001) * p["glow"][0]) ** p["glow"][2]
    g = 0.0 if glow_factor < 1.0 else glow_factor * (g + 0.25)
    amb = p["ambient"]
    col = [(bh[i] * bw[i] * (sl[i] + amb[i]) + hh * hw[i] * (sl[i] * g + amb[i])) * (1 - tr[i]) for i in range(3)]
    amb2 = [a + max(0.0, 1 - a) * p["cloud_shadow"] * 0.5 for a in amb]
    sl2 = [s * max(0.0, 1 - p["cloud_shadow"]) for s in sl]
    below = [bh[i] * bw[i] * (sl2[i] + amb2[i]) + hh * hw[i] * (sl2[i] * g + amb2[i]) for i in range(3)]
    col = [c + (b - c) * (1 - math.sqrt(math.sqrt(t))) for c, b, t in zip(col, below, tr)]
    return [min(5.0, max(0.0, 2 * c)) for c in col]

def env_at(doc):
    """scene.json -> env dict (see module doc), or None without a usable day cycle"""
    try:
        e = doc["environment"]["environment"]; dc = e["day_cycle"]
        at = datetime.datetime.fromisoformat(doc["exported_at"][:26] + doc["exported_at"][-6:]).timestamp()
        frac = ((at + e["day_offset"]) % e["day_length"]) / e["day_length"]
        keys = sorted(((k.get("key_keyframe") or 0.0), dc["frames"][k["key_name"]]) for k in dc["tracks"][1])
        i = max(j for j, (t, _) in enumerate(keys) if t <= frac)
    except (KeyError, TypeError, ValueError, IndexError):
        return None
    (t0, a), (t1, b) = keys[i], keys[(i + 1) % len(keys)]
    w = (frac - t0) / (((t1 - t0) % 1.0) or 1.0)
    p = mix(params(a), params(b), w)
    rot = lambda k: qrot_x(slerp(a[k], b[k], w)) if a.get(k) and b.get(k) else None
    sun = rot("sun_rotation") or z0(doc.get("sun_dir", [0, 0, 1])); moon = rot("moon_rotation")
    hz = lambda f: z0(f.get("legacy_haze", {}).get("blue_horizon", [0.3, 0.4, 0.6]))
    out = {"frac": frac, "sun_dir": sun, "sunlight": p["sunlight"], "ambient": p["ambient"], "cloud_shadow": p["cloud_shadow"],
           "horizon": [x + (y - x) * w for x, y in zip(hz(a), hz(b))][:3], "zenith": p["blue_density"], "moon": p["moon"]}
    if moon: out["moon_dir"] = moon
    if sun[2] < 0:   # night: the sky as the viewer draws it, lit by the moon (if up) and the sky ambient
        up = bool(moon) and moon[2] >= 0
        ly = max(moon[2] if up else -1.0, -0.1)   # LLEnvironment::getClampedLightNorm
        gf = p["moon"] * 0.25 if up else 0.0
        out["night_sky"] = {"horizon": sky_rgb(p, ly, False, gf, 2), "zenith": sky_rgb(p, ly, False, gf, 90),
                            "stars": min(1.0, p["stars"] / 500.0)}
    return out
