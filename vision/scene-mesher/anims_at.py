#!/usr/bin/env python3
"""Which animations was she playing at the export moment, and how far into each? -> scene-mesher --anim= value.
usage: anims_at.py <textclient.log> <scene.json>   prints  uuid@seconds,uuid@seconds,...
Replays the client's '[anim] start|RESTART|stop <uuid>' lines up to the export's exported_at (a RESTART restarts the clock;
a '[login] OK' line clears the set, since a logout leaves no stop lines).
ponytail: log timestamps are whole seconds, so each phase is +-1 s; named built-ins (STAND, WALK, ...) are skipped
because an AO overrides them; ease-in/out weights are ignored (an anim counts fully from its start line).
"""
import json, re, sys
from datetime import datetime

log, scene = sys.argv[1], sys.argv[2]
at = datetime.fromisoformat(json.load(open(scene))["exported_at"]).replace(tzinfo=None)  # box-local, as the log
line = re.compile(r"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) \[anim\] (start|RESTART|stop) ([0-9a-f]{8}-[0-9a-f-]{27})\b")
active = {}
for l in open(log, errors="replace"):
    if " [login] OK " in l and datetime.strptime(l[:19], "%Y-%m-%d %H:%M:%S") <= at: active.clear()
    m = line.match(l)
    if not m: continue
    t = datetime.strptime(m[1], "%Y-%m-%d %H:%M:%S")
    if t > at: break
    if m[2] == "stop": active.pop(m[3], None)
    else: active[m[3]] = t
print(",".join(f"{u}@{(at - t).total_seconds():.1f}" for u, t in sorted(active.items(), key=lambda kv: kv[1])))
