#!/usr/bin/env python3
"""Gather an approximate scene around Galatea from the live text client (read-only commands only).

Uses `where`, `map <r>`, `terrain <x> <y>` and `faces <uuid>` through text-galatay.sh. Writes scene JSON:
objects as oriented boxes (position, size, yaw) with one representative texture UUID, a terrain height grid,
and Galatea's position. Textures are referenced by UUID; the render worker fetches them itself from SL's
public asset CDN. No chat, no movement, no state changes.
usage: gather_scene.py [--radius 64] [--grid-step 5] [--grid-half 40] [--textured 60] out.json
"""
import argparse, json, re, subprocess, sys, time

CMD = "/home/box/viewers/text-galatay.sh"

def cmd(c, timeout=60):
    return subprocess.run([CMD, "cmd", c], capture_output=True, text=True, timeout=timeout).stdout

OBJ = re.compile(r"\s+<([-\d.]+),([-\d.]+),([-\d.]+)> size ([\d.]+)x([\d.]+)x([\d.]+) rotZ (-?\d+) (PHANTOM )?'(.*)' (\S+)\s*$")
FACE = re.compile(r"^\s+([0-9a-f-]{36})\s+\[([^\]]*)\]\s+(.*)$")
POS = re.compile(r"pos=<([-\d.]+), ([-\d.]+), ([-\d.]+)>")
BAKED = {"5a9f4a74-30f2-821c-b88d-70499d3e7183", "ae2de45c-d252-50b8-5c6e-19f39ce79317", "24daea5f-0539-cfcf-047f-fbc40b2786ba",
         "52cc6bb6-2ee5-e632-d3ad-50197b1dcb8a", "43529ce8-7faa-ad92-165a-bc4078371687", "9742065b-19b5-297c-858a-29711d539043",
         "03642e83-2bd1-4eb9-34b4-4c47ed586d2d", "edd51b77-fc10-ce7a-4b3d-011dfc349e4f"}

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out"); ap.add_argument("--radius", type=int, default=64)
    ap.add_argument("--grid-step", type=float, default=5); ap.add_argument("--grid-half", type=float, default=40)
    ap.add_argument("--textured", type=int, default=60); ap.add_argument("--zmax", type=float, default=90)
    a = ap.parse_args()
    t0 = time.time()
    w = cmd("where"); m = POS.search(w)
    if not m: sys.exit("no position from 'where': " + w[:200])
    me = [float(m.group(i)) for i in (1, 2, 3)]
    region = (re.search(r"region=(\S+)", w) or [None, "?"])[1]
    objs = []
    for line in cmd(f"map {a.radius}").splitlines():
        o = OBJ.match(line)
        if not o: continue
        p = [float(o.group(i)) for i in (1, 2, 3)]
        if p[2] > a.zmax: continue  # skip sky platforms far above the ground scene
        objs.append({"pos": p, "size": [float(o.group(i)) for i in (4, 5, 6)], "yaw_deg": int(o.group(7)),
                     "phantom": bool(o.group(8)), "name": o.group(9), "uuid": o.group(10), "texture": None})
    # representative texture for the biggest objects and everything close by
    def vol(o): s = o["size"]; return s[0] * s[1] * max(s[2], 0.2)
    def dist(o): return sum((o["pos"][i] - me[i]) ** 2 for i in range(3)) ** 0.5
    pick = sorted(objs, key=vol, reverse=True)[:a.textured] + [o for o in objs if dist(o) < 12]
    seen = set()
    for o in pick:
        if o["uuid"] in seen: continue
        seen.add(o["uuid"])
        best, best_n = None, -1
        for line in cmd(f"faces {o['uuid']} r={a.radius + 10}").splitlines():
            f = FACE.match(line)
            if not f or f.group(1) in BAKED: continue
            n = len(f.group(2).split(","))
            vis = "skipped" not in f.group(3)
            score = n + (100 if vis else 0)
            if score > best_n: best, best_n = f.group(1), score
        o["texture"] = best
    # terrain grid
    xs = [me[0] - a.grid_half + i * a.grid_step for i in range(int(2 * a.grid_half / a.grid_step) + 1)]
    ys = [me[1] - a.grid_half + j * a.grid_step for j in range(int(2 * a.grid_half / a.grid_step) + 1)]
    grid = []
    for y in ys:
        row = []
        for x in xs:
            xx, yy = min(max(x, 0), 255.9), min(max(y, 0), 255.9)
            r = re.search(r"ground at [\d.]+,[\d.]+: ([-\d.]+)", cmd(f"terrain {xx:.1f} {yy:.1f}"))
            row.append(float(r.group(1)) if r else None)
        grid.append(row)
    scene = {"region": region, "galatea": {"pos": me}, "objects": objs,
             "terrain": {"x0": xs[0], "y0": ys[0], "step": a.grid_step, "nx": len(xs), "ny": len(ys), "heights": grid},
             "gathered_at": time.strftime("%Y-%m-%dT%H:%M:%S%z"), "gather_seconds": round(time.time() - t0, 1)}
    json.dump(scene, open(a.out, "w"), indent=1)
    print(f"{len(objs)} objects ({sum(1 for o in objs if o['texture'])} textured), terrain {len(xs)}x{len(ys)}, "
          f"{scene['gather_seconds']} s -> {a.out}")

if __name__ == "__main__":
    main()
