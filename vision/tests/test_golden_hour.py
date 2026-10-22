"""2026-10-21: an outdoor hd portrait after sunset gets golden hour from the sun's azimuth; not under a roof, not with --sky."""
import math, os, struct, tempfile
HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = open(os.path.join(HERE, "look.py")).read()
ns = {"os": os, "llsky": type("S", (), {"preset_env": staticmethod(lambda n, d: {"sun_dir": [0.0, 0.9, 0.0872], "moon_dir": [0, -0.9, -0.0872]})})}
exec(SRC[SRC.index("def mesh_under_roof"):SRC.index("def mesher_env")], ns)
ENV = {"sun_dir": [-0.6, -0.6, -0.5292], "source": "x"}


def test_golden_hour_keeps_azimuth_and_rises_sun():
    g = ns["golden_hour_env"](None, ENV, True, None)
    assert g["sun_dir"][2] > 0 and abs(math.atan2(g["sun_dir"][1], g["sun_dir"][0]) - math.atan2(-0.6, -0.6)) < 1e-6
    assert g["source"].startswith("golden-hour")


def test_not_applied():
    gh = ns["golden_hour_env"]
    assert gh(None, ENV, False, None) is None and gh(None, ENV, True, "midday") is None
    assert gh(None, {"sun_dir": [0, 0.8, 0.6]}, True, None) is None


def _bin(tri_z):
    P = [(-5, -5, tri_z), (5, -5, tri_z), (0, 5, tri_z)]
    raw = b"".join(struct.pack("3f", *p) for p in P) + b"\0" * (3 * 32 - 36) + struct.pack("3I", 0, 1, 2)
    f = tempfile.NamedTemporaryFile(delete=False); f.write(raw); f.close()
    return f.name, {"me": [0, 0, 0], "batches": [{"group": "scene", "nv": 3, "ni": 3, "offset": 0}]}


def test_roof_ray():
    p, m = _bin(4.0); assert ns["mesh_under_roof"](m, p)
    p, m = _bin(1.0); assert not ns["mesh_under_roof"](m, p)
    m["batches"][0]["group"] = "avatar:x"; p, _ = _bin(4.0); assert not ns["mesh_under_roof"](m, p)
