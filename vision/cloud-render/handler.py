"""Runpod Serverless handler: graphics-driver diagnostics + Blender test renders of an SL scene package.

Input: {"scene": {...gather_scene.py JSON...}, "renders": ["scene", "face"], "eevee_test": true}
Output: diagnostics, per-render timings and JPEG images (base64). Textures are fetched by UUID from SL's
public asset CDN and decoded with Pillow (JPEG 2000) at reduced resolution.
"""
import base64, concurrent.futures as cf, glob, gzip, io, json, lzma, os, re, subprocess, time, urllib.request
from PIL import Image

CDN = "http://asset-cdn.glb.agni.lindenlab.com/?texture_id="
BLENDER = os.environ.get("BLENDER", "blender")
WORKER_T0 = time.time()
UUID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")  # trust boundary: ids become paths and URLs
KINDS = {"scene", "face", "body"}
BAKE_NAMES = {"head", "upper", "lower", "eyes", "skirt", "hair", "leftarm", "leftleg", "aux1", "aux2", "aux3"}
BAKE_KEY = re.compile(r"(?:[0-9a-f]{8}-)?(" + "|".join(sorted(BAKE_NAMES)) + ")")  # hers, or "<agent id[:8]>-<name>" for others
MAX_TEXTURES = int(os.environ.get("GT_MAX_TEXTURES", "200"))  # per job; the box `look` path raises it (local disk cache, no worker timeout)

def sh(c, timeout=60):
    try:
        r = subprocess.run(c, shell=True, capture_output=True, text=True, timeout=timeout)
        return (r.stdout + r.stderr).strip()
    except Exception as e:
        return f"ERR {e}"

def diagnostics():
    libs = sh("ldconfig -p | grep -E 'libEGL_nvidia|libGLX_nvidia|libnvoptix|libnvidia-rtcore|libnvidia-glcore|libnvidia-eglcore|libcuda.so' | awk '{print $1}' | sort -u")
    return {
        "nvidia_smi": sh("nvidia-smi --query-gpu=name,driver_version,memory.total --format=csv,noheader"),
        "NVIDIA_DRIVER_CAPABILITIES": os.environ.get("NVIDIA_DRIVER_CAPABILITIES"),
        "driver_libs": libs.splitlines(),
        "egl_vendor_files": sorted(glob.glob("/usr/share/glvnd/egl_vendor.d/*") + glob.glob("/etc/glvnd/egl_vendor.d/*")),
        "vulkan_icd_files": sorted(glob.glob("/usr/share/vulkan/icd.d/*") + glob.glob("/etc/vulkan/icd.d/*")),
        "bootstrap_seconds": os.environ.get("GT_BOOTSTRAP_SECONDS"),
    }

def to_png(data, path, cap):
    im = Image.open(io.BytesIO(data))
    r = 0
    while max(im.size) >> (r + 1) >= cap: r += 1  # JPEG 2000: skip resolution levels we would throw away anyway
    try: im.reduce = r
    except Exception: pass
    im = im.convert("RGBA"); im.thumbnail((cap, cap)); im.save(path)

def fetch_tex(uuid, outdir, cap=512):
    # Cap in the filename so a 256 look-around cache entry isn't reused as a 1024 portrait (and vice versa).
    path = os.path.join(outdir, f"{uuid}.{cap}.png")
    legacy = os.path.join(outdir, uuid + ".png")
    if os.path.exists(path): return uuid, path, None
    # reuse / downscale legacy uncappped cache (uuid.png) so a cap change is free offline
    if os.path.exists(legacy):
        try:
            with Image.open(legacy) as im:
                im = im.convert("RGBA")
                if max(im.size) <= cap:
                    return uuid, legacy, None
                im.thumbnail((cap, cap)); im.save(path)
                return uuid, path, None
        except Exception:
            pass
    try:
        to_png(urllib.request.urlopen(CDN + uuid, timeout=20).read(), path, cap)
        return uuid, path, None
    except Exception as e:
        return uuid, None, str(e)[:120]

def blender(args, timeout, env=None):
    t = time.time()
    script = args.pop(0) if args[0].endswith(".py") else "/app/render_scene.py"
    r = subprocess.run([BLENDER, "-b", "--factory-startup", "-noaudio", "--python", script, "--"] + args,
                       capture_output=True, text=True, timeout=timeout, env={**os.environ, **(env or {})})
    return round(time.time() - t, 1), r.returncode, (r.stdout + r.stderr)

def mesh_job(inp, out, work):
    """Real geometry from scene-mesher: mesh.json + xz (or gzip) mesh.bin + terrain + her bakes (raw j2c)."""
    mj = inp["mesh_job"]; os.makedirs(work + "/tex", exist_ok=True)
    meta = mj["mesh_json"]; json.dump(meta, open(work + "/mesh.json", "w")); json.dump(mj["terrain"], open(work + "/terrain.json", "w"))
    raw = base64.b64decode(mj["mesh_bin_xz_b64"]) if "mesh_bin_xz_b64" in mj else gzip.decompress(base64.b64decode(mj["mesh_bin_gz_b64"]))
    open(work + "/mesh.bin", "wb").write(lzma.decompress(raw) if "mesh_bin_xz_b64" in mj else raw)
    for name, b64 in mj.get("bakes", {}).items():  # PNG, decoded on the box (5-channel bake j2c)
        if BAKE_KEY.fullmatch(name): Image.open(io.BytesIO(base64.b64decode(b64))).convert("RGBA").save(f"{work}/tex/bake-{name}.png")  # alpha = bake cut-outs
    return fetch_textures(meta, out, work)

def crowd_distances(meta):
    """'avatar:<id8>' group -> metres from her, for other avatars in a crowd mesh (mesh.json 'others' + 'me')."""
    me = meta.get("me")
    if not me: return {}
    return {o["group"]: sum((a - b) ** 2 for a, b in zip(o["pos"], me)) ** 0.5 for o in meta.get("others", []) if o.get("pos") and o.get("group")}

def crowd_cap(dist, av_cap):
    """Texture size for another avatar's outfit by distance (Warehouse 21: ~2500 crowd textures at full size)."""
    if dist is None: return av_cap
    return av_cap if dist < 6 else min(av_cap, 256) if dist < 15 else min(av_cap, 128)

def crowd_drop_order(meta, av_dist, want):
    """Textures used only by other avatars, farthest wearer first (her own and scene textures never listed)."""
    nearest = {}
    for b in meta["batches"]:
        d = av_dist.get(b["group"]) if b["group"].startswith("avatar:") else -1.0
        for t in [b["tex"], *(b.get("mat") or {}).values()]:
            if isinstance(t, str) and t in want: nearest[t] = min(nearest.get(t, 1e9), d if d is not None else -1.0)
    return [t for t, d in sorted(nearest.items(), key=lambda kv: -kv[1]) if d >= 0]

def fetch_textures(meta, out, work):
    """Every texture the batches reference (CDN, capped), into work/tex; None or an error string."""
    max_tex = int(os.environ.get("GT_MAX_TEXTURES", str(MAX_TEXTURES)))
    maps = os.environ.get("GT_TEX_MAPS", "1") != "0"  # 0 = diffuse only (look around navigation)
    av_cap = int(os.environ.get("GT_TEX_CAP_AVATAR", "1024"))
    sc_cap = int(os.environ.get("GT_TEX_CAP_SCENE", "512"))
    want = {}
    av_dist = crowd_distances(meta)
    for b in meta["batches"]:
        mat = b.get("mat") or {}
        pairs = [(b["tex"], crowd_cap(av_dist.get(b["group"]), av_cap) if b["group"].startswith("avatar") else sc_cap)]
        if maps:
            pairs += [(mat.get(k), min(512, sc_cap)) for k in ("normal", "spec", "mr", "emissive_tex")]
        for t, cap in pairs:
            if isinstance(t, str) and UUID.match(t): want[t] = max(want.get(t, 0), cap)
    near = {t for b in meta["batches"] if b["group"] != "far" for t in [b["tex"], *(b.get("mat") or {}).values()] if isinstance(t, str)}
    if len(want) > max_tex:  # backdrop ("far") textures go first: those faces then show their plain colour
        for t in [t for t in want if t not in near][:len(want) - max_tex]: del want[t]
    if len(want) > max_tex and av_dist:  # then the farthest other avatars' outfit textures (plain colour), never hers
        for t in crowd_drop_order(meta, av_dist, want)[:len(want) - max_tex]: del want[t]
    if len(want) > max_tex: return f"{len(want)} textures > {max_tex}"
    t = time.time()
    workers = max(1, int(os.environ.get("GT_TEX_WORKERS", "8")))
    with cf.ThreadPoolExecutor(workers) as ex:
        res = list(ex.map(lambda kv: fetch_tex(kv[0], work + "/tex", kv[1]), want.items()))
    out["tex_paths"] = {r[0]: r[1] for r in res if r[1]}  # the exact file per texture (render_mesh: tex-manifest.json)
    out["textures"] = {"requested": len(want), "ok": sum(1 for r in res if r[1]), "errors": [r for r in res if r[2]][:5],
                       "seconds": round(time.time() - t, 1), "workers": workers, "maps": maps, "caps": [av_cap, sc_cap]}
    return None

def handler(job):
    t_start = time.time()
    inp = job["input"]
    out = {"worker_age_s": round(t_start - WORKER_T0, 1), "diag": diagnostics()}
    work = "/tmp/job"; os.makedirs(work + "/tex", exist_ok=True)
    renders = inp.get("renders", ["scene"])
    if not set(renders) <= KINDS: return {"error": f"renders must be a subset of {sorted(KINDS)}"}
    if "mesh_job" in inp:
        err = mesh_job(inp, out, work)
        if err: return {"error": err}
        out["renders"] = {}
        for name in renders:
            img = f"{work}/{name}-mesh.jpg"
            cam = inp.get("cameras", {}).get(name)
            if cam is not None and not re.fullmatch(r"-?[\d.]+(,-?[\d.]+){5,6}", cam): return {"error": "camera must be x,y,z,tx,ty,tz[,lens]"}
            av = inp.get("avatar_at", {}).get(name)
            if av is not None and not re.fullmatch(r"-?[\d.]+,-?[\d.]+,-?[\d.]+", av): return {"error": "avatar_at must be x,y,yaw"}
            env = {k: v for k, v in (("GT_CAM", cam), ("GT_AV", av), ("GT_EXPOSURE", str(float(inp.get("exposure", 0))))) if v}
            secs, rc, log = blender(["/app/render_mesh.py", work, name, "CYCLES", img], timeout=200, env=env)
            info = {"seconds": secs, "returncode": rc, "log_tail": [l for l in log.splitlines() if l.startswith("GT:") or "Error" in l][-25:]}
            if os.path.exists(img): info["jpeg_b64"] = base64.b64encode(open(img, "rb").read()).decode()
            out["renders"][f"{name}-mesh"] = info
        out["handler_seconds"] = round(time.time() - t_start, 1)
        return out
    scene = inp["scene"]
    for o in scene.get("objects", []):
        if o.get("texture") and not UUID.match(o["texture"]): o["texture"] = None
    uuids = sorted({o["texture"] for o in scene.get("objects", []) if o.get("texture")} | {u for u in inp.get("extra_textures", []) if UUID.match(u)})
    if len(uuids) > MAX_TEXTURES: return {"error": f"{len(uuids)} textures > {MAX_TEXTURES}"}
    t = time.time()
    with cf.ThreadPoolExecutor(8) as ex:
        res = list(ex.map(lambda u: fetch_tex(u, work + "/tex"), uuids))
    out["textures"] = {"requested": len(uuids), "ok": sum(1 for r in res if r[1]), "errors": [r for r in res if r[2]][:5],
                       "seconds": round(time.time() - t, 1)}
    json.dump(scene, open(work + "/scene.json", "w"))
    out["renders"] = {}
    for name in renders:
        for engine in ["CYCLES"] + (["EEVEE"] if inp.get("eevee_test") and name == "scene" else []):
            img = f"{work}/{name}-{engine.lower()}.jpg"
            secs, rc, log = blender([work + "/scene.json", work + "/tex", name, engine, img], timeout=150 if engine == "CYCLES" else 90)
            info = {"seconds": secs, "returncode": rc,
                    "log_tail": [l for l in log.splitlines() if l.startswith("GT:") or "Error" in l or "error" in l][-25:]}
            if os.path.exists(img):
                info["jpeg_b64"] = base64.b64encode(open(img, "rb").read()).decode()
            out["renders"][f"{name}-{engine.lower()}"] = info
    out["handler_seconds"] = round(time.time() - t_start, 1)
    return out

if __name__ == "__main__":
    import runpod
    runpod.serverless.start({"handler": handler})
