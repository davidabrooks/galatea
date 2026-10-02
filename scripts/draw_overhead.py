#!/usr/bin/env python3
"""Annotated overhead of Naberrie (Buddha Center): SL map tile + object data from the text client.
Read-only inputs: map tile JPG, naberrie-objects.json (parsed `map` dumps), bc-path-graph.json, parcel-grid8.txt.
usage: draw_overhead.py OUT.png"""
import json, math, re, sys, datetime
from PIL import Image, ImageDraw, ImageFont, ImageEnhance
R = '/workspace/secondlife/research/'
X0, X1, Y0, Y1, S = 56, 186, 84, 194, 7          # crop in region metres, px per metre
W, H = (X1-X0)*S, (Y1-Y0)*S
def P(x, y): return ((x-X0)*S, (Y1-y)*S)         # region (x east, y north) -> image px
F = lambda n, b=False: ImageFont.truetype(f"/usr/share/fonts/truetype/dejavu/DejaVuSans{'-Bold' if b else ''}.ttf", n)

tile = Image.open(R+'naberrie-tile-z1.jpg').convert('RGB')          # 256 px = 256 m, north up
base = tile.crop((X0, 256-Y1, X1, 256-Y0)).resize((W, H), Image.BICUBIC)
base = ImageEnhance.Color(base).enhance(0.55); base = ImageEnhance.Brightness(base).enhance(1.15)
img = base.convert('RGBA'); ov = Image.new('RGBA', img.size, (0, 0, 0, 0)); d = ImageDraw.Draw(ov)

# parcel: 8 m samples (each sample = centre of an 8x8 m cell)
bc = set()
for l in open(R+'parcel-grid8.txt'):
    p = l.split(None, 2)
    if "The Buddha Center" in l: bc.add((int(p[0]), int(p[1])))
for (x, y) in bc:
    d.rectangle([P(x-4, y+4), P(x+4, y-4)], fill=(80, 170, 255, 38))
for (x, y) in bc:
    for dx, dy, e in ((8, 0, ((x+4, y-4), (x+4, y+4))), (-8, 0, ((x-4, y-4), (x-4, y+4))), (0, 8, ((x-4, y+4), (x+4, y+4))), (0, -8, ((x-4, y-4), (x+4, y-4)))):
        if (x+dx, y+dy) not in bc: d.line([P(*e[0]), P(*e[1])], fill=(30, 110, 230, 230), width=3)

# zendo diamond (no-go unless asked): |x-80.5|+|y-142| <= 16.55
zc, zr = (80.5, 142.0), 16.55
dia = [P(zc[0]+zr, zc[1]), P(zc[0], zc[1]+zr), P(zc[0]-zr, zc[1]), P(zc[0], zc[1]-zr)]
d.polygon(dia, fill=(220, 40, 40, 45), outline=(200, 20, 20, 255)); d.line(dia+[dia[0]], fill=(200, 20, 20, 255), width=3)

objs = json.load(open(R+'naberrie-objects.json'))
def orect(o, fill, outline):
    t = math.radians(o['rotz']); c, s = math.cos(t), math.sin(t); hx, hy = o['sx']/2, o['sy']/2
    pts = [P(o['x']+c*ax-s*ay, o['y']+s*ax+c*ay) for ax, ay in ((-hx, -hy), (hx, -hy), (hx, hy), (-hx, hy))]
    d.polygon(pts, fill=fill, outline=outline)
# Skye mesh path / step pieces (solid, walkable)
for o in objs:
    n = o['name'].lower()
    if o['z'] < 100 and 'skye' in n and 'path' in n:
        orect(o, (235, 205, 150, 190), (120, 85, 40, 255))
    elif o['z'] < 100 and 'skye' in n and ('step' in n or 'stair' in n):
        orect(o, (235, 205, 150, 90), (150, 120, 80, 200))
# guide strips graph
g = json.load(open(R+'bc-path-graph.json')); N = g['nodes']
for e in g['edges']:
    a, b = N[e['a']], N[e['b']]
    d.line([P(a['x'], a['y']), P(b['x'], b['y'])], fill=(255, 140, 0, 255), width=4)

# seats
seatw = ('pillow', 'cushion', 'zafu', 'zabuton', 'meditation seat', 'bench', 'chair', 'mat ')
HER = '10d8a656-56bf-a9c6-0ff1-59ef1a6a47fd'; DAVID = 'd942bdeb-368d-29f5-6df6-43ab5f72b496'
for o in objs:
    if o['z'] < 100 and any(w in o['name'].lower() for w in seatw) and X0 < o['x'] < X1 and Y0 < o['y'] < Y1 and o['id'] not in (HER,):
        x, y = P(o['x'], o['y']); r = 3
        col = (40, 160, 60, 255) if o['id'] != DAVID else (0, 120, 255, 255)
        d.ellipse([x-r, y-r, x+r, y+r], fill=(255, 255, 255, 220), outline=col, width=2)

# planned route (sample): pillow -> off the pool rock (NavWalk-tested points) -> guide path -> Deer Park
route = [(133.2, 139.3), (132.5, 141.5), (136.0, 144.0), (137.9, 147.6)]
import heapq
adj = {n['i']: [] for n in N}
for e in g['edges']: adj[e['a']].append(e['b']); adj[e['b']].append(e['a'])
near = lambda x, y: min(adj, key=lambda i: math.hypot(N[i]['x']-x, N[i]['y']-y))
s0, g0 = near(137.9, 147.6), near(145.7, 93.7)
dist, prev, pq = {s0: 0}, {}, [(0, s0)]
while pq:
    d0, u = heapq.heappop(pq)
    for v in adj[u]:
        nd = d0 + math.hypot(N[u]['x']-N[v]['x'], N[u]['y']-N[v]['y'])
        if nd < dist.get(v, 1e9): dist[v] = nd; prev[v] = u; heapq.heappush(pq, (nd, v))
seq = [g0]
while seq[-1] != s0: seq.append(prev[seq[-1]])
route += [(N[i]['x'], N[i]['y']) for i in reversed(seq)]
pts = [P(*p) for p in route]
d.line(pts, fill=(200, 0, 180, 255), width=3, joint='curve')
for i in range(len(pts)-1):
    (x1, y1), (x2, y2) = pts[i], pts[i+1]
    if math.hypot(x2-x1, y2-y1) < 25: continue
    mx, my = (x1+x2)/2, (y1+y2)/2; a = math.atan2(y2-y1, x2-x1)
    d.polygon([(mx+9*math.cos(a), my+9*math.sin(a)), (mx+7*math.cos(a+2.5), my+7*math.sin(a+2.5)), (mx+7*math.cos(a-2.5), my+7*math.sin(a-2.5))], fill=(200, 0, 180, 255))
ex, ey = pts[-1]; d.rectangle([ex-6, ey-6, ex+6, ey+6], outline=(200, 0, 180, 255), width=3)

img = Image.alpha_composite(img, ov); d = ImageDraw.Draw(img)
def label(x, y, t, col=(20, 20, 20), size=13, bold=False):
    f = F(size, bold); bb = d.textbbox((x, y), t, font=f)
    d.rectangle([bb[0]-3, bb[1]-2, bb[2]+3, bb[3]+2], fill=(255, 255, 255, 215)); d.text((x, y), t, font=f, fill=col)
# Galatea
gx, gy = P(133.2, 139.3); star = []
for k in range(10):
    rr = 11 if k % 2 == 0 else 5; a = -math.pi/2 + k*math.pi/5; star.append((gx+rr*math.cos(a), gy+rr*math.sin(a)))
d.polygon(star, fill=(255, 215, 0), outline=(0, 0, 0))
label(gx+14, gy-8, 'Galatea (seated, pillow 10d8a656)', bold=True)
dx_, dy_ = P(133.1, 138.2); label(dx_+10, dy_+8, "David's pillow (empty)", (0, 90, 200), 11)
# other avatars
bx, by = P(105.4, 151.2); d.ellipse([bx-6, by-6, bx+6, by+6], outline=(0, 0, 0), width=2)
label(bx-215, by-10, 'BodhiCheetah (skybox, z 253)', (60, 60, 60), 11)
label(*P(72, 147), 'ZENDO', (180, 0, 0), 13, True); label(*P(70.5, 143.5), 'no-go unless asked', (180, 0, 0), 10)
lx, ly = P(107.0, 150.8); d.ellipse([lx-5, ly-5, lx+5, ly+5], fill=(0, 0, 0)); label(lx+8, ly-24, 'BC landing point', size=11)
label(ex+10, ey-6, 'Deer Park end', (140, 0, 120), 12, True)
label(*P(60, 124), 'Parcel "The Buddha Center" (blue, 8 m sampling)', (20, 80, 200), 11)
# title / legend
now = datetime.datetime.now().strftime('%Y-%m-%d %H:%M PT')
d.rectangle([0, 0, W, 30], fill=(255, 255, 255, 235))
d.text((8, 6), f'Naberrie / Buddha Center - Galatea {now}   (base: SL map tile 1136,1088 from 2026-09-25 06:16 PT)', font=F(13, True), fill=(0, 0, 0))
L = [((255, 140, 0), 'marked walking path ("Walk This Way" guide strips)'), ((200, 0, 180), 'planned route (sample): pillow -> Deer Park'),
     ((190, 150, 90), 'Skye mesh path/step pieces'), ((40, 160, 60), 'free seats (pillows, cushions, lily pads)'), ((200, 20, 20), 'zendo footprint')]
bx0, by0 = 16, H-18*len(L)-14
d.rectangle([bx0-8, by0-8, bx0+345, H-6], fill=(255, 255, 255, 225), outline=(0, 0, 0))
for i, (c, t) in enumerate(L):
    y = by0 + 18*i; d.line([(bx0, y+7), (bx0+24, y+7)], fill=c, width=5); d.text((bx0+32, y), t, font=F(11), fill=(0, 0, 0))
# scale bar + north
sy = 62; d.rectangle([12, sy-8, 12+20*S, sy], fill=(0, 0, 0)); d.rectangle([12+10*S, sy-8, 12+20*S, sy], fill=(255, 255, 255), outline=(0, 0, 0))
d.text((12, sy-26), '0         10        20 m', font=F(11), fill=(0, 0, 0))
d.polygon([(W-30, 44), (W-38, 66), (W-22, 66)], fill=(0, 0, 0)); d.text((W-35, 68), 'N', font=F(13, True), fill=(0, 0, 0))
img.convert('RGB').save(sys.argv[1] if len(sys.argv) > 1 else '/workspace/secondlife/images/overhead-sample.png', optimize=True)
print('saved', W, H)
