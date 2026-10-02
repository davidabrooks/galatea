#!/usr/bin/env python3
"""Overhead picture for galatay-text `overhead`: SL map tile + live state JSON written by the daemon.
usage: overhead.py STATE.json OUT.png   (prints a one-line summary; nonzero exit on failure)"""
import json, math, os, re, sys, time, urllib.request, heapq
from PIL import Image, ImageDraw, ImageFont, ImageEnhance
st = json.load(open(sys.argv[1])); out = sys.argv[2]
TILES = '/workspace/secondlife/research/tiles'; os.makedirs(TILES, exist_ok=True)
gx, gy = st['grid_x'], st['grid_y']; tf = f"{TILES}/map-1-{gx}-{gy}-objects.jpg"
tile_note = ''
try:
    if not os.path.exists(tf) or time.time() - os.path.getmtime(tf) > 12*3600:
        req = urllib.request.Request(f"https://secondlife-maps-cdn.akamaized.net/map-1-{gx}-{gy}-objects.jpg", headers={'User-Agent': 'galatay-overhead/1'})
        with urllib.request.urlopen(req, timeout=10) as r:
            data = r.read(); lm = r.headers.get('Last-Modified', '')
        open(tf + '.tmp', 'wb').write(data); os.replace(tf + '.tmp', tf); open(tf + '.lm', 'w').write(lm)
    tile = Image.open(tf).convert('RGB'); tile_note = open(tf + '.lm').read().strip() if os.path.exists(tf + '.lm') else ''
except Exception as e:
    tile = Image.new('RGB', (256, 256), (205, 215, 200)); tile_note = f'no map tile ({type(e).__name__})'
X0, X1, Y0, Y1 = st['crop']
S = max(4.0, min(8.0, 920.0 / max(1.0, X1 - X0)))
W, H = int((X1 - X0) * S), int((Y1 - Y0) * S)
def P(x, y): return ((x - X0) * S, (Y1 - y) * S)
def F(n, b=False): return ImageFont.truetype(f"/usr/share/fonts/truetype/dejavu/DejaVuSans{'-Bold' if b else ''}.ttf", n)
base = tile.crop((int(X0), int(256 - Y1), int(math.ceil(X1)), int(math.ceil(256 - Y0)))).resize((W, H), Image.BICUBIC)
base = ImageEnhance.Brightness(ImageEnhance.Color(base).enhance(0.55)).enhance(1.15)
img = base.convert('RGBA'); ov = Image.new('RGBA', img.size, (0, 0, 0, 0)); d = ImageDraw.Draw(ov)
# parcel (8 m samples)
pg = st.get('parcel_grid')
if pg and os.path.exists(pg):
    bc = set()
    for l in open(pg):
        if 'The Buddha Center' in l: p = l.split(); bc.add((int(p[0]), int(p[1])))
    for (x, y) in bc: d.rectangle([P(x - 4, y + 4), P(x + 4, y - 4)], fill=(80, 170, 255, 34))
    for (x, y) in bc:
        for dx, dy, a, b in ((8, 0, (x+4, y-4), (x+4, y+4)), (-8, 0, (x-4, y-4), (x-4, y+4)), (0, 8, (x-4, y+4), (x+4, y+4)), (0, -8, (x-4, y-4), (x+4, y-4))):
            if (x + dx, y + dy) not in bc: d.line([P(*a), P(*b)], fill=(30, 110, 230, 230), width=3)
if st.get('zendo'):
    zx, zy, zr = st['zendo']; dia = [P(zx + zr, zy), P(zx, zy + zr), P(zx - zr, zy), P(zx, zy - zr)]
    d.polygon(dia, fill=(220, 40, 40, 45)); d.line(dia + [dia[0]], fill=(200, 20, 20, 255), width=3)
def orect(o, fill, outline):
    t = math.radians(o['rotz']); c, s = math.cos(t), math.sin(t); hx, hy = o['sx'] / 2, o['sy'] / 2
    d.polygon([P(o['x'] + c*ax - s*ay, o['y'] + s*ax + c*ay) for ax, ay in ((-hx, -hy), (hx, -hy), (hx, hy), (-hx, hy))], fill=fill, outline=outline)
for o in st['pieces']:
    orect(o, (235, 205, 150, 190), (120, 85, 40, 255)) if o['kind'] == 'path' else orect(o, (235, 205, 150, 90), (150, 120, 80, 200))
# path graph
places = {}
if st.get('graph') and os.path.exists(st['graph']):
    g = json.load(open(st['graph'])); N = g['nodes']
    for a, b, k in g['edges']:
        d.line([P(*N[a][:2]), P(*N[b][:2])], fill=(255, 140, 0, 255) if k != 'connector' else (255, 185, 60, 255), width=4 if k == 'strip' else 3)
    places = {k: N[v['node']] for k, v in g['places'].items()}
# seats
for sct in st['seats']:
    if sct['id'] == st.get('home_pillow'): continue
    x, y = P(sct['x'], sct['y']); occ = sct.get('occupied')
    col = (0, 120, 255, 255) if sct['id'] == st.get('david_pillow') else ((200, 60, 60, 255) if occ else (40, 160, 60, 255))
    d.ellipse([x - 3, y - 3, x + 3, y + 3], fill=col if occ else (255, 255, 255, 220), outline=col, width=2)
# route + progress
rt = st.get('route')
if rt:
    pts = [P(p[0], p[1]) for p in rt['points']]
    if len(pts) > 1: d.line(pts, fill=(200, 0, 180, 255), width=3, joint='curve')
    for i in range(len(pts) - 1):
        (x1, y1), (x2, y2) = pts[i], pts[i + 1]
        if math.hypot(x2 - x1, y2 - y1) < 25: continue
        mx, my = (x1 + x2) / 2, (y1 + y2) / 2; a = math.atan2(y2 - y1, x2 - x1)
        d.polygon([(mx + 9*math.cos(a), my + 9*math.sin(a)), (mx + 7*math.cos(a + 2.5), my + 7*math.sin(a + 2.5)), (mx + 7*math.cos(a - 2.5), my + 7*math.sin(a - 2.5))], fill=(200, 0, 180, 255))
    ex, ey = pts[-1]; d.rectangle([ex - 6, ey - 6, ex + 6, ey + 6], outline=(200, 0, 180, 255), width=3)
img = Image.alpha_composite(img, ov); d = ImageDraw.Draw(img)
def label(x, y, t, col=(20, 20, 20), size=12, bold=False):
    f = F(size, bold); bb = d.textbbox((x, y), t, font=f)
    d.rectangle([bb[0] - 3, bb[1] - 2, bb[2] + 3, bb[3] + 2], fill=(255, 255, 255, 215)); d.text((x, y), t, font=f, fill=col)
for k, n in places.items():
    x, y = P(n[0], n[1]); d.ellipse([x - 4, y - 4, x + 4, y + 4], fill=(0, 0, 0)); label(x + 6, y + 4, k, (60, 40, 0), 10)
if st.get('zendo'): label(*P(st['zendo'][0] - 8, st['zendo'][1] + 4), 'ZENDO (no-go)', (180, 0, 0), 11, True)
for av in st['avatars']:
    x, y = P(av['x'], av['y']); sky = av['z'] > 150
    if not (0 <= x <= W and 0 <= y <= H): continue
    col = (0, 90, 220) if av.get('friend_owner') else (60, 60, 60)
    d.ellipse([x - 6, y - 6, x + 6, y + 6], outline=col, width=3, fill=None if sky else (255, 255, 255))
    label(x + 8, y - 7, av['name'].replace(' Resident', '') + (f" (skybox z{av['z']:.0f})" if sky else (' (seated)' if av['seated'] else '')), col, 10)
me = st['me']; gx_, gy_ = P(me['x'], me['y'])
star = [(gx_ + (11 if k % 2 == 0 else 5) * math.cos(-math.pi/2 + k*math.pi/5), gy_ + (11 if k % 2 == 0 else 5) * math.sin(-math.pi/2 + k*math.pi/5)) for k in range(10)]
d.polygon(star, fill=(255, 215, 0), outline=(0, 0, 0))
label(gx_ + 13, gy_ - 8, f"Galatea ({'seated' if me['seated'] else 'standing'}, z {me['z']:.1f})", bold=True)
d.rectangle([0, 0, W, 44], fill=(255, 255, 255, 235))
t = f"{st['region']} - Galatea {st['time']} PT" + (f"  [{st['tag']}]" if st.get('tag') else '')
d.text((8, 4), t, font=F(13, True), fill=(0, 0, 0))
sub = (f"route {rt['name']}: {rt['progress_m']:.0f}/{rt['len_m']:.0f} m" if rt else 'no route running') + f"   |   base: SL map tile {gx},{gy} {('(' + tile_note + ')') if tile_note else ''}"
d.text((8, 24), sub[:150], font=F(11), fill=(40, 40, 40))
L = [((255, 140, 0), 'marked path (Walk This Way strips) / connector'), ((200, 0, 180), 'current route'), ((190, 150, 90), 'path & step pieces'),
     ((40, 160, 60), 'free seat'), ((200, 60, 60), 'occupied seat'), ((0, 120, 255), "David's pillow"), ((200, 20, 20), 'zendo footprint')]
bx0, by0 = 12, H - 16*len(L) - 12
d.rectangle([bx0 - 6, by0 - 6, bx0 + 300, H - 4], fill=(255, 255, 255, 225), outline=(0, 0, 0))
for i, (c, tx) in enumerate(L):
    y = by0 + 16*i; d.line([(bx0, y + 7), (bx0 + 22, y + 7)], fill=c, width=5); d.text((bx0 + 30, y), tx, font=F(10), fill=(0, 0, 0))
sy = 64; d.rectangle([12, sy - 7, 12 + 20*S, sy], fill=(0, 0, 0)); d.rectangle([12 + 10*S, sy - 7, 12 + 20*S, sy], fill=(255, 255, 255), outline=(0, 0, 0))
d.text((12 + 20*S + 6, sy - 12), '20 m', font=F(10), fill=(0, 0, 0))
d.polygon([(W - 22, 50), (W - 29, 68), (W - 15, 68)], fill=(0, 0, 0)); d.text((W - 27, 69), 'N', font=F(12, True), fill=(0, 0, 0))
img.convert('RGB').save(out, optimize=True)
print(f"{W}x{H}, {len(st['avatars'])} avatars, {len(st['seats'])} seats, {len(st['pieces'])} pieces")
