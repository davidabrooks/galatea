#!/usr/bin/env python3
"""Pack a `scene export` dir (after scene-mesher) into a Runpod job input for render_mesh.py.
usage: make_job.py <export dir> <terrain.json|-> <renders,comma,separated> out.json [scene camera x,y,z,tx,ty,tz,lens] [avatar x,y,yaw]
Bakes are 5-channel JPEG 2000 (RGBA + 1 extra): decoded here with imagecodecs (Pillow and LibreMetaverse's CoreJ2K
mis-read them), RGBA as WebP, max 1024 px. Needs: /home/box/tools/imgvenv (pip: imagecodecs pillow).
Also adds mesh_json["env"]: the region/parcel EEP sky interpolated at export time (llsky.py).
"""
import base64, glob, io, json, lzma, os, sys
import imagecodecs
import llsky
from PIL import Image

def decode_bake_bytes(data, cap):
    """one bake .j2c (5-channel: RGBA + a mask) -> RGBA Pillow thumbnail, or None"""
    a = imagecodecs.jpeg2k_decode(data)
    if a.ndim == 3 and a.shape[0] > 8:
        im = Image.fromarray(a[..., :4]); im.thumbnail((cap, cap)); return im
    return None
def decode_bakes(d, only=None, cap=1024, workers=None):
    """bake-<key>.j2c -> {key: RGBA Pillow}. only=iterable of keys (or prefixes like 'd2d5be85') to decode;
    None = all. Parallel workers default min(8, cpu). Cap thumbnails for look-around speed."""
    import concurrent.futures as cf
    files = glob.glob(f"{d}/bake-*.j2c")
    if only is not None:
        want = set(only)
        files = [f for f in files if os.path.basename(f)[5:-4] in want]
    def one(f):
        return os.path.basename(f)[5:-4], decode_bake_bytes(open(f, "rb").read(), cap)
    res = {}
    n = workers or min(8, max(1, (os.cpu_count() or 4)))
    with cf.ThreadPoolExecutor(n) as ex:
        for key, im in ex.map(one, files):
            if im is not None: res[key] = im
    return res
def eep(doc):
    """Ground-sky track of the region (or parcel) day cycle at export time, as the SL viewer computes it: llsky.env_at."""
    return llsky.env_at(doc)
if __name__ == "__main__":
    d, terrain, renders, out = sys.argv[1:5]
    meta = json.load(open(f"{d}/mesh.json"))
    bakes = {}
    for k, im in decode_bakes(d).items():
        b = io.BytesIO(); im.save(b, "WEBP", quality=90); bakes[k] = base64.b64encode(b.getvalue()).decode()
    meta["env"] = eep(json.load(open(f"{d}/scene.json")))
    t = json.load(open(terrain))["terrain"] if terrain != "-" else {"x0": 0, "y0": 0, "step": 1, "nx": 0, "ny": 0, "heights": []}
    job = {"renders": renders.split(","), "cameras": {"scene": sys.argv[5]} if len(sys.argv) > 5 else {},
           "avatar_at": {"scene": sys.argv[6]} if len(sys.argv) > 6 else {}, "mesh_job": {"mesh_json": meta, "terrain": t, "bakes": bakes,
           "mesh_bin_xz_b64": base64.b64encode(lzma.compress(open(f"{d}/mesh.bin", "rb").read())).decode()}}  # xz: ~30% under gzip; /run caps ~10 MB
    json.dump(job, open(out, "w"))
    print(f"{out}: {os.path.getsize(out) / 1e6:.1f} MB, bakes {sorted(bakes)}, env {meta['env']}")
