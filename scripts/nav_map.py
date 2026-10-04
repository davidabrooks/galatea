#!/usr/bin/env python3
"""nav_map.py (2026-10-04, David: walk around the Peronaut patio and through its doors without flying)
Walkable-grid builder for the text client's `nav` planner (galatay-text/NavPlan.cs), from a `scene export`.

  build  <export dir> <out.json> [--center x,y] [--size 40] [--cell 0.1] [--floor z] [--radius 0.4] [--name home]
         [--door-names <uuid prefix>=<name>,...] [--places name=x,y;name=x,y]
         meshes the export with `scene-mesher --nav` (per-prim triangles, invisible faces kept: they still collide),
         slices every solid (non-phantom) prim at avatar height (floor+0.35 .. floor+1.9 m), marks cells with no floor
         under them (deck edge) as blocked, finds door panels (thin, floor-to-~3 m link prims of a building) and keeps
         them as door cells ('D', passable after the door is opened), and writes the grid (rows south->north, '.' free,
         '#' blocked, 'D' door) + doors + named places. ALSO draws <out>.png (top-down map).
  draw   <grid.json> <out.png> [--route x,y;x,y;...] [--track client.log since-HH:MM[:SS] [until]] [--title text] [--plan x,y x,y ...]
         top-down map: obstacles, doors, places, planned legs (A*, same rules as NavPlan.cs) and/or the positions her walk logged.
  plan   <grid.json> x,y x,y [x,y ...]   print the planned waypoints for each leg (A* + line-of-sight smoothing)
  selftest   plan on a tiny synthetic grid (a wall with a door) - no export needed
Python: /home/box/tools/imgvenv/bin/python (numpy + pillow). Read-only toward SL (the mesher only GETs CDN assets).
"""
import base64, heapq, json, math, os, re, shutil, subprocess, sys, tempfile
import numpy as np

MESHER = os.environ.get("SCENE_MESHER", "/home/box/tools/scene-mesher/SceneMesher.dll")
DOTNET = shutil.which("dotnet") or os.path.join(os.environ.get("DOTNET_ROOT", "/home/box/.dotnet"), "dotnet")
BAND = (0.35, 1.9)      # obstacle slice above the floor: over a step/door sill, up to her head (1.58 m) + margin
STEP = 0.45             # floor surfaces within this of the floor level carry her (deck boards, sills, rugs)
HULL_MAX = 4.0          # prims up to this size (m) block their whole 2-D convex hull (SL mesh default physics = hull)


def lid(v): return v if isinstance(v, int) else int.from_bytes(bytes(v), "big")   # export localid: 4 bytes, big-endian


def opt(a, k, d):
    return a[a.index(k) + 1] if k in a else d


def load_tris(d):
    meta = json.load(open(f"{d}/mesh.json")); raw = open(f"{d}/mesh.bin", "rb").read(); out = {}
    for b in meta["batches"]:
        if not b["group"].startswith("nav:"): continue
        nv, ni, off = b["nv"], b["ni"], b["offset"]
        P = np.frombuffer(raw, np.float32, nv * 3, off).reshape(-1, 3).astype(np.float64)
        I = np.frombuffer(raw, np.uint32, ni, off + nv * 8 * 4).reshape(-1, 3)   # after pos, normal (3) and uv (2) floats
        out.setdefault(int(b["group"][4:]), []).append(P[I])
    return {k: np.concatenate(v) for k, v in out.items()}


def hull2d(P):
    """convex hull (monotone chain) of an (n, 2) array -> [(x, y), ...]"""
    pts = sorted(set(map(tuple, np.round(P, 3))))
    if len(pts) < 3: return pts
    def half(seq):
        h = []
        for p in seq:
            while len(h) >= 2 and (h[-1][0] - h[-2][0]) * (p[1] - h[-2][1]) - (h[-1][1] - h[-2][1]) * (p[0] - h[-2][0]) <= 0: h.pop()
            h.append(p)
        return h[:-1]
    return half(pts) + half(reversed(pts))


def clip(poly, z, above):
    out = []
    for i in range(len(poly)):
        a, b = poly[i], poly[(i + 1) % len(poly)]
        ia, ib = (a[2] >= z, b[2] >= z) if above else (a[2] <= z, b[2] <= z)
        if ia: out.append(a)
        if ia != ib: out.append(a + (b - a) * ((z - a[2]) / (b[2] - a[2])))
    return out


class Grid:
    def __init__(s, x0, y0, cell, nx, ny):
        s.x0, s.y0, s.cell, s.nx, s.ny = x0, y0, cell, nx, ny
    def ij(s, x, y): return int((x - s.x0) / s.cell), int((y - s.y0) / s.cell)
    def xy(s, i, j): return s.x0 + (i + 0.5) * s.cell, s.y0 + (j + 0.5) * s.cell
    def img(s):  # PIL image space: x right, row 0 = north edge
        from PIL import Image
        return Image.new("L", (s.nx, s.ny), 0)
    def pix(s, x, y): return ((x - s.x0) / s.cell, s.ny - (y - s.y0) / s.cell)


def raster(g, polys, width=1):
    """polys: lists of (x,y) -> bool mask [j][i] (j = south->north)"""
    from PIL import ImageDraw
    im = g.img(); dr = ImageDraw.Draw(im)
    for pts in polys:
        q = [g.pix(x, y) for x, y in pts]
        if len(q) >= 3: dr.polygon(q, fill=255, outline=255)
        if len(q) >= 2: dr.line(q + [q[0]], fill=255, width=width)   # vertical faces project to segments: draw them
    return np.asarray(im)[::-1] > 0


def dilate(m, r):
    out = m.copy(); n = int(math.ceil(r))
    for di in range(-n, n + 1):
        for dj in range(-n, n + 1):
            if di * di + dj * dj > r * r or (di == 0 and dj == 0): continue
            sh = np.zeros_like(m)
            sh[max(0, dj):m.shape[0] + min(0, dj), max(0, di):m.shape[1] + min(0, di)] = m[max(0, -dj):m.shape[0] - max(0, dj), max(0, -di):m.shape[1] - max(0, di)]
            out |= sh
    return out


def build(a):
    exp, out = a[0], a[1]
    doc = json.load(open(f"{exp}/scene.json")); me = doc["me"]["pos"]
    cx, cy = (float(v) for v in opt(a, "--center", f"{me[0]},{me[1]}").split(","))
    size, cell, radius = float(opt(a, "--size", "40")), float(opt(a, "--cell", "0.1")), float(opt(a, "--radius", "0.4"))
    work = tempfile.mkdtemp(prefix="navmesh-"); shutil.copy(f"{exp}/scene.json", work)   # never overwrite look's mesh files
    r = subprocess.run([DOTNET, MESHER, work, "40", "scene", f"{cx},{cy},{me[2]}", str(size * 0.75), "--nav"], capture_output=True, text=True)
    if r.returncode: sys.exit("scene-mesher --nav failed: " + (r.stderr or r.stdout)[-400:])
    tris = load_tris(work); shutil.rmtree(work, ignore_errors=True)
    prims = {lid(p["localid"]): p for p in doc["prims"] if "world_pos" in p}
    g = Grid(cx - size / 2, cy - size / 2, cell, int(size / cell), int(size / cell))
    # floor: given, or the level (0.05 m bins) with the most upward-facing area within 4 m of her, 0.3-1.5 m below her
    # agent position (the biggest surface, not a seat she may be sitting on)
    floor = opt(a, "--floor", None)
    if floor is None:
        area = {}
        for T in tris.values():
            c = T.mean(1); n = np.cross(T[:, 1] - T[:, 0], T[:, 2] - T[:, 0])
            m = (np.hypot(c[:, 0] - me[0], c[:, 1] - me[1]) < 4) & (c[:, 2] > me[2] - 1.5) & (c[:, 2] < me[2] - 0.3) & (n[:, 2] > 0.9 * np.linalg.norm(n, axis=1))
            for z, ar in zip(c[m, 2], n[m, 2] / 2): area[round(z / 0.05)] = area.get(round(z / 0.05), 0) + ar
        if not area: sys.exit("no floor under her in the export: pass --floor z")
        floor = max(area, key=area.get) * 0.05
    floor = float(floor); zlo, zhi = floor + BAND[0], floor + BAND[1]
    # doors: thin floor-standing link prims ~2-3.3 m tall and 0.7-3.2 m wide of a linkset that also has walls
    doors = []
    for k, T in tris.items():
        p = prims.get(k)
        if p is None or lid(p["parentid"]) == 0 or p.get("phantom"): continue
        mn, mx = T.reshape(-1, 3).min(0), T.reshape(-1, 3).max(0); ext = mx - mn
        thin, wide = sorted(ext[:2])
        if abs(mn[2] - floor) < 0.4 and 1.9 <= ext[2] <= 3.4 and thin < 0.5 and 0.7 <= wide <= 3.2:
            doors.append({"localid": k, "uuid": p["id"], "center": [round(float(v), 2) for v in (mn + mx) / 2],
                          "min": [round(float(v), 2) for v in mn], "max": [round(float(v), 2) for v in mx],
                          "axis": "x" if ext[0] > ext[1] else "y", "pos": [round(float(v), 3) for v in p["world_pos"]],
                          "local_rot": [float(v or 0) for v in p["rotation"]]})   # closed pose = as exported (export nulls are 0)
    door_ids = {d["localid"] for d in doors}
    solid, door_polys, support = [], [], []
    for k, T in tris.items():
        p = prims.get(k)
        if p is None: continue
        # phantom things she could walk through but shouldn't (David: avoid the plant and the dance machine): small phantom
        # prims (<= 3 m across) count as solid; big phantom ones (trees, sound/particle volumes) don't
        if p.get("phantom") and max(np.ptp(T[:, :, 0]), np.ptp(T[:, :, 1])) > 3: continue
        z0, z1 = T[:, :, 2].min(1), T[:, :, 2].max(1)
        inx = (T[:, :, 0].max(1) >= g.x0) & (T[:, :, 0].min(1) <= g.x0 + size) & (T[:, :, 1].max(1) >= g.y0) & (T[:, :, 1].min(1) <= g.y0 + size)
        nz = np.cross(T[:, 1] - T[:, 0], T[:, 2] - T[:, 0]); nn = np.linalg.norm(nz, axis=1) + 1e-12; up = nz[:, 2] / nn
        hit = T[inx & (z1 >= zlo) & (z0 <= zhi)]
        ext = sorted((np.ptp(T[:, :, 0]), np.ptp(T[:, :, 1])))
        if len(hit) and k not in door_ids and ext[1] <= HULL_MAX and ext[0] > 0.5:   # not thin walls/frames (their openings matter)
            # furniture / plants: SL's default physics for a mesh is its convex hull, which reaches past the visual slice
            # (2026-10-04 test: she stepped up onto the hanging net chair's hull beside it), so block the 2-D hull of the prim
            solid.append(hull2d(T.reshape(-1, 3)[:, :2]))
            continue
        for t in hit:
            q = clip(list(t), zlo, True); q = clip(q, zhi, False) if len(q) >= 2 else q
            if len(q) >= 2: (door_polys if k in door_ids else solid).append([(v[0], v[1]) for v in q])
        for t in T[inx & (up > 0.7) & (z1 >= floor - STEP) & (z1 <= floor + STEP)]:
            support.append([(v[0], v[1]) for v in t])
    blocked = raster(g, solid)
    floor_ok = raster(g, support)
    ter = doc.get("terrain")
    if ter and ter.get("heights"):   # region heightmap (step m); terrain at floor level carries her too
        H = np.array(ter["heights"]); st = ter["step"]; x0t = ter.get("x0", 0); y0t = ter.get("y0", 0)
        J, I = np.mgrid[0:g.ny, 0:g.nx]; X = g.x0 + (I + 0.5) * cell; Y = g.y0 + (J + 0.5) * cell
        hi = np.clip(((X - x0t) / st).astype(int), 0, H.shape[1] - 1); hj = np.clip(((Y - y0t) / st).astype(int), 0, H.shape[0] - 1)
        floor_ok |= np.abs(H[hj, hi] - floor) <= STEP
    door = raster(g, door_polys, width=2) & ~blocked
    hard = blocked | ~floor_ok          # walls, furniture, and edges with nothing to stand on
    inflated = dilate(hard, radius / cell)
    rows = []
    for j in range(g.ny):
        rows.append("".join("#" if inflated[j, i] else "D" if door[j, i] else "." for i in range(g.nx)))
    # named places: her position, seats near the floor, door approach points (1 m each side of the panel)
    places = {"export_pos": [round(me[0], 2), round(me[1], 2)]}
    for k, p in prims.items():
        if lid(p["parentid"]) == 0 and re.search(r"chair|seat|sofa|bench", p["name"], re.I) and abs(p["world_pos"][2] - floor) < 1.5 \
                and abs(p["world_pos"][0] - cx) < size / 2 and abs(p["world_pos"][1] - cy) < size / 2:
            places[f"{p['name']} {p['id'][:8]}"] = [round(p["world_pos"][0], 2), round(p["world_pos"][1], 2)]
    names = dict(v.split("=", 1) for v in a[a.index("--door-names") + 1].split(",")) if "--door-names" in a else {}
    for n, d in enumerate(sorted(doors, key=lambda d: (d["center"][1], d["center"][0]))):
        d["name"] = next((v for k, v in names.items() if d["uuid"].startswith(k)), f"door{n + 1}")
    for kv in (a[a.index("--places") + 1].split(";") if "--places" in a else []):   # name=x,y;name=x,y
        k, v = kv.split("="); places[k.strip()] = [float(c) for c in v.split(",")]
    doc_out = {"name": opt(a, "--name", "nav"), "region": doc.get("region"), "built_from": os.path.abspath(exp), "floor_z": round(floor, 3),
               "band": [round(zlo, 2), round(zhi, 2)], "radius": radius, "x0": round(g.x0, 3), "y0": round(g.y0, 3), "cell": cell,
               "nx": g.nx, "ny": g.ny, "doors": doors, "places": places, "rows": rows,
               "note": "rows[0] is the south edge; '.' free, '#' blocked (inflated by radius), 'D' door panel (open it first)"}
    json.dump(doc_out, open(out, "w"), indent=0)
    print(f"{out}: {g.nx}x{g.ny} @ {cell} m, floor {floor:.2f}, band {zlo:.2f}-{zhi:.2f}, {len(doors)} door(s): "
          + ", ".join(f"{d['name']} {d['uuid'][:8]} at {d['center'][0]},{d['center'][1]} ({d['axis']})" for d in doors))
    draw([out, os.path.splitext(out)[0] + ".png"])


def load(path):
    d = json.load(open(path)); d["g"] = Grid(d["x0"], d["y0"], d["cell"], d["nx"], d["ny"]); return d


# ---- planner: identical rules to NavPlan.cs (8-connected A*, door cells cost x3, then line-of-sight smoothing) ----
def free(d, i, j): return 0 <= i < d["nx"] and 0 <= j < d["ny"] and d["rows"][j][i] != "#"


def nearest_free(d, i, j, rmax=15):
    if free(d, i, j): return i, j
    for r in range(1, rmax + 1):
        best = None
        for di in range(-r, r + 1):
            for dj in range(-r, r + 1):
                if max(abs(di), abs(dj)) == r and free(d, i + di, j + dj):
                    c = di * di + dj * dj
                    if best is None or c < best[0]: best = (c, i + di, j + dj)
        if best: return best[1], best[2]
    return None


def los(d, a, b):
    (i0, j0), (i1, j1) = a, b; n = max(abs(i1 - i0), abs(j1 - j0)) * 2 + 1
    for k in range(n + 1):
        i = round(i0 + (i1 - i0) * k / n); j = round(j0 + (j1 - j0) * k / n)
        if not free(d, i, j): return False
        if d["rows"][j][i] == "D" and not (d["rows"][j0][i0] == "D" or d["rows"][j1][i1] == "D"): pass
    return True


def astar(d, s, t):
    s = nearest_free(d, *s); t = nearest_free(d, *t)
    if not s or not t: return None
    openq = [(0, 0, s)]; came = {s: None}; cost = {s: 0}
    while openq:
        _, c, cur = heapq.heappop(openq)
        if cur == t: break
        if c > cost[cur]: continue
        for di, dj in ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)):
            n = (cur[0] + di, cur[1] + dj)
            if not free(d, *n) or (di and dj and not (free(d, cur[0] + di, cur[1]) and free(d, cur[0], cur[1] + dj))): continue
            nc = c + (1.4142 if di and dj else 1.0) * (3.0 if d["rows"][n[1]][n[0]] == "D" else 1.0)
            if nc < cost.get(n, 1e18):
                cost[n] = nc; came[n] = cur
                heapq.heappush(openq, (nc + math.hypot(t[0] - n[0], t[1] - n[1]), nc, n))
    if t not in came: return None
    path = [t]
    while came[path[-1]] is not None: path.append(came[path[-1]])
    path.reverse()
    # smoothing: keep the farthest visible cell, and always keep the cells just before/after a door so she crosses it square
    out = [path[0]]; k = 0
    while k < len(path) - 1:
        m = len(path) - 1
        while m > k + 1 and not (los(d, path[k], path[m]) and not any(d["rows"][q[1]][q[0]] == "D" for q in path[k + 1:m])): m -= 1
        out.append(path[m]); k = m
    return out


def plan(d, pts):
    g = d["g"]; legs = []
    for a, b in zip(pts, pts[1:]):
        p = astar(d, g.ij(*a), g.ij(*b))
        legs.append(None if p is None else [tuple(round(v, 2) for v in g.xy(*c)) for c in p])
    return legs


def draw(a):
    from PIL import Image, ImageDraw, ImageFont
    d = load(a[0]); g = d["g"]; S = max(1, int(round(0.05 / g.cell * 0 + 20 * g.cell)))  # px per cell
    S = 4 if g.cell <= 0.1 else 8
    F = lambda n: ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", n)
    im = Image.new("RGB", (g.nx * S, g.ny * S + 40), "white"); dr = ImageDraw.Draw(im)
    P = lambda x, y: ((x - g.x0) / g.cell * S, (g.ny - (y - g.y0) / g.cell) * S)
    col = {"#": (70, 70, 70), "D": (60, 140, 255), ".": (250, 250, 245)}
    arr = np.zeros((g.ny, g.nx, 3), np.uint8)
    for j, row in enumerate(d["rows"]):
        for i, ch in enumerate(row): arr[g.ny - 1 - j, i] = col[ch]
    im.paste(Image.fromarray(arr).resize((g.nx * S, g.ny * S), Image.NEAREST), (0, 0))
    for m in range(int(math.ceil(g.x0)), int(g.x0 + g.nx * g.cell) + 1):
        if m % 2 == 0: dr.line([P(m, g.y0), P(m, g.y0 + g.ny * g.cell)], fill=(200, 200, 200) if m % 10 else (150, 150, 150))
        if m % 4 == 0: dr.text((P(m, 0)[0] + 2, g.ny * S + 2), str(m), fill="red", font=F(11))
    for m in range(int(math.ceil(g.y0)), int(g.y0 + g.ny * g.cell) + 1):
        if m % 2 == 0: dr.line([P(g.x0, m), P(g.x0 + g.nx * g.cell, m)], fill=(200, 200, 200) if m % 10 else (150, 150, 150))
        if m % 4 == 0: dr.text((2, P(0, m)[1] + 1), str(m), fill="red", font=F(11))
    for dd in d["doors"]:
        x, y = dd["center"][:2]; dr.text(P(x + 0.3, y + 0.6), dd["name"], fill=(0, 60, 200), font=F(14))
    for n, (x, y) in d["places"].items():
        q = P(x, y); dr.ellipse([q[0] - 5, q[1] - 5, q[0] + 5, q[1] + 5], outline=(200, 120, 0), width=2)
        dr.text((q[0] + 6, q[1] - 6), n.split(" - ")[-1][:22], fill=(160, 90, 0), font=F(11))
    legend = ["dark = blocked (walls/furniture/edge, +radius)", "blue = door panel"]
    if "--plan" in a or "--route" in a:
        pts = [tuple(float(v) for v in s.split(",")) for s in (a[a.index("--plan") + 1:] if "--plan" in a else opt(a, "--route", "").split(";")) if "," in s and not s.startswith("--")]
        for n, leg in enumerate(plan(d, pts)):
            if leg: dr.line([P(*p) for p in leg], fill=(0, 170, 60), width=3); [dr.ellipse([P(*p)[0] - 3, P(*p)[1] - 3, P(*p)[0] + 3, P(*p)[1] + 3], fill=(0, 120, 40)) for p in leg]
            else: dr.text(P(*pts[n]), "NO PATH", fill="red", font=F(14))
        legend.append("green = planned legs (A*)")
    if "--track" in a:
        log, since = a[a.index("--track") + 1], a[a.index("--track") + 2]
        until = a[a.index("--track") + 3] if len(a) > a.index("--track") + 3 and not a[a.index("--track") + 3].startswith("--") else "99"
        tr = []
        for l in open(log, errors="replace"):
            m = re.match(r"\S+ (\d\d:\d\d:\d\d) \[(walk|nav)\] .*?(arrived at|STUCK at|through \S+: at|leg [^<]*:) <([\d.]+),([\d.]+),([\d.]+)>", l)
            if m and since <= m.group(1) <= until:
                tr.append((float(m.group(4)), float(m.group(5)), "STUCK" in l))
            for m2 in re.finditer(r"\[navpos\] <([\d.]+),([\d.]+),([\d.]+)>", l) if since <= l[11:19] <= until else []:
                tr.append((float(m2.group(1)), float(m2.group(2)), False))
        if len(tr) > 1: dr.line([P(x, y) for x, y, _ in tr], fill=(220, 30, 30), width=2)
        for x, y, st in tr:
            if st: q = P(x, y); dr.rectangle([q[0] - 5, q[1] - 5, q[0] + 5, q[1] + 5], outline="red", width=2)
        legend.append(f"red = walked track ({len(tr)} logged positions, red squares = stuck)")
    if "--title" in a: dr.text((8, 6), opt(a, "--title", ""), fill=(0, 0, 0), font=F(15))
    dr.text((g.nx * S // 3, g.ny * S + 18), " | ".join(legend), fill="black", font=F(12))
    im.save(a[1]); print(a[1])


def selftest():
    # 4 m square, a wall across y=2 with a 0.8 m door at x 1.6..2.4; start south, goal north: the path must go through it
    n = 40; rows = []
    for j in range(n):
        r = ""
        for i in range(n):
            r += ("D" if 16 <= i < 24 else "#") if 19 <= j <= 20 else "."
        rows.append(r)
    d = {"x0": 0, "y0": 0, "cell": 0.1, "nx": n, "ny": n, "rows": rows}; d["g"] = Grid(0, 0, 0.1, n, n)
    leg = plan(d, [(0.5, 0.5), (0.5, 3.5)])[0]
    ok = leg is not None and any(1.5 <= x <= 2.5 and 1.8 <= y <= 2.2 for x, y in leg) and all(free(d, *d["g"].ij(x, y)) for x, y in leg)
    rows2 = [r.replace("D", "#") for r in rows]; d2 = dict(d, rows=rows2)
    ok2 = plan(d2, [(0.5, 0.5), (0.5, 3.5)])[0] is None
    print("selftest", "ok" if ok and ok2 else f"FAILED leg={leg} closed-wall-path-found={not ok2}"); return 0 if ok and ok2 else 1


if __name__ == "__main__":
    a = sys.argv[1:]
    if not a: sys.exit(__doc__)
    if a[0] == "selftest": sys.exit(selftest())
    if a[0] == "build" and len(a) >= 3: build(a[1:])
    elif a[0] == "draw" and len(a) >= 3: draw(a[1:])
    elif a[0] == "plan" and len(a) >= 4:
        d = load(a[1]); pts = [tuple(float(v) for v in s.split(",")) for s in a[2:]]
        for n, leg in enumerate(plan(d, pts)): print(f"leg {n + 1}: {pts[n]} -> {pts[n + 1]}:", "NO PATH" if leg is None else ";".join(f"{x},{y}" for x, y in leg))
    else: sys.exit(__doc__)
