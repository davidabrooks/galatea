"""2026-10-14 saturation pass: the classic sky's sun colour at a surface is atmosphere-attenuated (warm), not the raw EEP sunlight."""
import math, os, re
HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = open(os.path.join(HERE, "cloud-render", "render_mesh.py")).read()
ns = {"math": math, "os": os}
exec(SRC[SRC.index("def atmos_sunlit"):SRC.index("def retint_sun")], ns)

ENV = {"sunlight": [1.1734, 1.2109, 1.3046], "sun_dir": [-0.8038, 0.0, 0.5949], "max_y": 1387.4,
       "haze": {"blue_density": [0.22398, 0.43855, 0.76835], "haze_density": 0.7, "density_multiplier": 0.00023844}}


def test_sun_is_warm_at_the_deck():
    r, g, b = ns["atmos_sunlit"](ENV, -0.15)
    assert r > g > b and b / r < 0.8          # raw sunlight is b > r (1.30 / 1.17)
    assert ns["atmos_sunlit"](ENV, 0.5)[2] / ns["atmos_sunlit"](ENV, 0.5)[0] > b / r   # a higher view ray: less path, less warm


def test_no_haze_params_no_tint():
    assert ns["atmos_sunlit"]({"sunlight": [1, 1, 1], "sun_dir": [0, 0, 1]}, 0.0) is None


def test_defaults_documented():
    assert 'GT_SKY_SCALE", "0.66,0.70,0.76"' in SRC and 'GT_PBR_SPEC", "0.15"' in SRC and 'GT_CLASSIC_WORLD", "0.1"' in SRC
