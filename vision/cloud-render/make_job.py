#!/usr/bin/env python3
"""Pack a `scene export` dir (after scene-mesher) into a Runpod job input for render_mesh.py.
usage: make_job.py <export dir> <terrain.json|-> <renders,comma,separated> out.json [scene camera x,y,z,tx,ty,tz,lens] [avatar x,y,yaw]
Bakes are 5-channel JPEG 2000 (RGBA + 1 extra): decoded here with imagecodecs (Pillow and LibreMetaverse's CoreJ2K
mis-read them), RGBA as WebP, max 1024 px. Needs: /home/box/tools/imgvenv (pip: imagecodecs pillow).
Also adds mesh_json["env"]: the region EEP sky interpolated at export time (sun dir from the simulator).
"""
import base64, datetime, glob, io, json, lzma, os, sys
import imagecodecs
from PIL import Image

d, terrain, renders, out = sys.argv[1:5]
meta = json.load(open(f"{d}/mesh.json"))
bakes = {}
for f in glob.glob(f"{d}/bake-*.j2c"):
    a = imagecodecs.jpeg2k_decode(open(f, "rb").read())
    if a.ndim == 3 and a.shape[0] > 8:  # 8x8-ish placeholders = unused bake slot
        im = Image.fromarray(a[..., :4]); im.thumbnail((1024, 1024)); b = io.BytesIO(); im.save(b, "WEBP", quality=90)
        bakes[os.path.basename(f)[5:-4]] = base64.b64encode(b.getvalue()).decode()
def eep(doc):
    """Ground-sky track of the region day cycle at export time (LL: (now + day_offset) % day_length), lerped."""
    try:
        e = doc["environment"]["environment"]; dc = e["day_cycle"]
        at = datetime.datetime.fromisoformat(doc["exported_at"][:26] + doc["exported_at"][-6:]).timestamp()
        frac = ((at + e["day_offset"]) % e["day_length"]) / e["day_length"]
        keys = sorted(((k.get("key_keyframe") or 0.0), dc["frames"][k["key_name"]]) for k in dc["tracks"][1])
    except (KeyError, TypeError, ValueError, IndexError):
        return None
    z = lambda v: [x or 0.0 for x in v]  # OSD JSON writes 0 as null
    i = max(j for j, (t, _) in enumerate(keys) if t <= frac); (t0, a), (t1, b) = keys[i], keys[(i + 1) % len(keys)]
    w = (frac - t0) / (((t1 - t0) % 1.0) or 1.0)
    mix = lambda f: [x + (y - x) * w for x, y in zip(z(f(a)), z(f(b)))]
    hz = lambda f: f.get("legacy_haze", {}).get("blue_horizon", [0.3, 0.4, 0.6])
    return {"frac": frac, "sun_dir": z(doc.get("sun_dir", [0, 0, 1])), "sunlight": mix(lambda f: f["sunlight_color"])[:3],
            "horizon": mix(hz)[:3], "moon": (a.get("moon_brightness") or 0.0) * (1 - w) + (b.get("moon_brightness") or 0.0) * w}
meta["env"] = eep(json.load(open(f"{d}/scene.json")))
t = json.load(open(terrain))["terrain"] if terrain != "-" else {"x0": 0, "y0": 0, "step": 1, "nx": 0, "ny": 0, "heights": []}
job = {"renders": renders.split(","), "cameras": {"scene": sys.argv[5]} if len(sys.argv) > 5 else {},
       "avatar_at": {"scene": sys.argv[6]} if len(sys.argv) > 6 else {}, "mesh_job": {"mesh_json": meta, "terrain": t, "bakes": bakes,
       "mesh_bin_xz_b64": base64.b64encode(lzma.compress(open(f"{d}/mesh.bin", "rb").read())).decode()}}  # xz: ~30% under gzip; /run caps ~10 MB
json.dump(job, open(out, "w"))
print(f"{out}: {os.path.getsize(out) / 1e6:.1f} MB, bakes {sorted(bakes)}, env {meta['env']}")
