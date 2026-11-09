"""2026-11-09: Blender 4.2.3 (EOL) -> 4.5.15 LTS. Every default path and the Runpod bootstrap name the same build, and
render_mesh drops degenerate triangles (a repeated vertex) before Mesh.update(): Blender 4.5 segfaulted there on them."""
import os, re

V = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILD = "blender-4.5.15-linux-x64"


def _src(*p):
    return open(os.path.join(V, *p)).read()


def test_default_paths_name_one_build():
    for p in (("look.py",), ("look_cam.py",), ("cloud-render", "render_local.py")):
        found = set(re.findall(r"/home/box/tools/(blender-[\d.]+-linux-x64)/blender", _src(*p)))
        assert found == {BUILD}, (p, found)


def test_bootstrap_downloads_and_verifies_the_same_build():
    s = _src("cloud-render", "bootstrap.sh")
    assert f"BL={BUILD}" in s and "release/Blender4.5/$BL.tar.xz" in s
    assert re.search(r"BL_SHA256=[0-9a-f]{64}", s) and "sha256sum -c" in s


def test_degenerate_triangles_dropped_before_mesh_update():
    s = _src("cloud-render", "render_mesh.py")
    i = s.index("(t3[:, 0] != t3[:, 1]) & (t3[:, 1] != t3[:, 2]) & (t3[:, 0] != t3[:, 2])")
    j = s.index("me = bpy.data.meshes.new(b[\"tex\"][:40])", i)
    k = s.index("me.update()", j)
    assert i < j < k and "I = t3[ok].ravel(); ni = len(I)" in s[i:j]
