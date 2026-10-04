#!/usr/bin/env python3
"""Default path: render a make_job.py job on the box CPU (no cloud). Same inputs and Blender script as the Runpod worker.
usage: render_local.py <job.json> <outdir> [samples=128]
Runs Cycles on all cores under nice 10 (the SL text client stays responsive); prints seconds per image.
Needs: BLENDER (default /home/box/tools/blender-4.2.3-linux-x64/blender), python with pillow (imgvenv).
"""
import json, os, re, shutil, subprocess, sys, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import handler  # reuse its job unpacking + texture fetch (trust-boundary checks included)

job, outdir = json.load(open(sys.argv[1])), sys.argv[2]; samples = sys.argv[3] if len(sys.argv) > 3 else "128"
work = "/tmp/gt-render-job"; shutil.rmtree(work, ignore_errors=True); os.makedirs(work + "/tex"); os.makedirs(outdir, exist_ok=True)
info = {}; err = handler.mesh_job(job, info, work)
if err: sys.exit(err)
print("textures", info["textures"])
blender = os.environ.get("BLENDER", "/home/box/tools/blender-4.2.3-linux-x64/blender")
for name in job["renders"]:
    cam, av = job.get("cameras", {}).get(name), job.get("avatar_at", {}).get(name)
    for v in (cam, av):
        if v is not None and not re.fullmatch(r"-?[\d.]+(,-?[\d.]+)+", v): sys.exit("bad camera/avatar_at")
    env = {**os.environ, "GT_SAMPLES": samples, **({"GT_CAM": cam} if cam else {}), **({"GT_AV": av} if av else {})}
    out = f"{outdir}/{name}.jpg"; t = time.time()
    r = subprocess.run(["nice", "-n", "10", blender, "-b", "--factory-startup", "-noaudio", "-t", "0", "--python",
                        os.path.join(os.path.dirname(os.path.abspath(__file__)), "render_mesh.py"), "--", work, name, "CYCLES", out],
                       capture_output=True, text=True, env=env)
    print(name, "rc", r.returncode, f"{time.time() - t:.1f} s", out, *[l for l in r.stdout.splitlines() if l.startswith("GT: render")])
