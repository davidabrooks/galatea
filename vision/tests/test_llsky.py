"""2026-10-06: region/parcel EEP sky as the SL viewer computes it (vision/cloud-render/llsky.py; pure, no Blender).
The Ahern four-corner render (09:27 PT) came out with a flat mid-grey sky: Ahern's legacy 4-hour cycle was at night
(sun ~49 deg below the horizon), and the renderer drew night as the day gradient x 0.35 instead of the viewer's sky."""
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "cloud-render"))
import llsky  # noqa: E402

# Linden "Region (legacy)" keyframes (from the Ahern export): midnight and 03:00 (sky track keys 0 and 0.125)
MIDNIGHT = {"sun_rotation": [None, 0.7071068, None, 0.7071068], "moon_rotation": [None, -0.7071068, 0, 0.7071068],
            "sunlight_color": [0.3488, 0.3557, 0.66, 0.22], "moon_brightness": 0.5, "star_brightness": 500, "max_y": 906.2,
            "cloud_shadow": 0.27, "glow": [5, 0.001, -0.48],
            "legacy_haze": {"blue_density": [0.45, 0.45, 0.45], "blue_horizon": [0.24, 0.24, 0.24], "haze_density": 4,
                            "density_multiplier": 0.0003, "distance_multiplier": 0.0001}}
THREE = {**MIDNIGHT, "sun_rotation": [None, 0.3826834, None, 0.9238795], "sunlight_color": [0.6024, 0.6145, 1.14, 0.38],
         "legacy_haze": {**MIDNIGHT["legacy_haze"], "haze_horizon": 4.6e-06}}
NOON = {"sun_rotation": [None, -0.7071068, None, 0.7071068], "moon_rotation": [None, 0.7071068, 0, 0.7071068],
        "sunlight_color": [0.7342, 0.7816, 0.9, 0.3], "ambient": [1.05, 1.05, 1.05], "max_y": 1605, "cloud_shadow": 0.27,
        "legacy_haze": {"blue_density": [0.2448, 0.4487, 0.76], "blue_horizon": [0.4955, 0.4955, 0.64], "haze_density": 0.7,
                        "haze_horizon": 0.19, "density_multiplier": 0.00018}}


def doc(frames, keys, exported_at, offset=57600, length=14400):
    return {"exported_at": exported_at, "sun_dir": [0, 0, 1], "environment": {"parcel_id": -1, "environment": {
        "day_length": length, "day_offset": offset, "day_cycle": {"frames": frames, "tracks": [
            [{"key_keyframe": 0, "key_name": "w"}], [{"key_keyframe": k, "key_name": n} for k, n in keys]]}}}}


def test_sun_direction_from_eep_rotation():
    assert [round(c, 3) for c in llsky.qrot_x(MIDNIGHT["sun_rotation"])] == [0, 0, -1]   # straight down
    assert [round(c, 3) for c in llsky.qrot_x(NOON["sun_rotation"])] == [0, 0, 1]


def test_ahern_0927_is_night_and_the_viewer_sky_is_dark():
    # 2026-10-06 09:27:09 PT: day position 0.113 of Ahern's 4 h cycle -> between midnight and 03:00, sun below
    e = llsky.env_at(doc({"m": MIDNIGHT, "t": THREE}, [(None, "m"), (0.125, "t")], "2026-10-06T09:27:09.3536266-07:00"))
    assert abs(e["frac"] - 0.1131) < 1e-3
    assert e["sun_dir"][2] < -0.7 and e["moon_dir"][2] > 0.7            # the simulator said z = -0.81 too
    ns = e["night_sky"]
    assert max(ns["horizon"] + ns["zenith"]) < 0.06                   # the old render: 0.24 x 0.35 = 0.084, grey
    assert ns["zenith"][2] > ns["zenith"][0] and ns["stars"] > 0.99    # faintly blue, full stars


def test_day_has_no_night_sky_and_keeps_the_eep_colours():
    e = llsky.env_at(doc({"n": NOON}, [(None, "n")], "2026-10-06T09:27:09.3536266-07:00"))   # fixed sky: one frame
    assert e["sun_dir"][2] > 0.99 and "night_sky" not in e
    assert e["horizon"] == [0.4955, 0.4955, 0.64] and e["ambient"] == [1.05, 1.05, 1.05]
    p = llsky.params(NOON)   # the same shader by day is bright and blue at the horizon (sanity of the port)
    h = llsky.sky_rgb(p, 1.0, True, 1.0, 2)
    assert min(h) > 0.5 and h[2] > h[0]


def test_no_day_cycle_is_none():
    assert llsky.env_at({"environment": "error: x", "exported_at": "2026-10-06T09:27:09.3536266-07:00"}) is None
