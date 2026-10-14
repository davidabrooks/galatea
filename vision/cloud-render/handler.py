"""Runpod Serverless handler: graphics-driver diagnostics + Blender test renders of an SL scene package.

Input: {"scene": {...gather_scene.py JSON...}, "renders": ["scene", "face"], "eevee_test": true}
Output: diagnostics, per-render timings and JPEG images (base64). Textures are fetched by UUID from SL's
public asset CDN and decoded with Pillow (JPEG 2000) at reduced resolution.
"""
import base64, math, concurrent.futures as cf, glob, gzip, io, json, lzma, os, re, subprocess, time, urllib.request
from PIL import Image

CDN = "http://asset-cdn.glb.agni.lindenlab.com/?texture_id="

CDN_HOST = "asset-cdn.glb.agni.lindenlab.com"
_CDN_IPS = None
def _cdn_ips():
    """2026-10-11: box egress blocks connections to DNS names (system DNS -> 198.18.0.1), so resolve the CDN by
    DNS-over-HTTPS to 1.1.1.1 (by IP) and GET the IP with a Host header; empty list = use the name (other hosts)."""
    global _CDN_IPS
    if _CDN_IPS is None:
        try:
            r = urllib.request.Request("https://1.1.1.1/dns-query?name=" + CDN_HOST + "&type=A", headers={"accept": "application/dns-json"})
            _CDN_IPS = [a["data"] for a in json.load(urllib.request.urlopen(r, timeout=8)).get("Answer", []) if a.get("type") == 1]
        except Exception:
            _CDN_IPS = []
    return _CDN_IPS
def _cdn_get(uuid):
    for ip in _cdn_ips()[:2]:
        try:
            return urllib.request.urlopen(urllib.request.Request("http://" + ip + "/?texture_id=" + uuid, headers={"Host": CDN_HOST}), timeout=20).read()
        except Exception:
            pass
    return urllib.request.urlopen(CDN + uuid, timeout=20).read()
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
        to_png(_cdn_get(uuid), path, cap)
        return uuid, path, None
    except Exception as e:
        best = cached_fallback(uuid, outdir)  # CDN down/offline: the largest size already cached beats plain colour
        return (uuid, best, None) if best else (uuid, None, str(e)[:120])

def cached_fallback(uuid, outdir):
    """Largest cached <uuid>.<cap>.png (e.g. a 1024 copy when an HD photo asks for 4096 and the CDN can't be reached)."""
    best = None
    try:
        for f in os.listdir(outdir):
            if f.startswith(uuid + ".") and f.endswith(".png") and f[len(uuid) + 1:-4].isdigit():
                c = int(f[len(uuid) + 1:-4])
                if best is None or c > best[0]: best = (c, os.path.join(outdir, f))
    except OSError:
        return None
    return best[1] if best else None

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

# 2026-10-14 photo hd (Ultra, 256 m, 4591 prims, 3 neighbour regions): every scene face at 2048 px drove Blender to
# 7 GB while "Updating Images" and the memory guard killed it twice. The viewer never loads that: it streams each
# texture at the discard level its on-screen size needs. Mirrored here from secondlife/viewer (develop, 2026-10):
#  LLFace::calcPixelArea / LLPipeline::calcPixelArea: app_angle = atan(radius / dist), pixel radius = app_angle *
#    sCurPixelAngle (view height px / vertical FOV rad), pixel area = pi r^2; dist < 16 m ramps (d/16)^2*16
#  LLFace::getTextureVirtualSize: texel_area = |repeats * 0.5|^2 * pi; vsize = pixel area / clamp(texel_area, 1/64, 128)
#  LLViewerFetchedTexture::processTextureStats: discard = floor(log4(texels / vsize)), min discard 1 above
#    max_tex_res (RenderMaxTextureResolution 2048) -> loaded edge = smallest power of two >= sqrt(vsize), <= 2048
#  LLViewerTextureList::updateImageDecodePriority: sDesiredDiscardBias b >= 1 divides vsize by round(4^(b-1));
#    low system memory slams b to 1.5 and raises it until memory fits (LLViewerTexture::updateClass)
# The 4K output is the "screen" (sCurPixelAngle = output height / 60 deg FOV), her position the camera.
VIEW_FOV = math.radians(60.0)
MIN_TEX = 64

def pixel_area(dist, radius, height_px, fov=VIEW_FOV):
    """LLPipeline::calcPixelArea: on-screen area (px^2) of a bounding sphere `radius` m at `dist` m"""
    if dist < 16.0: dist = max((dist / 16.0) ** 2 * 16.0, 1e-3)
    r_px = math.atan(radius / dist) * (height_px / fov)
    return math.pi * r_px * r_px

def virtual_size(dist, radius, repeats, height_px, bias=0.0):
    """LLFace::getTextureVirtualSize (+ the discard-bias scaler of updateImageDecodePriority)"""
    ru, rv = repeats
    texel_area = ((ru * 0.5) ** 2 + (rv * 0.5) ** 2) * math.pi
    v = pixel_area(dist, radius, height_px) / min(max(texel_area, 1 / 64), 128.0)
    if bias >= 1.0: v /= round(4 ** (bias - 1.0))
    return v

def discard_cap(vsize, max_res=2048):
    """Pure: the texture edge the viewer would load for this virtual size: smallest power of two >= sqrt(vsize)
    (discard = floor(log4(texels / vsize))), clamped to [MIN_TEX, max_res]"""
    edge = MIN_TEX
    while edge < max_res and edge * edge < vsize: edge *= 2
    return min(edge, max_res)

def batch_geometry(meta, work):
    """scene/far batch index -> (dist m from her, bounding radius m, (u, v) repeats from the UV extent); {} without
    mesh.bin / her position (mesh.bin positions are region coordinates for scene/far batches)"""
    me = meta.get("me"); path = os.path.join(work, "mesh.bin")
    if not me or not os.path.exists(path): return {}
    import numpy as np
    buf = np.memmap(path, np.uint8, "r"); out = {}; m = np.asarray(me[:3], np.float32)
    for i, b in enumerate(meta["batches"]):
        if b.get("group") not in ("scene", "far") or not b.get("nv"): continue
        try:
            nv, o = b["nv"], b["offset"]
            P = np.frombuffer(buf, np.float32, nv * 3, o).reshape(-1, 3)
            T = np.frombuffer(buf, np.float32, nv * 2, o + nv * 24).reshape(-1, 2)
            lo, hi = P.min(0), P.max(0); c = (lo + hi) / 2
            rep = np.clip(T.max(0) - T.min(0), 1e-3, 1e4)
            out[i] = (float(np.linalg.norm(c - m)), float(np.linalg.norm(hi - lo)) / 2, (float(rep[0]), float(rep[1])))
        except (ValueError, KeyError): continue
    return out

def viewer_cap(geom, cap, height_px, bias=0.0):
    """Pure: scene face texture edge per the viewer's discard logic; unknown geometry -> cap"""
    if geom is None: return cap
    d, r, rep = geom
    return discard_cap(virtual_size(d, r, rep, height_px, bias), cap)

def tex_mem_mb(want):
    """Estimate of Blender + Cycles RAM for these textures: RGBA 8-bit, held twice (Blender image + Cycles copy)."""
    return sum(c * c * 4 * 2 for c in want.values()) / 2 ** 20

def fetch_textures(meta, out, work):
    """Every texture the batches reference (CDN, capped), into work/tex; None or an error string."""
    max_tex = int(os.environ.get("GT_MAX_TEXTURES", str(MAX_TEXTURES)))
    maps = os.environ.get("GT_TEX_MAPS", "1") != "0"  # 0 = diffuse only (look around navigation)
    av_cap = int(os.environ.get("GT_TEX_CAP_AVATAR", "1024"))
    sc_cap = int(os.environ.get("GT_TEX_CAP_SCENE", "512"))
    want = {}
    av_dist = crowd_distances(meta)
    tiered = os.environ.get("GT_TEX_DIST", "0") == "1"   # photo hd: viewer discard levels from on-screen size
    bias = float(os.environ.get("GT_TEX_BIAS", "0"))
    height = int((os.environ.get("GT_RES", "3840x2160").split("x") + ["2160"])[1])
    geom = batch_geometry(meta, work) if tiered else {}
    budget = float(os.environ.get("GT_TEX_BUDGET_MB", "0") or 0)
    def collect(bias):
        want = {}
        for i, b in enumerate(meta["batches"]):
            mat = b.get("mat") or {}
            scene = b["group"] in ("scene", "far")
            scap = viewer_cap(geom.get(i), sc_cap, height, bias) if tiered and scene else sc_cap
            pairs = [(b["tex"], crowd_cap(av_dist.get(b["group"]), av_cap) if b["group"].startswith("avatar") else scap)]
            if maps:
                pairs += [(mat.get(k), min(512, scap if scene else sc_cap)) for k in ("normal", "spec", "mr", "emissive_tex")]
            for t, cap in pairs:
                if isinstance(t, str) and UUID.match(t): want[t] = max(want.get(t, 0), cap)
        return want
    want = collect(bias); est0 = tex_mem_mb(want)
    # LLViewerTexture::updateClass under low memory: bias to >= 1.5, then up until it fits (here: in 0.5 steps, max 5)
    while tiered and budget and tex_mem_mb(want) > budget and bias < 5.0:
        bias = max(1.5, bias + 0.5); want = collect(bias)
    os.environ["GT_TEX_BIAS"] = f"{bias:g}"   # the bias in effect: a lighter retry starts above it
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
                       "seconds": round(time.time() - t, 1), "workers": workers, "maps": maps, "caps": [av_cap, sc_cap],
                       "est_mb": round(tex_mem_mb(want)), "est_mb_before_budget": round(est0), "viewer_discard": tiered,
                       "discard_bias": bias, "budget_mb": budget,
                       "sizes": {str(c): n for c, n in sorted(__import__("collections").Counter(want.values()).items())}}
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
