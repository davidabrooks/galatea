"""PR #33: crowd placeholders: avatars with no attachment prims are meshed as placeholders (stand-ins).
2026-11-23: runs on a tiny synthetic checked-in export (3 made-up avatars, flat terrain, no prims, no bakes) instead of a
box export that no longer exists; needs only scene-mesher (SCENE_MESHER), so it runs on the box and skips where the mesher is not built."""
import json
import os
import shutil
import subprocess
import tempfile

import pytest

MESHER = os.environ.get("SCENE_MESHER", "/home/box/tools/scene-mesher/SceneMesher.dll")
SRC = os.environ.get("CROWD_EXPORT") or os.path.join(os.path.dirname(os.path.abspath(__file__)), "fixtures", "crowd-placeholders")
DOTNET = shutil.which("dotnet") or os.path.expanduser("~/.dotnet/dotnet")


@pytest.mark.skipif(not os.path.isfile(MESHER) or not os.path.isdir(SRC),
                    reason="SCENE_MESHER not built here (CI builds it in the C# job only)")
def test_pr33_avatars_without_attachments_are_placeholders():
    with tempfile.TemporaryDirectory() as d:
        shutil.copytree(SRC, d, dirs_exist_ok=True)
        doc = json.load(open(f"{d}/scene.json"))
        doc["prims"] = [p for p in doc["prims"] if not p.get("attached_to") and not p.get("attached_to_me")]
        json.dump(doc, open(f"{d}/scene.json", "w"))
        me = doc["me"]["pos"]
        r = subprocess.run(
            [DOTNET, MESHER, d, "12", "all", f"{me[0]},{me[1]},{me[2]}", "30", "--roots=32"],
            capture_output=True, text=True, timeout=120,
        )
        assert r.returncode == 0, r.stderr[-400:]
        meta = json.load(open(f"{d}/mesh.json"))
        others = meta["others"]
        assert len(others) == len(doc["avatars"])
        assert all(o.get("placeholder") for o in others)
