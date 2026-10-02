#!/usr/bin/env python3
"""Path graph from the phantom 'Buddha Center Walk This Way' guide strips (Naberrie).
Each strip = one edge; its long axis is local +Y rotated by yaw (rotZ); endpoints within MERGE m are merged.
usage: bc_path_graph.py [objects.json] [--route x1,y1 x2,y2]"""
import json, math, sys, heapq
MERGE = 2.0
args = sys.argv[1:]
src = args[0] if args and not args[0].startswith('--') else '/workspace/secondlife/research/naberrie-objects.json'
objs = json.load(open(src))
nodes = []  # [x, y, z, count]
def node(x, y, z):
    for i, n in enumerate(nodes):
        if math.hypot(n[0]-x, n[1]-y) <= MERGE:
            c = n[3]; n[0] = (n[0]*c + x)/(c+1); n[1] = (n[1]*c + y)/(c+1); n[2] = (n[2]*c + z)/(c+1); n[3] = c+1
            return i
    nodes.append([x, y, z, 1]); return len(nodes)-1
edges = []
for o in objs:
    if 'Walk This Way' not in o['name'] or o['z'] > 100: continue
    t = math.radians(o['rotz']); d = (-math.sin(t), math.cos(t)); h = o['sy']/2
    a = node(o['x']-d[0]*h, o['y']-d[1]*h, o['z']); b = node(o['x']+d[0]*h, o['y']+d[1]*h, o['z'])
    dest = o['name'].split('(')[-1].rstrip(')') if '(' in o['name'] else ''
    edges.append(dict(a=a, b=b, id=o['id'], dest=dest))
def D(i, j): return math.hypot(nodes[i][0]-nodes[j][0], nodes[i][1]-nodes[j][1])
adj = {i: [] for i in range(len(nodes))}
for e in edges:
    if e['a'] != e['b']: adj[e['a']].append(e['b']); adj[e['b']].append(e['a'])
graph = dict(source='phantom "Buddha Center Walk This Way" strips, 1.5 m wide', merge_m=MERGE,
             nodes=[dict(i=i, x=round(n[0], 1), y=round(n[1], 1), z=round(n[2], 1), degree=len(adj[i])) for i, n in enumerate(nodes)],
             edges=edges)
json.dump(graph, open('/workspace/secondlife/research/bc-path-graph.json', 'w'), indent=1)
ends = [i for i in adj if len(adj[i]) == 1]; junc = [i for i in adj if len(adj[i]) > 2]
print(f"{len(edges)} strips -> {len(nodes)} nodes; dead ends {[(round(nodes[i][0]),round(nodes[i][1])) for i in ends]}; junctions {[(round(nodes[i][0]),round(nodes[i][1])) for i in junc]}")
def route(p, q):
    s = min(adj, key=lambda i: math.hypot(nodes[i][0]-p[0], nodes[i][1]-p[1]))
    g = min(adj, key=lambda i: math.hypot(nodes[i][0]-q[0], nodes[i][1]-q[1]))
    dist = {s: 0}; prev = {}; pq = [(0, s)]
    while pq:
        d0, u = heapq.heappop(pq)
        if u == g: break
        for v in adj[u]:
            nd = d0 + D(u, v)
            if nd < dist.get(v, 1e9): dist[v] = nd; prev[v] = u; heapq.heappush(pq, (nd, v))
    if g not in dist: return None
    path = [g]
    while path[-1] != s: path.append(prev[path[-1]])
    return [nodes[i][:3] for i in reversed(path)], dist[g]
if '--route' in args:
    k = args.index('--route'); p = tuple(map(float, args[k+1].split(','))); q = tuple(map(float, args[k+2].split(',')))
    r = route(p, q)
    if r is None: print('no route')
    else:
        pts, L = r; print(f"route {L:.0f} m, {len(pts)} waypoints")
        print(';'.join(f"{x:.1f},{y:.1f},{z:.1f}" for x, y, z in pts))
