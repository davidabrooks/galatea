"""SL particle systems (llParticleSystem, ObjectUpdate PSys) -> a steady-state snapshot of particles for a still.

Pure (numpy only, no bpy) so CI can test it. A source emits burst_part_count particles every burst_rate s, each living
part_max_age s; in a still, ages are spread uniformly over [0, part_max_age). Position = source + radius x dir +
(speed x age + accel x age^2 / 2) along dir / accel; colour and scale interpolate start -> end with age (InterpColor /
InterpScale flags). ponytail: no wind, bounce, follow-source velocity or target homing; patterns: drop / explode /
angle / angle cone (cone around the source's +Z, unrotated)."""
import numpy as np

INTERP_COLOR, INTERP_SCALE, EMISSIVE = 0x001, 0x002, 0x100
DROP, EXPLODE, ANGLE, ANGLE_CONE, ANGLE_CONE_EMPTY = 0x01, 0x02, 0x04, 0x08, 0x10
MAX_PER_SOURCE = 400


def _v(x, n=3):
    x = list(x or [])
    return np.array([float(v or 0.0) for v in (x + [0.0] * n)[:n]])


def snapshot(ps, pos, seed=0):
    """ps = the export's "particles" map (ParticleSystem.GetOSD), pos = source world position.
    -> list of dicts {pos, color (rgba), size (x, y), emissive, glow}"""
    age_max = float(ps.get("part_max_age") or 0)
    count = int(ps.get("burst_part_count") or 0); rate = max(0.05, float(ps.get("burst_rate") or 0.1))
    if age_max <= 0 or count <= 0: return []
    n = int(min(MAX_PER_SOURCE, max(1, round(count * age_max / rate))))
    rng = np.random.default_rng(seed)
    age = rng.uniform(0, age_max, n); t = age / age_max
    pat = int(ps.get("pattern") or EXPLODE)
    if pat & DROP:
        d = np.tile([0.0, 0.0, 0.0], (n, 1))
    elif pat & (ANGLE | ANGLE_CONE | ANGLE_CONE_EMPTY):
        lo, hi = float(ps.get("inner_angle") or 0), float(ps.get("outer_angle") or 0)
        if hi < lo: lo, hi = hi, lo
        th = rng.uniform(lo, hi, n); ph = rng.uniform(0, 2 * np.pi, n) if pat & (ANGLE_CONE | ANGLE_CONE_EMPTY) else np.zeros(n)
        d = np.stack([np.sin(th) * np.cos(ph), np.sin(th) * np.sin(ph), np.cos(th)], 1)
    else:   # explode: any direction
        d = rng.normal(size=(n, 3)); d /= np.maximum(np.linalg.norm(d, axis=1, keepdims=True), 1e-9)
    sp = rng.uniform(float(ps.get("burst_speed_min") or 0), float(ps.get("burst_speed_max") or 0), n)
    acc = _v(ps.get("part_acceleration"))
    p = _v(pos) + d * (float(ps.get("burst_radius") or 0) + (sp * age)[:, None]) + acc * (age ** 2 / 2)[:, None]
    fl = int(ps.get("part_data_flags") or 0)
    c0, c1 = _v(ps.get("part_start_color"), 4), _v(ps.get("part_end_color"), 4)
    s0, s1 = _v(ps.get("part_start_scale"), 2), _v(ps.get("part_end_scale"), 2)
    col = c0 + (c1 - c0) * t[:, None] if fl & INTERP_COLOR else np.tile(c0, (n, 1))
    size = s0 + (s1 - s0) * t[:, None] if fl & INTERP_SCALE else np.tile(s0, (n, 1))
    g0, g1 = float(ps.get("part_start_glow") or 0), float(ps.get("part_end_glow") or 0)
    glow = g0 + (g1 - g0) * t
    return [{"pos": p[i].tolist(), "color": np.clip(col[i], 0, 1).tolist(), "size": np.maximum(size[i], 0.03).tolist(),
             "emissive": bool(fl & EMISSIVE), "glow": float(glow[i])} for i in range(n)]
