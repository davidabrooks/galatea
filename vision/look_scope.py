"""`look self` / hover-check render options (2026-10-26, David: the quick self and side-view render needs explicit options and named
presets so a hover or seat-height check draws only what it needs and is as fast as possible). Pure helpers, no Blender, no SL:
look.py parses --options with parse_options, filters the export's scene.json (filter_scene) before the mesher, and puts env_for(opts)
in the environment that render_mesh.py / handler.py read. Defaults reproduce the old render exactly. docs/render-presets.md says which
preset to use for which check.
Options (look.py argv, all `--name=value`; look self / hover check only; photo hd and the other looks reject them):
  --preset=<name>       hover-ground | hover-seat | photo-fast | outfit  (options below override the preset's)
  --scope=full|nearby|seat|none   what of the world is drawn: full = the near scene (default); nearby = linksets within NEARBY_R m;
                                  seat = the object she sits on; none = only her.  aliases: self-only = none, seat-only = seat
  --floor=auto|on|off   auto = on (a self render has always drawn the floor); off = no terrain, no floor prims, no red floor line
  --avatars=auto|off    off drops other avatars' meshes and attachments (a self look never draws them in the picture anyway)
  --hide-attach=<points>  comma list of attachment points (names such as skull,chin,left-ear or numbers) whose worn items are not drawn, e.g.
                        skull to skip hair when only her feet / hips matter. Dropping every item is not offered: her body is a worn
                        mesh, so with none she is not drawn at all.
  --textures=on|off     off: no scene / attachment textures (plain face colours); her bakes still load
  --materials=on|off    off: diffuse only, no normal / specular maps
  --small               quick size: half-size frame, 4 samples, 256 px bakes     --scale=<0.1..2> frame scale (wins over --small)
  --size=WxH            exact frame (wins over scale)      --samples=<1..256>   Cycles samples      --jpeg=<1..100> JPEG quality (90)
  --exposure=<stops>    view exposure, -6..6 (quick looks: -1.5)
  --cam-margin=<0.1..4> camera distance factor (1.08)  --cam-lens=<14..200> mm (50)  --cam-fov=<5..120> deg across the long side (instead of lens)
  --cam-height=<-1..3>  camera height above the floor in metres (default: half her height)
  --zoom=feet|off       feet = every side view is a tight close-up (about 0.6 m wide) on her LOWEST CONTACT with the floor: the soles standing, the hips
                        (and thighs / heels that touch) in a ground sit, the shins kneeling. `views=feet` = the same, from her left, as its own view.
Not exposed: lighting / shadows (one fixed neutral sun, no per-light switches), anti-aliasing filter, sky.
"""
import math

SCOPES = ("full", "nearby", "seat", "none")
SCOPE_ALIASES = {"self-only": "none", "seat-only": "seat"}
FLOORS = ("auto", "on", "off")
NEARBY_R = 6.0
FLAG_ON_OFF = ("on", "off")
ZOOM_ALIASES = {"contact": "feet", "close": "feet", "closeup": "feet", "none": "off"}
# SL attachment points (LLViewerJointAttachment ids) by name
ATTACH_POINTS = {"chest": 1, "skull": 2, "head": 2, "left-shoulder": 3, "right-shoulder": 4, "left-hand": 5, "right-hand": 6, "left-foot": 7, "right-foot": 8,
                 "spine": 9, "back": 9, "pelvis": 10, "mouth": 11, "chin": 12, "left-ear": 13, "right-ear": 14, "left-eye": 15, "right-eye": 16, "nose": 17,
                 "right-upper-arm": 18, "right-lower-arm": 19, "left-upper-arm": 20, "left-lower-arm": 21, "right-hip": 22, "right-upper-leg": 23,
                 "right-lower-leg": 24, "left-hip": 25, "left-upper-leg": 26, "left-lower-leg": 27, "stomach": 28, "belly": 28, "left-pec": 29, "right-pec": 30,
                 "neck": 39, "avatar-center": 40}

# presets: options only (explicit options override); "views" is the default `look self` side list when none is given
PRESETS = {
    "hover-ground": {"scope": "none", "floor": "on", "samples": 8, "textures": "off", "materials": "off"},          # her + the floor under her, flat and fast
    "hover-seat":   {"scope": "seat", "floor": "off", "samples": 8, "textures": "off", "materials": "off", "views": "left"},  # her + the seat object; the floor only if asked
    "photo-fast":   {"scope": "full", "floor": "auto", "fast": True},                                             # today's `look self fast`: the whole near scene
    "outfit":       {"scope": "none", "floor": "off", "fast": True, "views": "front,back"},                       # how she looks: her alone, textures on, normal size
}
PHOTO_HD = "photo-hd"   # `photo hd` (the client routes it; look.py never renders it)

DEFAULTS = {"preset": None, "scope": "full", "floor": "auto", "avatars": "auto", "hide_attach": (), "textures": "on", "materials": "on",
            "small": False, "fast": False, "scale": None, "size": None, "samples": None, "jpeg": None, "exposure": None,
            "cam_margin": None, "cam_lens": None, "cam_fov": None, "cam_height": None, "views": None, "zoom": "off"}

def _num(name, v, lo, hi):
    try: x = float(v)
    except ValueError: raise ValueError(f"--{name} needs a number ({lo}..{hi}), got '{v}'")
    if math.isnan(x) or not lo <= x <= hi: raise ValueError(f"--{name} must be {lo}..{hi}, got '{v}'")
    return x

def parse_options(a):
    """argv words -> (rest, opts); ValueError (message for the user) on a bad value. Only the -- forms (a bare word could be a target name).
    A preset fills the options first, anything explicit overrides it."""
    rest, given = [], {}
    for x in a:
        t = x[2:] if x.startswith("--") else ""
        k, _, v = t.partition("=")
        if x == "--small": given["small"] = True
        elif k == "preset": given["preset"] = v.lower()
        elif k == "scope": given["scope"] = SCOPE_ALIASES.get(v.lower(), v.lower())
        elif k in ("floor", "avatars", "textures", "materials"): given[k] = v.lower()
        elif k == "hide-attach":
            pts = []
            for n in (x_.strip().lower() for x_ in v.split(",") if x_.strip()):
                if n in ATTACH_POINTS: pts.append(ATTACH_POINTS[n])
                elif n.isdigit() and 1 <= int(n) <= 56: pts.append(int(n))
                else: raise ValueError(f"unknown attachment point '{n}' ({', '.join(sorted(ATTACH_POINTS))} or 1..56)")
            given["hide_attach"] = tuple(sorted(set(pts)))
        elif k == "scale": given["scale"] = _num("scale", v, 0.1, 2.0)
        elif k == "size":
            try: w, h = (int(p) for p in v.lower().split("x"))
            except ValueError: raise ValueError(f"--size needs WxH, got '{v}'")
            if not (64 <= w <= 4096 and 64 <= h <= 4096): raise ValueError("--size must be 64..4096 each way")
            given["size"] = f"{w}x{h}"
        elif k == "samples": given["samples"] = int(_num("samples", v, 1, 256))
        elif k == "jpeg": given["jpeg"] = int(_num("jpeg", v, 1, 100))
        elif k == "exposure": given["exposure"] = _num("exposure", v, -6, 6)
        elif k == "zoom":
            z = ZOOM_ALIASES.get(v.lower(), v.lower())
            if z not in ("feet", "off"): raise ValueError(f"unknown zoom '{v}' (feet = close-up on her lowest contact, off)")
            given["zoom"] = z
        elif k == "cam-margin": given["cam_margin"] = _num("cam-margin", v, 0.1, 4)
        elif k == "cam-lens": given["cam_lens"] = _num("cam-lens", v, 14, 200)
        elif k == "cam-fov": given["cam_fov"] = _num("cam-fov", v, 5, 120)
        elif k == "cam-height": given["cam_height"] = _num("cam-height", v, -1, 3)
        else: rest.append(x)
    opts = dict(DEFAULTS)
    p = given.get("preset")
    if p == PHOTO_HD: raise ValueError("preset photo-hd is `photo hd` (the client runs it); look.py renders the quick presets")
    if p is not None:
        if p not in PRESETS: raise ValueError(f"unknown preset '{p}' ({', '.join(PRESETS)}, {PHOTO_HD})")
        opts.update(PRESETS[p])
    opts.update(given)
    for key, allowed in (("scope", SCOPES), ("floor", FLOORS), ("avatars", ("auto", "off")), ("textures", FLAG_ON_OFF), ("materials", FLAG_ON_OFF)):
        if opts[key] not in allowed: raise ValueError(f"unknown {key} '{opts[key]}' ({', '.join(allowed)})")
    if opts["cam_fov"] is not None and opts["cam_lens"] is None: opts["cam_lens"] = round(18 / math.tan(math.radians(opts["cam_fov"]) / 2), 2)
    return rest, opts

def is_default(opts):
    """True when nothing differs from the old look self render (so photo hd / other looks can reject any option)"""
    return all(opts[k] == v for k, v in DEFAULTS.items() if k not in ("views",))

def floor_on(floor): return floor != "off"

def hovercheck_view(env):
    """the ONE view the hover check renders (2026-10-26, David: zoom in when checking the hover, but no extra render): the close-up on her lowest
    contact ("feet": soles / hips / shins) instead of the wide side view; GT_HC_ZOOM=off gives the old wide "side" view back. Always a single
    view: a hover check never renders two images."""
    return "side" if (env.get("GT_HC_ZOOM") or "on").lower() == "off" else "feet"

def env_for(opts):
    """-> (env vars for render_mesh / handler, fast): small / scale / samples imply the quick path"""
    e = {}
    if opts["floor"] == "off": e["GT_FLOOR"] = "off"
    if opts["textures"] == "off": e["GT_NO_TEXTURES"] = "1"
    if opts["materials"] == "off": e["GT_TEX_MAPS"] = "0"
    scale = opts["scale"] if opts["scale"] is not None else (0.5 if opts["small"] else None)
    if scale is not None and scale != 1.0: e["GT_RES_SCALE"] = f"{scale:g}"
    if opts["size"]: e["GT_RES"] = opts["size"]
    samples = opts["samples"] if opts["samples"] is not None else (4 if opts["small"] else None)
    if samples is not None: e["GT_HC_SAMPLES"] = str(samples)
    if opts["jpeg"] is not None: e["GT_JPEG_QUALITY"] = str(opts["jpeg"])
    if opts.get("zoom") == "feet": e["GT_ZOOM"] = "feet"
    if opts["exposure"] is not None: e["GT_EXPOSURE"] = f"{opts['exposure']:g}"
    for k, name in (("cam_margin", "GT_CAM_MARGIN"), ("cam_lens", "GT_CAM_LENS"), ("cam_height", "GT_CAM_HEIGHT")):
        if opts[k] is not None: e[name] = f"{opts[k]:g}"
    return e, bool(opts["small"] or opts["fast"] or opts["samples"] is not None or opts["scale"] is not None)

def bake_cap_small(opts): return bool(opts["small"])

def uid(v):
    """an exported local id: a plain int, or the 4 bytes of a uint32 (big endian) as the JSON export writes it"""
    if v is None: return 0
    if isinstance(v, (int, float)): return int(v)
    if isinstance(v, (list, tuple)): return int.from_bytes(bytes(int(b or 0) & 255 for b in v), "big")
    return 0

def _root(p, by_local):
    for _ in range(64):
        par = uid(p.get("parentid"))
        if par and par in by_local: p = by_local[par]
        else: break
    return uid(p.get("localid"))

def _floor_under(prims, me, exclude):
    """every scene prim a straight ray down from above her could hit: its bounding sphere covers her xy and its centre is below
    her (centre z <= her z + 0.3, within 6 m). A superset of what the render's floor cast hits, so the gap is the same as in the
    full scene (detached prims: drawn on their own)"""
    mx, my, mz = me; out = []
    for p in prims:
        if id(p) in exclude or p.get("pcode") not in (None, 9) or "world_pos" not in p or "scale" not in p: continue
        wx, wy, wz = p["world_pos"]; r = 0.5 * math.sqrt(sum(float(v) ** 2 for v in p["scale"]))
        if math.hypot(wx - mx, wy - my) <= r and mz - 6.0 <= wz <= mz + 0.3: out.append(p)
    return out

def needs_filter(opts):
    return opts["scope"] in ("seat", "none") or opts["avatars"] == "off" or bool(opts["hide_attach"])

def filter_scene(doc, opts):
    """a copy of the export doc with only what the options draw -> (doc2, info). scope seat / none: her own attachments, the seat's
    linkset (seat) and, when the floor is on, the scene prims under her (see _floor_under); full / nearby keep every scene prim
    (nearby is a mesher radius); avatars=off drops the other avatars and their worn items; hide_attach drops her items on those points."""
    scope = opts["scope"]
    info = {"scope": scope, "floor": floor_on(opts["floor"]), "seat_found": False, "kept_scene": 0, "kept_seat": 0, "kept_floor": 0, "dropped": 0}
    prims = doc.get("prims") or []; me = doc["me"]["pos"]; agent = doc.get("agent_id")
    others = {av.get("agent_id") for av in doc.get("avatars") or [] if av.get("agent_id") and av.get("agent_id") != agent}
    is_worn = lambda p: bool(p.get("attached_to_me")) or p.get("attached_to") is not None   # the mesher's Owner(): "attached_to_me" or the wearer's id
    scene = [p for p in prims if not is_worn(p)]
    worn = [p for p in prims if is_worn(p)]
    theirs = [p for p in worn if p.get("attached_to") in others]
    hide = set(opts["hide_attach"])
    mine = [p for p in worn if p.get("attached_to") not in others and p.get("attach_point") not in hide]
    if scope in ("full", "nearby") and opts["avatars"] != "off": mine = mine + theirs   # other avatars' worn items stay unless dropped
    seat, floors = [], []
    if scope in ("full", "nearby"): kept_scene = scene
    else:
        if scope == "seat":
            by_local = {uid(p.get("localid")): p for p in scene}
            sit = uid(doc["me"].get("sitting_on"))
            if sit and sit in by_local:
                r = _root(by_local[sit], by_local); seat = [p for p in scene if _root(p, by_local) == r]; info["seat_found"] = True
        if floor_on(opts["floor"]): floors = [dict(p, parentid=0) for p in _floor_under(scene, me, {id(p) for p in seat})]
        kept_scene = seat + floors
    kept = mine + kept_scene
    info.update(kept_scene=len(kept_scene), kept_seat=len(seat), kept_floor=len(floors), dropped=len(prims) - len(kept))
    d2 = dict(doc); d2["prims"] = kept
    d2["avatars"] = [av for av in doc.get("avatars") or [] if av.get("agent_id") == agent] if scope in ("seat", "none") or opts["avatars"] == "off" else doc.get("avatars")
    return d2, info

def nearby_mesher_args(d, me):
    """scene-mesher args for scope nearby: the standard near scene, but only linksets reaching within NEARBY_R of her"""
    return [d, "12", "all", ",".join(str(v) for v in me), str(NEARBY_R), "--roots=32"]

def hovercheck_preset(doc):
    """the automatic hover check's minimal preset: seated -> hover-seat, else hover-ground"""
    return "hover-seat" if uid((doc.get("me") or {}).get("sitting_on")) else "hover-ground"
