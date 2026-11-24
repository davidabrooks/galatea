"""photo hd self: wall fallback, closed-backdrop square thigh crop, open vista stays wide."""
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
    m_t, lift_t = framing(3840, 2160, True, True)
    m_tp, lift_tp = framing(2160, 3840, True, True)
    m_q, lift_q = framing(640, 1024, False)
    assert m_p == 0.45 and lift_p == 0.15
    assert m_l > m_p and lift_l == 0.0  # longer, level shot so sky and ground stay in a 16:9 frame
    assert m_t < m_l and lift_t == 0.0 and m_tp == m_t and lift_tp == 0.0
    assert m_q == 0.6 and lift_q == 0.15


def test_portrait_tight_res_is_square_for_closed_standard_hd():
    res = _fn("portrait_tight_res")
    assert res(3840, 2160) == (2048, 2048)  # existing HD square (SL's largest texture)
    assert res(2160, 3840) == (2048, 2048)  # default photo hd self is portrait
    assert res(2048, 2048) == (2048, 2048)
    assert res(1280, 720) == (720, 720)     # GT_HD_RES test hook: short side, not a 4K square
    assert res(720, 1280) == (720, 720)


def test_portrait_tight_box_crops_at_mid_thigh():
    box = _fn("portrait_tight_box")
    # feet at z=0, crown at z=1.70; mid-thigh is 0.35 of height (above the knee at ~0.25)
    mid, ext = box((10.0, 20.0, 0.0), (10.4, 20.3, 1.70))
    assert abs(mid[0] - 10.2) < 1e-9 and abs(mid[1] - 20.15) < 1e-9
    z_lo = 0.35 * 1.70
    assert abs(ext[0] - 0.4) < 1e-9 and abs(ext[1] - 0.3) < 1e-9
    assert abs(ext[2] - (1.70 - z_lo)) < 1e-9
    assert abs(mid[2] - (z_lo + 1.70) / 2) < 1e-9
    assert mid[2] > 0.85  # tighter mid is above the full-body centre
    # a raised hand that already raised the bbox is kept in the crop (top is still bhi.z)
    mid_h, ext_h = box((0, 0, 0), (0.5, 0.4, 2.0))  # hand above the crown
    assert abs(ext_h[2] - (2.0 - 0.35 * 2.0)) < 1e-9


def test_portrait_background_closed_when_hill_or_wall_fills_the_frame():
    closed = _fn("portrait_background_closed")
    # nearby hill / retaining bank: a few metres past her, almost no sky
    assert closed([2.0, 3.5, 4.0, 5.0, 3.0, 6.5, 4.5, 8.0, 3.0] * 4) is True
    # a room wall a couple of metres behind her
    assert closed([1.2, 1.5, 1.4, 1.3, 1.6, 1.2, 1.8, 1.4]) is True
    # a thin strip of sky over a close hillside is still a closed backdrop
    hill = [3.0, 4.0, 5.0, 4.5, 6.0, 3.5, 5.5, 4.0] * 3 + [None, None]
    assert closed(hill) is True
    # a park vista: sky and distant trees / buildings
    assert closed([None, None, None, 40.0, 28.0, 22.0, 8.0, None, 35.0, None] * 2) is False
    # no samples: leave the open landscape framing in place
    assert closed([]) is False

def test_closed_backdrop_policy_square_thigh_crop_open_stays_wide():
    """Closed backdrop (little sky or distant scenery) -> square + mid-thigh crop; an open vista stays 16:9."""
    closed = _fn("portrait_background_closed")
    res = _fn("portrait_tight_res")
    box = _fn("portrait_tight_box")
    hill = [3.0, 4.0, 5.0, 4.5, 6.0, 3.5, 5.5, 4.0] * 3 + [None, None]
    assert closed(hill) is True
    assert res(3840, 2160) == (2048, 2048)
    mid, ext = box((0, 0, 0), (0.4, 0.3, 1.70))
    assert ext[2] < 1.70 and mid[2] > 0.85  # legs cut, frame recentred on the torso
    vista = [None, None, None, 40.0, 28.0, 22.0, 8.0, None, 35.0, None] * 2
    assert closed(vista) is False
    # open path keeps the requested frame (tight_res is only applied when closed)

