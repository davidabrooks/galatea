"""2026-10-13 (David): photo hd follows every shared EEP setting by default, named presets override it, and her own
quick looks use no environment at all."""
import json, math, os, sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(HERE, "cloud-render"))
import llsky  # noqa: E402
from test_llsky import MIDNIGHT, NOON, doc  # noqa: E402

RM = open(os.path.join(HERE, "cloud-render", "render_mesh.py")).read()
LOOK = open(os.path.join(HERE, "look.py")).read()
WATER = {"type": "water", "water_fog_color": [0.1, 0.2, 0.3], "water_fog_density": 4, "fresnel_scale": 0.6, "fresnel_offset": 0.5,
         "normal_scale": [2, 2, 2], "wave1_direction": [1, 0], "wave2_direction": [0, 1], "underwater_fog_mod": 0.25}


def shared(frames=None, keys=None):
    d = doc(frames or {"n": {**NOON, "gamma": 1.4, "sun_scale": 2, "moon_scale": 0.5, "cloud_scroll_rate": [0.2, 0.01],
                              "cloud_pos_density2": [1, 0.5, 0.3]}}, keys or [(None, "n")], "2026-10-06T09:27:09.3536266-07:00")
    dc = d["environment"]["environment"]["day_cycle"]; dc["frames"]["w"] = WATER
    return d


def test_shared_exports_every_supported_setting():
    e = llsky.env_at(shared())
    assert e["source"] == "shared" and e["gamma"] == 1.4 and e["sun_scale"] == 2 and e["moon_scale"] == 0.5
    assert set(e["haze"]) == {"blue_density", "haze_density", "haze_horizon", "density_multiplier", "distance_multiplier"}
    assert e["clouds"]["detail"] == 0.3 and len(e["clouds"]["offset"]) == 2 and e["clouds"]["scroll"] == [0.2, 0.01]
    assert e["water"]["fog_color"] == [0.1, 0.2, 0.3] and e["water"]["fresnel_scale"] == 0.6
    assert e["sky"]["horizon"][2] > e["sky"]["horizon"][0]   # the viewer's day sky shader: blue


def test_presets_from_the_viewer_library():
    names = set(llsky.presets())
    assert {"default", "midday", "sunrise", "sunset", "midnight"} <= names
    up = lambda n: llsky.preset_env(n)["sun_dir"][2]
    assert up("midday") > 0.99 and up("midnight") < -0.99
    assert 0 < up("sunset") < 0.2 and llsky.preset_env("sunset")["sun_dir"][0] < -0.9   # low in the west
    assert 0 < up("sunrise") < 0.2 and llsky.preset_env("sunrise")["sun_dir"][0] > 0.9    # low in the east
    assert llsky.preset_env("midnight")["moon_dir"][2] > 0.99 and llsky.preset_env("midnight")["stars"] == 1.0
    s = llsky.preset_env("sunset")["sky"]["horizon"]; assert s[0] > s[2]                   # warm horizon
    assert llsky.preset_env("mars") is None


def test_dir_quat_round_trips():
    for d in ([1, 0, 0], [-1, 0, 0], [0, 0, 1], llsky.azalt_dir(2.0, 0.3)):
        n = math.sqrt(sum(c * c for c in d)); d = [c / n for c in d]
        assert all(abs(a - b) < 1e-6 for a, b in zip(llsky.qrot_x(llsky.dir_quat(d)), d))


def test_photo_env_default_is_shared_and_preset_keeps_shared_water():
    d = shared()
    assert llsky.photo_env(d)["source"] == "shared" and llsky.photo_env(d, "shared")["source"] == "shared"
    p = llsky.photo_env(d, "Sunset"); assert p["source"] == "preset:Sunset" and p["water"]["fog_density"] == 4
    try: llsky.photo_env(d, "mars"); assert False
    except ValueError as e: assert "sunset" in str(e)


def test_quick_looks_have_no_environment():
    assert 'meta["env"] = env_for(doc, hd, sky)' in LOOK and "if not hd: return None" in LOOK
    assert "make_job.eep(doc)" not in LOOK
    assert 'meta["env"] = None' in open(os.path.join(HERE, "look_cam.py")).read()
    assert '--sky needs --hd' in LOOK


def test_renderer_maps_the_hd_settings():
    for s in ('sc.view_settings.gamma', 'use_pass_mist', '"distance_multiplier"', 'env.get("sun_scale"', 'env.get("moon_scale"',
              'cl.get("offset")', 'cl.get("detail"', 'wt["fog_color"]', 'wt.get("fresnel_scale"', 'if HD and kind == "scene" and M.get("env"): env_post'):
        assert s in RM, s
