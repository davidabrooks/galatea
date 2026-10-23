#!/usr/bin/env python3
"""Floor contact of her posed avatar mesh (2026-10-23, David: check every pose's hover).
usage: pose_contact.py <export dir> [sit|stand] [--anim=<uuid,...>]
Runs scene-mesher on a `scene export` (avatar only, posed from her playing animations or --anim) and prints one JSON line:
{"lo": lowest contact z in mesh space, "me_z": agent z, "agent_off": mesh z0 - agent z}. Contact = the 0.5th percentile of
the body's vertex heights (hips/thighs in a ground sit, soles standing): robust to a stray strand or chain vertex.
The client compares lo + agent_off + me_z + hover - floor against the approved reference pose (no absolute truth needed).
Read-only; never talks to SL."""
import json, os, sys
import numpy as np
HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, HERE)

def contact_lo(zs, kind="sit"):
    """pure (tested): robust lowest height of the posed body"""
    zs = np.asarray(zs, np.float64)
    if zs.size == 0: return None
    return float(np.percentile(zs, 0.5 if kind == "sit" else 0.2))

def avatar_z(d):
    m = json.load(open(f"{d}/mesh.json")); buf = open(f"{d}/mesh.bin", "rb").read(); zs = []
    for b in m["batches"]:
        if b.get("group") != "avatar" or not b.get("nv"): continue
        P = np.frombuffer(buf, np.float32, b["nv"] * 3, b["offset"]).reshape(-1, 3); zs.append(P[:, 2])
    return m, (np.concatenate(zs) if zs else np.zeros(0))

def main(a):
    d = a[0]; kind = next((x for x in a[1:] if x in ("sit", "stand")), "sit"); extra = [x for x in a[1:] if x.startswith("--anim=")]
    import look
    r = look.run_child(["nice", "-n", "10", look.DOTNET, look.MESHER, d, "12", "avatar"] + extra, env=look.mesher_env(os.environ))
    if r.returncode: sys.exit("scene-mesher failed: " + (r.stderr or r.stdout)[-400:])
    m, zs = avatar_z(d)
    print(json.dumps({"lo": contact_lo(zs, kind), "me_z": m["me"][2], "agent_off": m.get("agent_off"), "kind": kind, "anim": m.get("anim")}))

if __name__ == "__main__":
    main(sys.argv[1:])
