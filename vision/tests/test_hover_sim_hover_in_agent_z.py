"""2026-10-31 18:26 relog: the sim's appearance hover became the login POST -0.08 and her agent z dropped 28.953 -> 28.873 at the same spot.
The render drew her 8 cm sunk (-0.062 at the right 0.012) and set 0.074; David: floating. With GT_SIM_HOVER taken out, both z read the same."""
from test_hover_floor_missing import _rm

def test_1826_low_agent_z_reads_like_the_1806_one():
    rm = _rm()
    base = {"GT_HOVER_CHECK": "1", "GT_AGENT_SIZE": "1.572", "GT_HOVER": "0.012", "GT_HOVER_BIAS": "0.1"}
    _, g0606, *_ = rm["hover_check_z0"](dict(base, GT_SIM_HOVER="0"), 28.953, 0.0, 28.07)
    _, g0626, *_ = rm["hover_check_z0"](dict(base, GT_SIM_HOVER="-0.080"), 28.873, 0.0, 28.07)
    assert abs(g0606 - g0626) < 1e-6
    _, old, *_ = rm["hover_check_z0"](base, 28.873, 0.0, 28.07)   # no GT_SIM_HOVER: the old, 8 cm sunk reading
    assert abs((g0606 - old) - 0.08) < 1e-6
