#!/usr/bin/env python3
"""Parse `map` command dumps (textclient) into one JSON list of unique root objects."""
import re, json, sys, glob
pat = re.compile(r"<([-\d.]+),([-\d.]+),([-\d.]+)> size ([\d.]+)x([\d.]+)x([\d.]+) rotZ (-?\d+) (PHANTOM )?'(.*)' ([0-9a-f-]{36})")
objs = {}
for fn in sys.argv[1:-1]:
    for line in open(fn):
        m = pat.search(line)
        if not m: continue
        x,y,z,sx,sy,sz,rz,ph,name,uid = m.groups()
        objs[uid] = dict(id=uid, name=name, x=float(x), y=float(y), z=float(z), sx=float(sx), sy=float(sy), sz=float(sz), rotz=int(rz), phantom=bool(ph))
json.dump(list(objs.values()), open(sys.argv[-1], 'w'), indent=0)
print(len(objs), "objects ->", sys.argv[-1])
