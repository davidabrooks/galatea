"""2026-10-31 David: no hardcoded adjustments; the contact is the average height of all her touchpoints. Touchpoints come from geometry
(the regions reaching down to where the points under her surround her pelvis), never pose names."""
import numpy as np
from test_hover_floor_missing import _rm

def test_com_inside():
    f = _rm()["com_inside"]
    sq = [(1, 1), (-1, 1), (-1, -1), (1, -1)]
    assert f(sq, (0, 0)) and not f(sq, (2, 0)) and not f([(1, 0), (2, 0)], (0, 0))

def test_heel_in_front_does_not_hold_her_up():
    """20:37 geometry: one heel stuck out ahead is the lowest point, the buttocks around the pelvis are 7 cm higher: support is the buttocks' height"""
    rm = _rm(); rng = np.random.default_rng(1)
    heel = np.c_[rng.normal(0.8, 0.01, 50), rng.normal(0, 0.01, 50), np.full(50, 0.0)]
    a = rng.uniform(0, 2 * np.pi, 400); butt = np.c_[0.1 * np.cos(a), 0.1 * np.sin(a), np.full(400, 0.07)]
    assert abs(rm["support_z"](np.r_[heel, butt], (0.0, 0.0)) - 0.07) < 1e-9

def test_standing_feet_either_side():
    rm = _rm(); feet = np.array([(0.0, 0.1, 0.0), (0.0, -0.1, 0.0), (0.2, 0.1, 0.0), (0.2, -0.1, 0.0)]); assert rm["support_z"](feet, (0.1, 0.0)) == 0.0
