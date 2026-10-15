#!/usr/bin/env python3
"""Which animations was she playing at the export moment, and how far into each? -> scene-mesher --anim= value.
usage: anims_at.py <textclient.log> <scene.json>   (also reads anim-events.log in the same directory)   prints  uuid@seconds,uuid@seconds,...
Replays the client's '[anim] start|RESTART|stop <uuid>' lines up to the export's exported_at (a RESTART restarts the clock;
a '[login] OK' line clears the set, since a logout leaves no stop lines).
ponytail: log timestamps are whole seconds, so each phase is +-1 s; named built-ins (STAND, WALK, ...) are skipped
because an AO overrides them; ease-in/out weights are ignored (an anim counts fully from its start line).
"""
import json, os, re, sys
from datetime import datetime

log, scene = sys.argv[1], sys.argv[2]
at = datetime.fromisoformat(json.load(open(scene))["exported_at"]).replace(tzinfo=None)  # box-local, as the log
line = re.compile(r"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) \[anim\] (start|RESTART|stop) ([0-9a-f]{8}-[0-9a-f-]{27})\b")

def events(log, at):
    """(time, order, kind, uuid): the log itself ([login] OK + any [anim] lines: older logs, or GT_ANIM_LOG=1) plus anim-events.log beside it
    (where the client writes [anim] lines by default since 2026-10-15). Both are time-ordered; merged by time, login first on ties."""
    ev = []
    for path in (log, os.path.join(os.path.dirname(os.path.abspath(log)), "anim-events.log")):
        if not os.path.exists(path): continue
        for l in open(path, errors="replace"):
            if " [login] OK " in l:
                try: t = datetime.strptime(l[:19], "%Y-%m-%d %H:%M:%S")
                except ValueError: continue
                if t <= at: ev.append((t, 0, "login", None))
                continue
            m = line.match(l)
            if m: ev.append((datetime.strptime(m[1], "%Y-%m-%d %H:%M:%S"), 1, m[2], m[3]))
    ev.sort(key=lambda e: (e[0], e[1]))   # stable: same-second lines keep their file order
    return ev

active = {}
for t, _, kind, u in events(log, at):
    if t > at: break
    if kind == "login": active.clear()
    elif kind == "stop": active.pop(u, None)
    else: active[u] = t
print(",".join(f"{u}@{(at - t).total_seconds():.1f}" for u, t in sorted(active.items(), key=lambda kv: kv[1])))
