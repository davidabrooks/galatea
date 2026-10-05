#!/usr/bin/env python3
"""`look` for the text client: a `scene export` dir -> mesh (scene-mesher) -> CPU Cycles render(s) of what's around her.
usage: look.py <export dir> {view|self|around|at} [target name] [--fast] [--far]
  view    over her shoulder, along her facing        around  4 views (front, left, back, right)
  self    her face + full body (avatar only)         at X    from beside her head toward avatar/object X
  --far   also the backdrop (scenery 30-96 m at a screen-size LOD, region terrain, water, sky gradient; needs a 96 m
          export). Off by default (David 2026-10-04: her renders are for her own needs, speed first): near scene only.
Prints one image path per line (then a JSON summary line). Read-only: never talks to SL; only CDN asset GETs.
Needs: dotnet + SCENE_MESHER (built scene-mesher), BLENDER, the imgvenv python (imagecodecs, pillow).
"""
import json, os, shutil, subprocess, sys, time
HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, f"{HERE}/cloud-render")
os.environ.setdefault("GT_MAX_TEXTURES", "800")
os.environ.setdefault("GT_TEX_WORKERS", "24")  # local disk cache; parallel CDN GETs (crowd looks were ~50 s at 8)
import handler, make_job  # texture fetch (CDN, capped) + bake decode / region sky: same code as the Runpod path

MESHER = os.environ.get("SCENE_MESHER", "/home/box/tools/scene-mesher/SceneMesher.dll")
# the text client's supervisor may not have dotnet on PATH (2026-10-04: "nice: 'dotnet': No such file"); DOTNET_ROOT is set
DOTNET = shutil.which("dotnet") or os.path.join(os.environ.get("DOTNET_ROOT", "/home/box/.dotnet"), "dotnet")
BLENDER = os.environ.get("BLENDER", "/home/box/tools/blender-4.2.3-linux-x64/blender")
OUT = os.environ.get("GT_LOOK_OUT", "/workspace/secondlife/vision/look")
WORK = os.environ.get("GT_LOOK_WORK", "/workspace/secondlife/vision/look-work")  # tex/ doubles as the CDN texture cache
VIEWS = {"view": "eye", "around": "eye;eye:90;eye:180;eye:-90"}

def mesher_args(d, mode, me, far):
    return [d, "12", "avatar"] if mode == "self" else [d, "12", "all", ",".join(str(v) for v in me)] + (["96", "--far=30"] if far else ["30", "--roots=32"])

def bake_keys(meta, doc):
    """Exact bake-<key>.j2c keys needed: batch bake: refs + stand-in heads for placeholder others."""
    keys = set()
    for b in meta.get("batches", []):
        t = b.get("tex") or ""
        if t.startswith("bake:"): keys.add(t[5:])
    for o in meta.get("others", []):
        pref = o.get("bake_prefix") or (o.get("agent_id") or "")[:8]
        if pref: keys.add(f"{pref}-head")  # stand_in head sphere
    return keys

def main(a):
    fast, far = "--fast" in a, "--far" in a; a = [x for x in a if x not in ("--fast", "--far")]
    if len(a) < 2 or a[1] not in ("view", "self", "around", "at") or (a[1] == "at") != (len(a) > 2): sys.exit(__doc__)
    d, mode, target = a[0], a[1], " ".join(a[2:]).strip().lower()
    doc = json.load(open(f"{d}/scene.json")); me = doc["me"]["pos"]; t0 = time.time(); times = {}
    # her surroundings: whole objects reaching within 30 m (Highest LOD within 12); with --far also objects out to 96 m,
    # those centred beyond 30 m as backdrop at a screen-size LOD; `self` meshes her alone. --roots=32: the 32 m export's
    # linksets even from a 96 m export (an older client that still exports 96 m)
    args = mesher_args(d, mode, me, far)
    if mode == "at":  # she turns her head toward the target (scene-mesher --look); an avatar's head ~0.7 m above its agent position
        av = next((v for v in doc.get("avatars", []) if target in str(v.get("name", "")).lower() and "pos" in v), None)
        obj = next((p for p in doc["prims"] if target in str(p.get("name", "")).lower() and "world_pos" in p), None)
        pt = [av["pos"][0], av["pos"][1], av["pos"][2] + 0.7] if av else obj["world_pos"] if obj else None
        if pt: args.append("--look=" + ",".join(str(v) for v in pt))
    r = subprocess.run(["nice", "-n", "10", DOTNET, MESHER] + args, capture_output=True, text=True)
    if r.returncode: sys.exit("scene-mesher failed: " + (r.stderr or r.stdout)[-400:])
    times["mesh"] = round(time.time() - t0, 1)
    meta = json.load(open(f"{d}/mesh.json")); meta["env"] = make_job.eep(doc); meta["backdrop"] = far
    if far: meta["water_height"] = doc.get("water_height")
    os.makedirs(f"{WORK}/tex", exist_ok=True); shutil.move(f"{d}/mesh.bin", f"{WORK}/mesh.bin")
    json.dump(far and doc.get("terrain") or {"x0": 0, "y0": 0, "step": 1, "nx": 0, "ny": 0, "heights": []}, open(f"{WORK}/terrain.json", "w"))  # region heightmap (exports 2026-10-04+)
    # bakes: only agents in this mesh (not every bake-*.j2c in a crowded export — was ~55–100 s)
    t = time.time()
    bake_cap = 256 if mode == "around" else (512 if fast else 1024)
    only = bake_keys(meta, doc)
    n_bake = 0
    for k, im in make_job.decode_bakes(d, only=only, cap=bake_cap).items():
        if handler.BAKE_KEY.fullmatch(k):
            im.convert("RGBA").save(f"{WORK}/tex/bake-{k}.png"); n_bake += 1
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
    view = VIEWS.get(mode)
    if mode == "at":  # an avatar's name (mesh.json "others") or an object's name (scene.json root prims), first substring match
        who = next((o["name"] for o in meta.get("others", []) if target in o["name"].lower()), None)
        obj = next((p for p in doc["prims"] if target in str(p.get("name", "")).lower() and "world_pos" in p), None)
        if who: view = "at:" + who
        elif obj: view = "at:" + ",".join(str(v) for v in obj["world_pos"])
        else:
            near = [o["name"] for o in meta.get("others", [])] + sorted({p["name"] for p in doc["prims"] if p.get("name")})[:20]
            sys.exit(f"nothing called '{target}' in view; avatars/objects: {', '.join(near) or 'none'}")
    os.makedirs(OUT, exist_ok=True); stamp = time.strftime("%Y%m%d-%H%M%S"); outs = []
    for kind in (["face", "body"] if mode == "self" else ["scene"]):
        out = f"{OUT}/look-{stamp}-{mode}{'-' + kind if mode == 'self' else ''}.jpg"; t = time.time()
        env = {**os.environ, "GT_SAMPLES": "8" if fast else "12" if mode == "around" else "48", **({"GT_VIEW": view} if view else {})}
        if mode == "around":
            env.setdefault("GT_RES", "640x360")
            env.setdefault("GT_NOSKY", "1")
            env.setdefault("GT_DENOISE", "0")
            env.setdefault("GT_MIN_TRIS", "12")  # drop dust; keep chairs/walls/avatars
        r = subprocess.run(["nice", "-n", "10", BLENDER, "-b", "--factory-startup", "-noaudio", "-t", "0", "--python",
                            f"{HERE}/cloud-render/render_mesh.py", "--", WORK, kind, "CYCLES", out], capture_output=True, text=True, env=env)
        lines = r.stdout.splitlines()
        got = [l.split()[-1] for l in lines if l.startswith("GT: render seconds")]
        rend = [float(l.split()[3]) for l in lines if l.startswith("GT: render seconds")]
        build = next((float(l.split()[3]) for l in lines if l.startswith("GT: build seconds")), None)
        if r.returncode or not got: sys.exit(f"blender {kind} failed: " + "\n".join((r.stdout + r.stderr).splitlines()[-8:]))
        outs += got; times[kind] = round(time.time() - t, 1)
        if build is not None: times["build"] = build
        times["render_views"] = rend
    for p in outs: print(p)
    print(json.dumps({"mode": mode, "target": view if mode == "at" else None, "fast": fast, "far": far, "seconds": times, "total": round(time.time() - t0, 1),
                      "others": [o["name"] for o in meta.get("others", [])], "textures": info["textures"], "bakes": n_bake}))

if __name__ == "__main__":
    main(sys.argv[1:])
