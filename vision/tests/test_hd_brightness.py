"""2026-10-13 'why are your photos so much darker?': Firestorm classic-sky ambient scale, linear light colours, moonlight."""
import os
HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = open(os.path.join(HERE, "cloud-render", "render_mesh.py")).read()
ns = {"os": os, "lin": lambda c: c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4}
exec(SRC[SRC.index("def light_linear"):SRC.index("AMB = amblit")], ns)
ENV = {"ambient": [0.25] * 3, "cloud_shadow": 0.27, "sunlight": [1.10, 1.11, 1.32], "moon": 0.5}


def test_light_colour_is_already_linear():
    assert ns["light_linear"]([1, 0.8039, 0.6078]) == [1, 0.8039, 0.6078] and ns["light_linear"]([None, 2, -1]) == [0.0, 1.0, 0.0]


def test_classic_ambient_matches_firestorm():
    # Peronaut night: lin(0.2 x 1.5 x 0.9) x 1.1 ~ 0.065 (was lin(0.2) = 0.033)
    assert abs(ns["amblit"](ENV) - 0.0652) < 0.002
    assert abs(ns["amblit"]({**ENV, "classic": False}) - 0.0331) < 0.002


def test_moon_stronger_than_before_and_zero_without_moon():
    assert 0.8 < ns["moon_strength"](ENV) < 1.7          # was 0.25 x 0.5 = 0.125; 0.36 cal ~0.6, 0.75 cal (full SSAO) ~1.2
    assert ns["moon_strength"]({**ENV, "moon": 0.0}) == 0.0
