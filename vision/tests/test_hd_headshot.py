"""photo hd self --headshot (2026-10-21, David's profile photo): head-and-shoulders framing gets depth of field like the
whole-body portrait, and the flag only applies to `self --hd`."""
import ast, os, subprocess, sys
HERE = os.path.dirname(os.path.abspath(__file__)); SRC = os.path.join(HERE, "..", "cloud-render", "render_mesh.py")

def _fn(name):
    src = open(SRC).read(); node = next(n for n in ast.parse(src).body if isinstance(n, ast.FunctionDef) and n.name == name)
    ns = {}; exec(ast.get_source_segment(src, node), ns); return ns[name]

def test_headshot_has_dof_subject():
    f = _fn("dof_subject")
    assert f("headshot", {}) and f("headshot:20", {}) and f("portrait", {})
    assert not f("eye", {})

def test_headshot_needs_self_hd():
    r = subprocess.run([sys.executable, os.path.join(HERE, "..", "look.py"), "/nonexistent", "view", "--hd", "--headshot"], capture_output=True, text=True)
    if "No module named" in r.stderr:
        import pytest; pytest.skip("look deps")
    assert r.returncode != 0 and "--headshot needs self --hd" in (r.stderr + r.stdout)
