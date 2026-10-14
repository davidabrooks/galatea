"""photo hd day sky (2026-10-14 Beach House boat vs David's Firestorm shot): the classic sky shader's colours are display
values (Firestorm draws them untonemapped), so render_mesh converts them sRGB -> linear before Cycles, like the night sky."""
import ast, os
SRC = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "cloud-render", "render_mesh.py")

def _fn(name):
    tree = ast.parse(open(SRC).read())
    node = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == name)
    return ast.get_source_segment(open(SRC).read(), node)

def test_day_sky_hd_branch_linearises():
    src = _fn("sky_seen")
    day = src.split('sk = env.get("sky")', 1)[1].split("h = env[", 1)[0]
    assert day.count("srgb_to_linear(") == 2, day

def test_srgb_to_linear_matches_davids_horizon():
    ns = {}; exec(_fn("srgb_to_linear"), ns)
    # Peronaut shared sky horizon 0.409/0.482/0.697 must come out ~ his 107/127/184 once Blender re-encodes to sRGB
    lin = [ns["srgb_to_linear"](c) for c in (0.409, 0.482, 0.697)]
    assert all(0.13 < v < 0.5 for v in lin) and lin[2] > lin[1] > lin[0]
