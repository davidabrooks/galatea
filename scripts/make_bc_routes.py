#!/usr/bin/env python3
"""Build the Naberrie path graph + named routes for galatay-text RouteNav from research/bc-path-graph.json
(the phantom 'Buddha Center Walk This Way' strips). Output: textclient/routes/_graph-Naberrie.json + <route>.json"""
import json, math, heapq, datetime
OUT = '/home/box/viewers/textclient/routes/'
g = json.load(open('/workspace/secondlife/research/bc-path-graph.json'))
nodes = [[n['x'], n['y'], n['z']] for n in g['nodes']]
edges = {(min(e['a'], e['b']), max(e['a'], e['b'])): 'strip' for e in g['edges']}
def near(x, y): return min(range(len(nodes)), key=lambda i: math.hypot(nodes[i][0]-x, nodes[i][1]-y))
def add(x, y, z): nodes.append([x, y, z]); return len(nodes)-1
def edge(a, b, kind): edges[(min(a, b), max(a, b))] = kind
# zendo: the strip end (94,142) is inside the footprint |x-80.5|+|y-142|<=16.55 (+2 m margin = 18.6) -> move it to x=99.5
zi = near(94, 142); nodes[zi] = [99.5, 142.1, 52.3]
# shortcut across the V at the landing point (Temple arm end <-> Deer Park arm start, 2.4 m)
edge(near(107.0, 144.3), near(109.3, 145.3), 'shortcut')
# pool rock connector (tested 16:18-16:19 PT, minus the stair corner where she got stuck): path node (140.9,147.6) -> (136,144) -> rock
# 2026-09-25 19:55: the diagonal (140.9,147.6)->(136,144) crosses a solid pool lip at y~145.6 going UP (stuck twice);
# go east along the pool front and step up onto the path at x~145 instead (walked OK 19:54 PT)
j = near(144.2, 147.7); c3 = add(145.0, 145.3, 47.3); c2 = add(141.0, 145.0, 47.3); c1 = add(136.0, 144.0, 47.2); pr = add(133.0, 141.8, 47.2)
edge(j, c3, 'connector'); edge(c3, c2, 'connector'); edge(c2, c1, 'connector'); edge(c1, pr, 'connector')
places = {'zendo': zi, 'landing': near(107.2, 149.9), 'waterfall': near(130.2, 147.4), 'poolrock': pr, 'deerpark': near(145.7, 93.7)}
notes = {'zendo': 'zendo east edge, outside the footprint (+2 m margin)', 'landing': 'BC landing point (107,150.8)',
         'waterfall': 'waterfall walkway (raised, z~51.5)', 'poolrock': "pool rock by her pillow 10d8a656 (goto_place sits via sit_home)",
         'deerpark': 'Deer Park end of the marked path'}
graph = dict(region='Naberrie', source='phantom "Buddha Center Walk This Way" strips + connectors', created=datetime.datetime.now().isoformat(timespec='seconds'),
             nodes=[[round(v, 2) for v in n] for n in nodes], edges=[[a, b, k] for (a, b), k in sorted(edges.items())],
             places={k: dict(node=v, note=notes[k]) for k, v in places.items()})
json.dump(graph, open(OUT+'_graph-Naberrie.json', 'w'), indent=1)
adj = {i: [] for i in range(len(nodes))}
for (a, b), k in edges.items(): adj[a].append(b); adj[b].append(a)
def D(a, b): return math.hypot(nodes[a][0]-nodes[b][0], nodes[a][1]-nodes[b][1])
def sp(s, t, avoid_shortcut=False):
    dist = {s: 0}; prev = {}; pq = [(0, s)]
    while pq:
        d, u = heapq.heappop(pq)
        for v in adj[u]:
            if avoid_shortcut and edges.get((min(u, v), max(u, v))) == 'shortcut': continue
            nd = d + D(u, v)
            if nd < dist.get(v, 1e9): dist[v] = nd; prev[v] = u; heapq.heappush(pq, (nd, v))
    p = [t]
    while p[-1] != s: p.append(prev[p[-1]])
    return list(reversed(p)), dist[t]
routes = {'bc-main': ('zendo', 'deerpark', True, 'whole marked path: zendo east edge -> landing point -> waterfall walkway -> Deer Park'),
          'poolrock-deerpark': ('poolrock', 'deerpark', False, 'pool rock -> connector -> path -> Deer Park'),
          'poolrock-landing': ('poolrock', 'landing', False, 'pool rock -> connector -> waterfall walkway -> landing point'),
          'poolrock-zendo': ('poolrock', 'zendo', False, 'pool rock -> waterfall walkway -> shortcut -> zendo east edge')}
for name, (a, b, avoid, note) in routes.items():
    p, L = sp(places[a], places[b], avoid)
    json.dump(dict(name=name, region='Naberrie', source='bc-strips-graph', created=graph['created'], note=note,
                   points=[[round(v, 2) for v in nodes[i]] for i in p]), open(OUT+name+'.json', 'w'), indent=1)
    print(f"{name}: {len(p)} pts, {L:.0f} m")
print('places', {k: nodes[v] for k, v in places.items()})
