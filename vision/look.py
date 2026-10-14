#!/usr/bin/env python3
"""`look` for the text client: a `scene export` dir -> mesh (scene-mesher) -> CPU Cycles render(s) of what's around her.
usage: look.py <export dir> {view|self|around|at} [target name] [--fast] [--far] [--views=front,back,left,right,<deg>|all]
             [--hd[=landscape|portrait]]
  --sky=<preset>  (with --hd) override the shared environment: default, midday, sunrise, sunset, midnight, night, foggy
  --hd    4K photo for David (not her quick vision look): 3840x2160 (portrait 2160x3840; self defaults to portrait),
          always in the real scene (self too: framed on her whole body from in front, no studio backdrop), with
          terrain, water and the export's sky/sun (96 m backdrop when the export reaches it), adaptive sampling +
          OpenImageDenoise, full-res textures and bakes (~5 min on 8 CPUs for a furnished room). Firestorm-tonemapped PNG + quality-95 JPG, and the HDR masters (32-bit OpenEXR + Radiance .hdr) in GT_PHOTO_OUT (/workspace/secondlife/photos), photo-<stamp>-<mode>.png/.jpg
  view    over her shoulder, along her facing        around  4 views (front, left, back, right)
  self    her face + full body (avatar only)         at X    from beside her head toward avatar/object X
  --views (self) only the full body, from each listed side / angle (degrees counter-clockwise from her front: 90 = her
          left, 180 = her back); all = front, back, left, right. One image per view: ...-self-body-<side|NNdeg>.jpg
  --far   also the backdrop (scenery 30-96 m at a screen-size LOD, region terrain, water, sky gradient; needs a 96 m
          export). Off by default (David 2026-10-04: her renders are for her own needs, speed first): near scene only.
Prints one image path per line (then a JSON summary line). Read-only: never talks to SL; only CDN asset GETs.
Needs: dotnet + SCENE_MESHER (built scene-mesher), BLENDER, the imgvenv python (imagecodecs, pillow).
"""
import json, os, re, shutil, subprocess, sys, time
HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, f"{HERE}/cloud-render")
os.environ.setdefault("GT_MAX_TEXTURES", "4000")  # Warehouse 21 crowd: ~3200 scene + avatar textures (3000 dropped ~200 far ones); disk-cached after the first look
os.environ.setdefault("GT_TEX_WORKERS", "24")  # local disk cache; parallel CDN GETs (crowd looks were ~50 s at 8)
import handler, llsky, make_job  # texture fetch (CDN, capped) + bake decode / region sky: same code as the Runpod path

MESHER = os.environ.get("SCENE_MESHER", "/home/box/tools/scene-mesher/SceneMesher.dll")
# the text client's supervisor may not have dotnet on PATH (2026-10-04: "nice: 'dotnet': No such file"); DOTNET_ROOT is set
DOTNET = shutil.which("dotnet") or os.path.join(os.environ.get("DOTNET_ROOT", "/home/box/.dotnet"), "dotnet")
BLENDER = os.environ.get("BLENDER", "/home/box/tools/blender-4.2.3-linux-x64/blender")
OUT = os.environ.get("GT_LOOK_OUT", "/workspace/secondlife/vision/look")
WORK = os.environ.get("GT_LOOK_WORK", "/workspace/secondlife/vision/look-work")  # tex/ doubles as the CDN texture cache
VIEWS = {"view": "eye", "around": "eye;eye:90;eye:180;eye:-90"}
PHOTO_OUT = os.environ.get("GT_PHOTO_OUT", "/workspace/secondlife/photos")
HD_RES = {"landscape": "3840x2160", "portrait": "2160x3840"}

# render profiles = David's Firestorm graphics presets (2026-10-13). highplus = his photo preset "High+" (screenshots):
# textures max 2048, tonemap ACES mix 0.0, CAS sharpening 0.4, exposure 1.0, draw distance 128, LOD volume 2.0 / avatar 1.0 /
# terrain 2.0. ultra = Firestorm featuretable "list Ultra": mix 0.7, sharpening 0.4, textures 2048, draw 256, LOD volume 3.0.
# Cycles path-traces AO / shadows / reflections itself; attached (face) lights are never rendered (matches his OFF)
PROFILES = {
    "highplus": {"GT_TEX_CAP_AVATAR": "2048", "GT_TEX_CAP_SCENE": "2048", "GT_TONEMAP_MIX": "0.0", "GT_TONEMAP_TYPE": "1",
                 "GT_SHARPEN": "0.4", "GT_FS_EXPOSURE": "1.0", "GT_GLOW_ITERATIONS": "2", "GT_GLOW_STRENGTH": "0.325"},
    "ultra": {"GT_TEX_CAP_AVATAR": "2048", "GT_TEX_CAP_SCENE": "2048", "GT_TONEMAP_MIX": "0.7", "GT_TONEMAP_TYPE": "1",
              "GT_SHARPEN": "0.4", "GT_FS_EXPOSURE": "1.0", "GT_GLOW_ITERATIONS": "2", "GT_GLOW_STRENGTH": "0.325"},
}

def hd_settings(arg, mode, profile="highplus"):
    """--hd[=landscape|portrait] -> (orientation, env for the HD render) or (None, {}) when absent. Quality knobs only:
    her normal looks never pass --hd, so they keep their small, fast settings."""
    if arg is None: return None, {}
    o = (arg or ("portrait" if mode == "self" else "landscape")).lower()
    if o not in HD_RES: raise ValueError(f"unknown photo preset '{arg}' (landscape or portrait)")
    if profile not in PROFILES: raise ValueError(f"unknown render profile '{profile}' ({' or '.join(PROFILES)})")
    return o, {"GT_HD": "1", "GT_RES": HD_RES[o], "GT_SAMPLES": os.environ.get("GT_HD_SAMPLES", "24"), "GT_DENOISE": "1",
               "GT_STUDIO_SHADOW": "1", "GT_HD_PROFILE": profile, **PROFILES[profile]}

def retry_env(rc, hd):
    """env for the 2nd Blender attempt after a signal death (rc < 0). 2026-10-13 20:05: an HD landscape was OOM-killed
    (SIGKILL, 7.2 GB) after 8 min and the identical rerun needed the same memory: retry lighter (textures capped at
    1024 px, smaller tiles); a plain crash (segfault) just reruns"""
    if hd and rc == -9: return {"GT_TEX_LIMIT": "1024", "GT_TILE": "512"}
    return {}

def env_for(doc, hd, sky=None):
    """mesh.json env: photo hd -> the shared EEP environment (sky None/"shared") or a named preset (llsky.photo_env;
    ValueError for an unknown name); her quick looks -> None: render_mesh's fixed neutral lighting, no EEP at all"""
    if not hd: return None
    return llsky.photo_env(doc, sky)

SIDES = ("front", "back", "left", "right")
def body_views(arg):
    """--views=back,left,45 / all -> GT_BODY_VIEWS for the self body render ("" = the single front view, unchanged).
    A side name or degrees around her vertical axis, counter-clockwise from her front (90 = her left, 180 = her back)."""
    v = (arg or "").strip().lower()
    if not v: return ""
    out = []
    for p in (SIDES if v == "all" else [p.strip() for p in v.split(",") if p.strip()]):
        q = p[:-3] if p.endswith("deg") else p
        if q not in SIDES:
            try: d = float(q)
            except ValueError: d = None
            if d is None or d != d or abs(d) > 360: raise ValueError(f"unknown view '{p}' (front, back, left, right, all, or degrees -360..360)")
            q = f"{d:g}"
        out.append(q)
    return ";".join(dict.fromkeys(out))

# SL's default terrain (llviewerregion / LLVLComposition): dirt, grass, mountain, rock; start 10 m, range 60 m
SL_TERRAIN_DEFAULT = {"detail": ["0bc58228-74a0-7e83-89bc-5c23464bcec5", "63338ede-0037-c4fd-855b-015d77112fc8",
                                 "303cd381-8560-7579-23f1-f0a880799740", "53a2f406-4895-1d13-d541-d2e3b86bc19c"],
                      "start": [10.0] * 4, "range": [60.0] * 4}

def terrain_composition(doc, texdir, fetch=None):
    """photo hd: each region's terrain detail textures + blend heights (scene.json terrain_composition, exports
    2026-10-13+; older exports: SL's defaults for her region), with the fetched texture path per layer (None = colour)"""
    fetch = fetch or (lambda u: handler.fetch_tex(u, texdir, 1024)[1])
    comps = [dict(c) for c in doc.get("terrain_composition") or []] or [{**SL_TERRAIN_DEFAULT, "region": doc.get("region", ""), "offset": [0, 0, 0], "default": True}]
    for c in comps:
        c["offset"] = [v or 0 for v in (c.get("offset") or [0, 0, 0])]
        # OSD JSON writes 0 as null: a region whose corner start height / range is 0 exported [null, ...] (2026-10-13
        # render_mesh terrain_layers TypeError). Nulls -> 0; a missing/short list -> SL's default
        for k in ("start", "range"):
            v = c.get(k)
            c[k] = [x or 0.0 for x in v] if isinstance(v, list) and len(v) == 4 else list(SL_TERRAIN_DEFAULT[k])
        ok = [u for u in c.get("detail") or [] if u and u != "00000000-0000-0000-0000-000000000000"]
        if len(ok) < 4: c.update({k: v for k, v in SL_TERRAIN_DEFAULT.items() if k == "detail"}); c["default"] = True
        c["paths"] = []
        for u in c["detail"]:
            try: c["paths"].append(fetch(u))
            except Exception: c["paths"].append(None)
    return comps

def mesher_args(d, mode, me, far, radius=96):
    """far: everything to the export radius (photo hd: the profile's draw distance, 96-256 m), full LOD inside 30 m"""
    return [d, "12", "avatar"] if mode == "self" else [d, "12", "all", ",".join(str(v) for v in me)] + ([str(int(max(96, radius or 96))), "--far=30"] if far else ["30", "--roots=32"])

def mesher_env(base):
    """The text client runs under a 512 MB .NET heap cap (DOTNET_GCHeapHardLimit) and look.py inherits it; a crowd mesh
    needs ~2 GB (Warehouse 21, ~50 avatars: out of memory at 512 MB and 1.5 GB, ok at 2 GB). Give the mesher its own 3 GB cap."""
    env = dict(base)
    env["DOTNET_GCHeapHardLimit"] = os.environ.get("GT_MESHER_HEAP", "0xC0000000")
    env["DOTNET_gcServer"] = "0"
    return env

def mesher_error(text):
    """The exception line itself (a .NET trace's tail is just Parallel.ForEach frames), then the tail."""
    lines = [l.strip() for l in (text or "").splitlines() if l.strip()]
    exc = next((l for l in lines if "Exception" in l and not l.startswith("at ")), "")
    return ((exc[:300] + " | ") if exc else "") + (text or "")[-300:]

def bake_keys(meta, doc):
    """Exact bake-<key>.j2c keys needed: batch bake: refs + stand-in heads for placeholder others."""
    keys = set()
    for b in meta.get("batches", []):
        t = b.get("tex") or ""
        if t.startswith("bake:"): keys.add(t[5:])
    return keys  # stand-ins are neutral grey (no head bake)

def bake_cache_key(data, cap):
    """Decoded-bake cache name: content hash of the .j2c + the thumbnail cap (a bake's bytes change when its avatar re-bakes)."""
    import hashlib
    return f"{hashlib.sha1(data).hexdigest()[:24]}.{int(cap)}.png"

def place(src, dst):
    """hard link (same inode and mtime, so the alpha index entry stays valid), else copy"""
    try:
        if os.path.lexists(dst): os.remove(dst)
        os.link(src, dst)
    except OSError:
        shutil.copyfile(src, dst)

def cached_bakes(d, only, cap, work):
    """bake-<key>.j2c in export d -> work/tex/bake-<key>.png, decoding only bakes not seen before (work/bakecache).
    Warehouse 21: ~300 bakes for 50 avatars took 12-14 s to decode on every look. -> (written, cache hits)"""
    import glob, concurrent.futures as cf
    cache = f"{work}/bakecache"; os.makedirs(cache, exist_ok=True)
    todo, hits, n = [], 0, 0
    have = {os.path.basename(f)[5:-4] for f in glob.glob(f"{d}/bake-*.j2c")}
    for old in glob.glob(f"{work}/tex/bake-*.png"):  # 2026-10-11: drop bakes from earlier exports (stale alpha hid David's legs)
        if os.path.basename(old)[5:-4].split(".")[0] not in have: os.remove(old)
    for f in glob.glob(f"{d}/bake-*.j2c"):
        k = os.path.basename(f)[5:-4]
        if only is not None and k not in only or not handler.BAKE_KEY.fullmatch(k): continue
        data = open(f, "rb").read(); c = f"{cache}/{bake_cache_key(data, cap)}"
        if os.path.exists(c): place(c, f"{work}/tex/bake-{k}.png"); hits += 1; n += 1
        else: todo.append((k, data, c))
    if todo:  # processes: decode + thumbnail + PNG encode hold the GIL long enough that threads managed ~2x on 8 cores
        with cf.ProcessPoolExecutor(min(8, os.cpu_count() or 4)) as ex:
            for (k, _, c), ok in zip(todo, ex.map(_decode_bake_to, [(data, c, cap) for _, data, c in todo])):
                if ok: place(c, f"{work}/tex/bake-{k}.png"); n += 1
    return n, hits

def _decode_bake_to(args):
    data, c, cap = args
    im = make_job.decode_bake_bytes(data, cap)
    if im is None: return False
    tmp = f"{c}.{os.getpid()}.tmp.png"; im.convert("RGBA").save(tmp); os.replace(tmp, c)
    return True

def alpha_from_extrema(lo, hi):
    """[has cut-out, max alpha] as render_mesh's pixel scan computes them on floats v/255: any texel < 0.98, max"""
    return [lo < 250, hi / 255.0]

def alpha_stats_png(path):
    from PIL import Image
    with Image.open(path) as im:
        if im.mode not in ("RGBA", "LA", "PA") and "transparency" not in im.info: return [False, 1.0]
        return alpha_from_extrema(*im.convert("RGBA").getchannel("A").getextrema())

def index_fresh(entry, st):
    """an alpha-index entry [size, mtime_ns, cut, amax] still describes the file with stat st"""
    return bool(entry) and entry[0] == st.st_size and entry[1] == st.st_mtime_ns

def write_manifest(work, paths):
    """work/tex-manifest.json: texture/bake key -> file, file name -> alpha stats (index kept across looks in
    work/alpha-index.json, so a texture is scanned once ever). render_mesh then never reads pixels in Python."""
    import concurrent.futures as cf
    ix_path = f"{work}/alpha-index.json"
    try: index = json.load(open(ix_path))
    except (OSError, ValueError): index = {}
    files = sorted(set(paths.values())); alpha = {}; todo = []
    for f in files:
        try: st = os.stat(f)
        except OSError: continue
        e = index.get(os.path.basename(f))
        if index_fresh(e, st): alpha[os.path.basename(f)] = e[2:]
        else: todo.append((f, st))
    def one(t):
        f, st = t
        try: return os.path.basename(f), [st.st_size, st.st_mtime_ns, *alpha_stats_png(f)]
        except Exception: return os.path.basename(f), None
    with cf.ThreadPoolExecutor(min(8, os.cpu_count() or 4)) as ex:
        for name, e in ex.map(one, todo):
            if e: index[name] = e; alpha[name] = e[2:]
    if todo:
        json.dump(index, open(ix_path + ".tmp", "w")); os.replace(ix_path + ".tmp", ix_path)
    json.dump({"paths": paths, "alpha": alpha}, open(f"{work}/tex-manifest.json", "w"))
    return len(todo)

def export_stages(text):
    """Look.cs export sub-stages "prims=1.2(20696),bakes=3.4(...)" -> {"prims": 1.2, ...} (details dropped; not summed into the total)"""
    out = {}
    for part in re.findall(r"(\w+)=([0-9.]+)", text or ""):
        out[part[0]] = float(part[1])
    return out

def crowd_summary(others):
    """mesh.json others -> how many rendered complete vs as stand-ins (still loading, or no attachments in the export)"""
    stand = [o for o in others if o.get("placeholder")]
    return {"complete": len(others) - len(stand), "stand_in": len(stand), "stand_in_names": [o.get("name") or o.get("group", "?") for o in stand]}

def main(a):
    fast, far = "--fast" in a, "--far" in a; a = [x for x in a if x not in ("--fast", "--far")]
    hd_arg = next(((x.split("=", 1)[1] if "=" in x else "") for x in a if x == "--hd" or x.startswith("--hd=")), None)
    a = [x for x in a if not (x == "--hd" or x.startswith("--hd="))]
    profile = next((x.split("=", 1)[1] for x in a if x.startswith("--profile=")), "highplus")
    a = [x for x in a if not x.startswith("--profile=")]
    sky = next((x.split("=", 1)[1] for x in a if x.startswith("--sky=")), None); a = [x for x in a if not x.startswith("--sky=")]
    if sky and hd_arg is None: sys.exit("--sky needs --hd (her own looks never use the environment)")
    vw = next((x.split("=", 1)[1] for x in a if x.startswith("--views=")), ""); a = [x for x in a if not x.startswith("--views=")]
    try: bviews = body_views(vw)
    except ValueError as e: sys.exit(str(e))
    if len(a) < 2 or a[1] not in ("view", "self", "around", "at") or (a[1] == "at") != (len(a) > 2): sys.exit(__doc__)
    d, mode, target = a[0], a[1], " ".join(a[2:]).strip().lower()
    try: hd, hd_env = hd_settings(hd_arg, mode, profile)
    except ValueError as e: sys.exit(str(e))
    if sky and sky.lower() != "shared" and sky.lower() not in llsky.presets():
        sys.exit(f"unknown sky preset '{sky}' (shared, {', '.join(sorted(llsky.presets()))})")
    if hd: fast = False; os.environ.update({k: v for k, v in hd_env.items() if k.startswith("GT_TEX_CAP")})
    doc = json.load(open(f"{d}/scene.json")); me = doc["me"]["pos"]; t0 = time.time(); times = {}
    # photo hd: always the real surroundings (David 2026-10-13: no studio backdrop, `self` too): scene path with
    # terrain, water, sky from the export's environment; the 96 m backdrop when the export reaches that far
    photo_self = bool(hd) and mode == "self"
    if hd: far = far or float(doc.get("radius") or 0) >= 96; terrain_hd = True
    else: terrain_hd = False
    mmode = "view" if photo_self else mode
    # her surroundings: whole objects reaching within 30 m (Highest LOD within 12); with --far also objects out to 96 m,
    # those centred beyond 30 m as backdrop at a screen-size LOD; `self` meshes her alone. --roots=32: the 32 m export's
    # linksets even from a 96 m export (an older client that still exports 96 m)
    args = mesher_args(d, mmode, me, far, float(doc.get("radius") or 96))
    if mode == "at":  # she turns her head toward the target (scene-mesher --look); an avatar's head ~0.7 m above its agent position
        av = next((v for v in doc.get("avatars", []) if target in str(v.get("name", "")).lower() and "pos" in v), None)
        obj = next((p for p in doc["prims"] if target in str(p.get("name", "")).lower() and "world_pos" in p), None)
        pt = [av["pos"][0], av["pos"][1], av["pos"][2] + 0.7] if av else obj["world_pos"] if obj else None
        if pt: args.append("--look=" + ",".join(str(v) for v in pt))
    r = subprocess.run(["nice", "-n", "10", DOTNET, MESHER] + args, capture_output=True, text=True, env=mesher_env(os.environ))
    if r.returncode: sys.exit("scene-mesher failed: " + mesher_error(r.stderr or r.stdout))
    mesh_warn = [l for l in (r.stderr or "").splitlines() if l.startswith("warning:")]
    times["mesh"] = round(time.time() - t0, 1)
    meta = json.load(open(f"{d}/mesh.json")); meta["backdrop"] = far or bool(hd)   # photo hd: the sky gradient too
    # environment (David 2026-10-13): photo hd follows the shared (parcel/region) EEP environment, or the named preset
    # (--sky=sunset); her own quick looks ignore it entirely: fixed neutral sun + sky, whatever the time of day
    meta["env"] = env_for(doc, hd, sky)
    if far or terrain_hd: meta["water_height"] = doc.get("water_height")
    os.makedirs(f"{WORK}/tex", exist_ok=True); shutil.move(f"{d}/mesh.bin", f"{WORK}/mesh.bin")
    tj = (far or terrain_hd) and doc.get("terrain") or {"x0": 0, "y0": 0, "step": 1, "nx": 0, "ny": 0, "heights": []}  # region heightmap (exports 2026-10-04+)
    if terrain_hd: tj = {**tj, "composition": terrain_composition(doc, f"{WORK}/tex")}
    json.dump(tj, open(f"{WORK}/terrain.json", "w"))
    # bakes: only agents in this mesh (not every bake-*.j2c in a crowded export — was ~55–100 s)
    t = time.time()
    bake_cap = 2048 if hd else 256 if mode == "around" else (512 if fast else 1024)
    only = bake_keys(meta, doc)
    n_bake, bake_hits = cached_bakes(d, only, bake_cap, WORK)
    times["bakes"] = round(time.time() - t, 1)
    json.dump(meta, open(f"{WORK}/mesh.json", "w"))
    # around: diffuse only, smaller caps (nav: who's there / obstacles — not fabric normal maps)
    if mode == "around":
        os.environ.setdefault("GT_TEX_MAPS", "0")
        os.environ.setdefault("GT_TEX_CAP_AVATAR", "512")
        os.environ.setdefault("GT_TEX_CAP_SCENE", "256")
    info = {}; err = handler.fetch_textures(meta, info, WORK)
    if err: sys.exit(err)
    times["textures"] = info["textures"]["seconds"]
    t = time.time()
    bake_paths = {"bake:" + k: f"{WORK}/tex/bake-{k}.png" for k in only if os.path.exists(f"{WORK}/tex/bake-{k}.png")}
    n_scanned = write_manifest(WORK, {**info.get("tex_paths", {}), **bake_paths})
    times["manifest"] = round(time.time() - t, 1)
    view = "portrait" if photo_self else VIEWS.get(mode)
    if mode == "at":  # an avatar's name (mesh.json "others") or an object's name (scene.json root prims), first substring match
        who = next((o["name"] for o in meta.get("others", []) if target in o["name"].lower()), None)
        obj = next((p for p in doc["prims"] if target in str(p.get("name", "")).lower() and "world_pos" in p), None)
        if who: view = "at:" + who
        elif obj: view = "at:" + ",".join(str(v) for v in obj["world_pos"])
        else:
            near = [o["name"] for o in meta.get("others", [])] + sorted({p["name"] for p in doc["prims"] if p.get("name")})[:20]
            sys.exit(f"nothing called '{target}' in view; avatars/objects: {', '.join(near) or 'none'}")
    out_dir = PHOTO_OUT if hd else OUT
    os.makedirs(out_dir, exist_ok=True); stamp = time.strftime("%Y%m%d-%H%M%S"); outs = []
    for kind in ((["body"] if bviews else ["face", "body"]) if mode == "self" and not hd else ["scene"]):
        out = f"{out_dir}/{'photo' if hd else 'look'}-{stamp}-{mode}{'-' + kind if mode == 'self' and not hd else ''}.{'png' if hd else 'jpg'}"; t = time.time()
        env = {**os.environ, "GT_SAMPLES": "8" if fast else "12" if mode == "around" else "48", **({"GT_VIEW": view} if view else {}),
               **({"GT_BODY_VIEWS": bviews} if kind == "body" and bviews else {}), **hd_env}
        if mode == "around":
            env.setdefault("GT_RES", "640x360")
            env.setdefault("GT_NOSKY", "1")
            env.setdefault("GT_DENOISE", "0")
            env.setdefault("GT_MIN_TRIS", "12")  # drop dust; keep chairs/walls/avatars
        for attempt in range(2):  # Blender 4.2 has segfaulted once in Mesh.update() on a crowd scene (tbb); the rerun was clean
            if attempt: env = {**env, **retry_env(r.returncode, hd)}
            r = subprocess.run(["nice", "-n", "10", BLENDER, "-b", "--factory-startup", "-noaudio", "-t", "0", "--python",
                                f"{HERE}/cloud-render/render_mesh.py", "--", WORK, kind, "CYCLES", out], capture_output=True, text=True, env=env)
            if r.returncode >= 0: break
        lines = r.stdout.splitlines()
        got = [l.split()[-1] for l in lines if l.startswith("GT: render seconds")]
        for l in lines:
            if l.startswith("GT: avatar omitted") or l.startswith("GT: avatar stand-in"): print(l[4:], file=sys.stderr)
        hdr_files = [x for l in lines if l.startswith("GT: hdr master") for x in l.split()[3:5]]   # photo hd: EXR + .hdr masters
        rend = [float(l.split()[3]) for l in lines if l.startswith("GT: render seconds")]
        build = next((float(l.split()[3]) for l in lines if l.startswith("GT: build seconds")), None)
        if r.returncode or not got: sys.exit(f"blender {kind} failed: " + "\n".join((r.stdout + r.stderr).splitlines()[-8:]))
        if hd:   # high-quality JPG next to each PNG master
            from PIL import Image
            for p in list(got):
                Image.open(p).convert("RGB").save(p[:-4] + ".jpg", quality=95, subsampling=0, optimize=True); got.append(p[:-4] + ".jpg")
        got += hdr_files
        outs += got; times[kind] = round(time.time() - t, 1)
        if build is not None: times["build"] = build
        times["render_views"] = rend
    for p in outs: print(p)
    crowd = crowd_summary(meta.get("others", []))
    if crowd["complete"] + crowd["stand_in"]:
        print(f"avatars: {crowd['complete']} complete, {crowd['stand_in']} still loading (grey stand-ins)"
              + (": " + ", ".join(crowd["stand_in_names"][:8]) + (" ..." if crowd["stand_in"] > 8 else "") if crowd["stand_in"] else ""))
    pre = dict(kv.split("=") for kv in os.environ.get("GT_LOOK_PRE_S", "").split(",") if "=" in kv)  # Look.cs: wait, export
    for k_, v_ in pre.items(): times["client_" + k_] = float(v_)
    if os.environ.get("GT_LOOK_EXPORT_S"): times["client_export_stages"] = export_stages(os.environ["GT_LOOK_EXPORT_S"])
    print(json.dumps({"mode": mode, "target": view if mode == "at" else None, "fast": fast, "far": far, "hd": hd, "seconds": times, "total": round(time.time() - t0, 1), "total_with_client": round(time.time() - t0 + sum(float(v_) for v_ in pre.values()), 1),
                      "others": [o["name"] for o in meta.get("others", [])], "avatars": crowd, "textures": info["textures"], "bakes": n_bake, "bake_cache_hits": bake_hits}
                     | ({"mesh_warnings": mesh_warn} if mesh_warn else {})))

if __name__ == "__main__":
    main(sys.argv[1:])
