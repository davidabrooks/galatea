#!/usr/bin/env python3
"""check: scene-mesher marks avatars without attachment prims as placeholder. run: python vision/test_crowd_placeholders.py"""
import json, os, subprocess, shutil, tempfile
MESHER = os.environ.get("SCENE_MESHER", "/home/box/tools/scene-mesher/SceneMesher.dll")
DOTNET = shutil.which("dotnet") or os.path.expanduser("~/.dotnet/dotnet")
SRC = "/workspace/secondlife/vision/export-20261004-225419"
assert os.path.isdir(SRC), "Warehouse 21 export missing"
with tempfile.TemporaryDirectory() as d:
    shutil.copytree(SRC, d, dirs_exist_ok=True)
    # drop attachment prims so every avatar is a placeholder
    doc = json.load(open(f"{d}/scene.json"))
    doc["prims"] = [p for p in doc["prims"] if not p.get("attached_to") and not p.get("attached_to_me")]
    json.dump(doc, open(f"{d}/scene.json", "w"))
    me = doc["me"]["pos"]
    r = subprocess.run([DOTNET, MESHER, d, "12", "all", f"{me[0]},{me[1]},{me[2]}", "30", "--roots=32"],
                       capture_output=True, text=True)
    assert r.returncode == 0, r.stderr[-400:]
    meta = json.load(open(f"{d}/mesh.json"))
    others = meta["others"]
    assert len(others) == len(doc["avatars"]), (len(others), len(doc["avatars"]))
    assert all(o.get("placeholder") for o in others), "expected every avatar to be a placeholder"
print(f"test_crowd_placeholders ok ({len(others)} placeholders)")
