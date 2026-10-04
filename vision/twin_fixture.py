#!/usr/bin/env python3
# usage: twin_fixture.py <export dir> <out dir>  then: look.py <out dir> at "test twin"   (the runnable check for the
#   other-avatar path when nobody is nearby)
# fixture: clone her as a second avatar ("Test Twin") into a copy of an export, to exercise the other-avatar path
import json, shutil, sys, glob, os
src, dst = sys.argv[1:3]; shutil.rmtree(dst, ignore_errors=True); os.makedirs(dst)
d = json.load(open(f"{src}/scene.json")); fake = "d00dfeed-0000-4000-8000-000000000001"; FL = 99999999
U = lambda l: int.from_bytes(bytes(l), "big"); B = lambda v: list(v.to_bytes(4, "big"))
mine = {U(o["localid"]) for o in d["prims"] if o.get("attached_to_me")}
for o in [o for o in d["prims"] if o.get("attached_to_me")]:
    c = json.loads(json.dumps(o)); del c["attached_to_me"]; c["attached_to"] = fake
    c["localid"] = B(U(o["localid"]) + 10_000_000); c["parentid"] = B(U(o["parentid"]) + 10_000_000 if U(o["parentid"]) in mine else FL)
    d["prims"].append(c)
p = d["me"]["pos"]
d["avatars"].append({"pos": [p[0] + 1.0, p[1] - 1.6, p[2]], "rot": [0, 0, 0.7071, 0.7071], "agent_id": fake, "name": "Test Twin", "local_id": FL,
                     "visual_params": d["visual_params"], "bakes": d["bakes"], "anims": d["me"].get("anims", "")})
json.dump(d, open(f"{dst}/scene.json", "w"))
for f in glob.glob(f"{src}/bake-*.j2c"): shutil.copy(f, f"{dst}/bake-{fake[:8]}-{os.path.basename(f)[5:]}"); shutil.copy(f, dst)
