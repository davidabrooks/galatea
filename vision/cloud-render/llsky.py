"""Region EEP sky at a moment, as the SL viewer computes it (pure: math only; make_job.eep / look.py use it).

env_at(doc): scene.json (environment + exported_at + sun_dir) -> the render's env dict:
  frac (day position, LL: (now + day_offset) % day_length), sun_dir / moon_dir (from the day cycle's sun_rotation /
  moon_rotation, as the EEP viewer does; the simulator's sun_dir only when the cycle has none), sunlight, ambient,
  cloud_shadow, horizon, zenith (EEP colours, lerped between the ground track's keyframes), moon (brightness), and at
  night night_sky {horizon, zenith, stars}: what the camera sees of the sky, from sky_rgb.
sky_rgb: the LL viewer's classic sky shader (class1/deferred/skyF.glsl; light settings from llsettingssky.cpp and
  llsettingsvo.cpp applySpecial) for one view elevation. ponytail: no clouds, rainbow, halo or moon/sun discs."""
import datetime, json, math, os

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
            "moon": f.get("moon_brightness") or 0.0, "stars": f.get("star_brightness") or 0.0,
            # clouds (photo hd): Windlight's "cloud coverage" is cloud_shadow; density = cloud_pos_density1 z
            "cloud_color": z0(f.get("cloud_color", [0.41, 0.41, 0.41]))[:3], "cloud_scale": f.get("cloud_scale", 0.42) or 0.0,
            "cloud_density": (z0(f.get("cloud_pos_density1", [1, 0.53, 1])) + [1, 1, 1])[2],
            # photo hd (2026-10-13, David: "support all the shared environment settings"): the rest of the EEP sky frame
            "cloud_pos": (z0(f.get("cloud_pos_density1", [1, 0.53, 1])) + [0, 0])[:2],
            "cloud_detail": (z0(f.get("cloud_pos_density2", [1, 0.53, 0.125])) + [1, 1, 0.125])[2],
            "cloud_scroll": (z0(f.get("cloud_scroll_rate", [0.2, 0.01])) + [0, 0])[:2],
            "gamma": f.get("gamma", 1.0) or 0.0, "distance_multiplier": lh.get("distance_multiplier", 0.8) or 0.0,
            "sun_scale": f.get("sun_scale", 1.0) or 0.0, "moon_scale": f.get("moon_scale", 1.0) or 0.0}

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

def day_frac(doc):
    """LL: (now + day_offset) % day_length / day_length at the export time"""
    e = doc["environment"]["environment"]
    at = datetime.datetime.fromisoformat(doc["exported_at"][:26] + doc["exported_at"][-6:]).timestamp()
    return ((at + e["day_offset"]) % e["day_length"]) / e["day_length"], at

def water_of(doc):
    """the day cycle's water track (track 0; EEP water frames have no time of day in practice: first key) -> render dict"""
    try:
        dc = doc["environment"]["environment"]["day_cycle"]; f = dc["frames"][dc["tracks"][0][0]["key_name"]]
    except (KeyError, TypeError, IndexError):
        return None
    if f.get("type") != "water": return None
    return {"fog_color": z0(f.get("water_fog_color", [0.0157, 0.149, 0.251]))[:3], "fog_density": f.get("water_fog_density", 10) or 0.0,
            "underwater_fog_mod": f.get("underwater_fog_mod", 0.25) or 0.0, "fresnel_scale": f.get("fresnel_scale", 0.4) or 0.0,
            "fresnel_offset": f.get("fresnel_offset", 0.5) or 0.0, "normal_scale": z0(f.get("normal_scale", [2, 2, 2]))[:3],
            "wave1": z0(f.get("wave1_direction", [1.05, -0.42]))[:2], "wave2": z0(f.get("wave2_direction", [1.11, -1.16]))[:2],
            "blur_multiplier": f.get("blur_multiplier", 0.04) or 0.0, "normal_map": f.get("normal_map")}

def build_env(p, sun, moon, frac=None, t=0.0, horizon=None):
    """interpolated sky params + sun/moon directions -> the render's env dict (shared by the region track and presets)"""
    out = {"frac": frac, "sun_dir": sun, "sunlight": p["sunlight"], "ambient": p["ambient"], "cloud_shadow": p["cloud_shadow"],
           "horizon": horizon or p["blue_horizon"], "zenith": p["blue_density"], "moon": p["moon"],
           "gamma": p["gamma"], "glow": p["glow"], "sun_scale": p["sun_scale"], "moon_scale": p["moon_scale"],
           "stars": min(1.0, p["stars"] / 500.0), "max_y": p["max_y"],
           "haze": {"blue_density": p["blue_density"], "haze_density": p["haze_density"], "haze_horizon": p["haze_horizon"],
                    "density_multiplier": p["density_multiplier"], "distance_multiplier": p["distance_multiplier"]}}
    # cloud layer: scroll is EEP's (legacy - 10) rate; the layer's offset at the export time = position + rate x t / 100 (wrapped)
    off = [(c + r * (t % 100000.0) / 100.0) % 1.0 for c, r in zip(p["cloud_pos"], p["cloud_scroll"])]
    out["clouds"] = {"color": p["cloud_color"], "coverage": p["cloud_shadow"], "density": p["cloud_density"], "scale": p["cloud_scale"],
                     "detail": p["cloud_detail"], "scroll": p["cloud_scroll"], "offset": off}
    if moon: out["moon_dir"] = moon
    up = sun[2] >= 0
    # what the camera sees of the sky, by the viewer's sky shader (day and night): horizon (2 deg) and zenith, and the
    # sun-side horizon (with glow) for the haze tint toward the sun
    ly = max(sun[2], 0.0) if up else max(moon[2] if moon and moon[2] >= 0 else -1.0, -0.1)
    gf = 1.0 if up else (p["moon"] * 0.25 if moon and moon[2] >= 0 else 0.0)
    out["sky"] = {"horizon": sky_rgb(p, ly, up, gf, 2), "zenith": sky_rgb(p, ly, up, gf, 90)}
    if not up:   # night: the sky as the viewer draws it, lit by the moon (if up) and the sky ambient
        out["night_sky"] = {**out["sky"], "stars": out["stars"]}
    return out

def env_at(doc):
    """scene.json -> env dict (see module doc), or None without a usable day cycle"""
    try:
        e = doc["environment"]["environment"]; dc = e["day_cycle"]
        frac, at = day_frac(doc)
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
    out = build_env(p, sun, moon, frac, at, [x + (y - x) * w for x, y in zip(hz(a), hz(b))][:3])
    out["source"] = "shared"
    wt = water_of(doc)
    if wt: out["water"] = wt
    return out

# ---- named sky presets (photo hd ... sunset): the viewer library's fixed skies, overriding the shared environment ----
_PRESETS = None
def presets():
    global _PRESETS
    if _PRESETS is None:
        _PRESETS = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "sky_presets.json")))["presets"]
    return _PRESETS

def azalt_dir(az, alt):
    """LL convert_azimuth_and_altitude_to_quat applied to the x axis: east_angle / sun_angle -> direction (+x east, +z up)"""
    return [math.cos(az) * math.cos(alt), math.sin(az) * math.cos(alt), math.sin(alt)]

def dir_quat(d):
    """direction -> EEP rotation [x, y, z, w] with qrot_x(q) == d (LL: angle-axis from the x axis)"""
    x = [1.0, 0.0, 0.0]; ax = [x[1] * d[2] - x[2] * d[1], x[2] * d[0] - x[0] * d[2], x[0] * d[1] - x[1] * d[0]]
    n = math.sqrt(sum(c * c for c in ax)); ang = math.acos(max(-1.0, min(1.0, d[0])))
    if n < 1e-9: ax, n = [0.0, 1.0, 0.0], 1.0   # parallel / antiparallel to x
    s = math.sin(ang / 2)
    return [ax[0] / n * s, ax[1] / n * s, ax[2] / n * s, math.cos(ang / 2)]

def preset_frame(w):
    """legacy Windlight sky (LLSD map of 4-vectors) -> an EEP sky frame, as LLSettingsSky::translateLegacySettings:
    scalars from the first component, cloud position / scroll rate shifted by -10 (EEP stores the offset from 10),
    star brightness x 250, sun and moon (opposite) rotation from east_angle / sun_angle"""
    v = lambda k, d=None: (w.get(k) if w.get(k) is not None else d)
    s = lambda k, d: (v(k)[0] if isinstance(v(k), list) else v(k, d))
    az, alt = v("east_angle", 0.0), v("sun_angle", math.pi / 2)
    c1, c2 = list(v("cloud_pos_density1", [1, 0.53, 1])), list(v("cloud_pos_density2", [1, 0.53, 0.125]))
    return {"type": "sky", "sunlight_color": v("sunlight_color")[:3], "cloud_color": v("cloud_color")[:3],
            "cloud_pos_density1": [c1[0] - 10 if c1[0] > 5 else c1[0], c1[1], c1[2]], "cloud_pos_density2": [c2[0] - 10 if c2[0] > 5 else c2[0], c2[1], c2[2]],
            "cloud_scale": s("cloud_scale", 0.42), "cloud_scroll_rate": [x - 10 for x in v("cloud_scroll_rate", [10.2, 10.01])],
            "cloud_shadow": s("cloud_shadow", 0.27), "gamma": s("gamma", 1.0), "glow": v("glow")[:3], "max_y": s("max_y", 1605),
            "star_brightness": v("star_brightness", 0) * 250.0, "moon_brightness": 0.5, "sun_scale": 1.0, "moon_scale": 1.0,
            "sun_rotation": dir_quat(azalt_dir(az, alt)), "moon_rotation": dir_quat(azalt_dir(az + math.pi, -alt)),
            "legacy_haze": {"ambient": v("ambient")[:3], "blue_density": v("blue_density")[:3], "blue_horizon": v("blue_horizon")[:3],
                            "haze_density": s("haze_density", 0.7), "haze_horizon": s("haze_horizon", 0.19),
                            "density_multiplier": s("density_multiplier", 0.0001), "distance_multiplier": s("distance_multiplier", 0.8)}}

def preset_env(name, doc=None):
    """a named preset ("sunset") -> env dict, keeping the shared water settings when the export has them; None = unknown"""
    w = presets().get((name or "").strip().lower())
    if w is None: return None
    f = preset_frame(w)
    out = build_env(params(f), qrot_x(f["sun_rotation"]), qrot_x(f["moon_rotation"]))
    out["source"] = "preset:" + w["name"]
    wt = water_of(doc) if doc else None
    if wt: out["water"] = wt
    return out

def photo_env(doc, sky=None):
    """photo hd: the shared environment (default, sky None / "shared") or a named preset; ValueError for an unknown one"""
    if not sky or sky.lower() == "shared": return env_at(doc)
    e = preset_env(sky, doc)
    if e is None: raise ValueError(f"unknown sky preset '{sky}' (shared, {', '.join(sorted(presets()))})")
    return e
