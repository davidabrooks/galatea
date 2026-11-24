"""photo hd self: a low wall must not crop the body; only a room wall may stop the camera short."""
import ast, math, os

SRC = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "cloud-render", "render_mesh.py")

def _fn(name):
    src = open(SRC).read()
    node = next(n for n in ast.parse(src).body if isinstance(n, ast.FunctionDef) and n.name == name)
    ns = {"math": math}
    exec(ast.get_source_segment(src, node), ns)
    return ns[name]


def test_portrait_shot_fits_requires_enough_distance():
    fits = _fn("portrait_shot_fits")
    mid = (0.0, 0.0, 1.0)
    assert fits(mid, (0.0, 4.0, 1.6), 4.0) is True
    assert fits(mid, (0.0, 1.0, 1.1), 4.0) is False  # camera stopped a metre away, body would be cropped
    assert fits(mid, (0.0, 3.85, 1.5), 4.0) is True  # 5% slop


def test_portrait_low_obstacle_is_a_curb_not_a_room_wall():
    low = _fn("portrait_low_obstacle")
    head_z, mid_z = 1.63, 0.85
    assert low(0.9, head_z, mid_z) is True    # retaining wall / curb around mid-thigh
    assert low(1.7, head_z, mid_z) is False   # hit at head height: a room wall
    assert low(1.5, head_z, mid_z) is False   # chest/head-adjacent


def test_portrait_framing_landscape_pulls_back_level():
    framing = _fn("portrait_framing")
    m_p, lift_p = framing(2160, 3840, True)
    m_l, lift_l = framing(3840, 2160, True)
    m_q, lift_q = framing(640, 1024, False)
    assert m_p == 0.45 and lift_p == 0.15
    assert m_l > m_p and lift_l == 0.0  # longer, level shot so sky and ground stay in a 16:9 frame
    assert m_q == 0.6 and lift_q == 0.15
