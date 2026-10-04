#!/usr/bin/env python3
"""Pack a `scene export` dir (after scene-mesher) into a Runpod job input for render_mesh.py.
usage: make_job.py <export dir> <terrain.json|-> <renders,comma,separated> out.json [scene camera x,y,z,tx,ty,tz,lens]
Bakes are 5-channel JPEG 2000 (RGB + 2 extra): decoded here with imagecodecs (Pillow and LibreMetaverse's CoreJ2K
mis-read them), RGB only, max 1024 px. Needs: /home/box/tools/imgvenv (pip: imagecodecs pillow).
"""
import base64, glob, gzip, io, json, os, sys
import imagecodecs
from PIL import Image

d, terrain, renders, out = sys.argv[1:5]
meta = json.load(open(f"{d}/mesh.json"))
bakes = {}
for f in glob.glob(f"{d}/bake-*.j2c"):
    a = imagecodecs.jpeg2k_decode(open(f, "rb").read())
    if a.ndim == 3 and a.shape[0] > 8:  # 8x8-ish placeholders = unused bake slot
        im = Image.fromarray(a[..., :3]); im.thumbnail((1024, 1024)); b = io.BytesIO(); im.save(b, "JPEG", quality=92)
        bakes[os.path.basename(f)[5:-4]] = base64.b64encode(b.getvalue()).decode()
t = json.load(open(terrain))["terrain"] if terrain != "-" else {"x0": 0, "y0": 0, "step": 1, "nx": 0, "ny": 0, "heights": []}
job = {"renders": renders.split(","), "cameras": {"scene": sys.argv[5]} if len(sys.argv) > 5 else {}, "mesh_job": {"mesh_json": meta, "terrain": t, "bakes": bakes,
       "mesh_bin_gz_b64": base64.b64encode(gzip.compress(open(f"{d}/mesh.bin", "rb").read(), 9)).decode()}}
json.dump(job, open(out, "w"))
print(f"{out}: {os.path.getsize(out) / 1e6:.1f} MB, bakes {sorted(bakes)}")
