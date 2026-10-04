"""Runpod Serverless handler: graphics-driver diagnostics + Blender test renders of an SL scene package.

Input: {"scene": {...gather_scene.py JSON...}, "renders": ["scene", "face"], "eevee_test": true}
Output: diagnostics, per-render timings and JPEG images (base64). Textures are fetched by UUID from SL's
public asset CDN and decoded with Pillow (JPEG 2000) at reduced resolution.
"""
import base64, concurrent.futures as cf, glob, gzip, io, json, os, re, subprocess, time, urllib.request
import runpod
from PIL import Image

CDN = "http://asset-cdn.glb.agni.lindenlab.com/?texture_id="
BLENDER = os.environ.get("BLENDER", "blender")
WORKER_T0 = time.time()
UUID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")  # trust boundary: ids become paths and URLs
KINDS = {"scene", "face", "body"}
BAKE_NAMES = {"head", "upper", "lower", "eyes", "skirt", "hair", "leftarm", "leftleg", "aux1", "aux2", "aux3"}
MAX_TEXTURES = 200

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
    try: im.reduce = 2 if max(im.size) >= 2 * cap else (1 if max(im.size) > cap else 0)
    except Exception: pass
    im = im.convert("RGBA"); im.thumbnail((cap, cap)); im.save(path)

def fetch_tex(uuid, outdir, cap=512):
    path = os.path.join(outdir, uuid + ".png")
    if os.path.exists(path): return uuid, path, None
    try:
        data = urllib.request.urlopen(CDN + uuid, timeout=20).read()
        im = Image.open(io.BytesIO(data))
        try: im.reduce = 2 if max(im.size) >= 1024 else (1 if max(im.size) >= 512 else 0)
        except Exception: pass
        im = im.convert("RGBA"); im.thumbnail((512, 512)); im.save(path)
        return uuid, path, None
    except Exception as e:
        return uuid, None, str(e)[:120]

def blender(args, timeout, env=None):
    t = time.time()
    script = args.pop(0) if args[0].endswith(".py") else "/app/render_scene.py"
    r = subprocess.run([BLENDER, "-b", "--factory-startup", "-noaudio", "--python", script, "--"] + args,
                       capture_output=True, text=True, timeout=timeout)
    return round(time.time() - t, 1), r.returncode, (r.stdout + r.stderr)

def mesh_job(inp, out, work):
    """Real geometry from scene-mesher: mesh.json + gzipped mesh.bin + terrain + her bakes (raw j2c)."""
    mj = inp["mesh_job"]; os.makedirs(work + "/tex", exist_ok=True)
    meta = mj["mesh_json"]; json.dump(meta, open(work + "/mesh.json", "w")); json.dump(mj["terrain"], open(work + "/terrain.json", "w"))
    open(work + "/mesh.bin", "wb").write(gzip.decompress(base64.b64decode(mj["mesh_bin_gz_b64"])))
    for name, b64 in mj.get("bakes", {}).items():  # PNG, decoded on the box (5-channel bake j2c)
        if name in BAKE_NAMES: Image.open(io.BytesIO(base64.b64decode(b64))).convert("RGB").save(f"{work}/tex/bake-{name}.png")
    want = {}
    for b in meta["batches"]:
        if UUID.match(b["tex"]): want[b["tex"]] = max(want.get(b["tex"], 0), 1024 if b["group"] == "avatar" else 512)
    if len(want) > MAX_TEXTURES: return f"{len(want)} textures > {MAX_TEXTURES}"
    t = time.time()
    with cf.ThreadPoolExecutor(8) as ex:
        res = list(ex.map(lambda kv: fetch_tex(kv[0], work + "/tex", kv[1]), want.items()))
    out["textures"] = {"requested": len(want), "ok": sum(1 for r in res if r[1]), "errors": [r for r in res if r[2]][:5], "seconds": round(time.time() - t, 1)}
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
            secs, rc, log = blender(["/app/render_mesh.py", work, name, "CYCLES", img], timeout=200, env={"GT_CAM": cam} if cam else None)
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

runpod.serverless.start({"handler": handler})
