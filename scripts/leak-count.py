#!/usr/bin/env python3
"""Print ONLY occurrence counts of the SECONDLIFE_PASSWORD value in the given files (never the value)."""
import json, os, sys
KEY = "SECONDLIFE_PASSWORD"
def find(o):
    if isinstance(o, dict):
        v = o.get(KEY)
        if isinstance(v, str) and v: return v
        if o.get("name") == KEY or o.get("key") == KEY:
            v = o.get("value")
            if isinstance(v, str) and v: return v
        for v in o.values():
            r = find(v)
            if r: return r
    elif isinstance(o, list):
        for v in o:
            r = find(v)
            if r: return r
    return None
pw = os.environ.get(KEY) or None
if not pw:
    try:
        with open("/home/box/agent-data/box-secrets.json") as f: pw = find(json.load(f))
    except Exception: pw = None
if not pw:
    print("no password available to search for"); sys.exit(1)
b = pw.encode()
for p in sys.argv[1:]:
    try:
        with open(p, "rb") as f: print(f"{p}: {f.read().count(b)}")
    except Exception as e:
        print(f"{p}: unreadable ({type(e).__name__})")
