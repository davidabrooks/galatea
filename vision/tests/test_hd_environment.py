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
    assert {"default", "midday", "midday-legacy", "sunrise", "sunset", "midnight"} <= names
    up = lambda n: llsky.preset_env(n)["sun_dir"][2]
    assert up("midday-legacy") > 0.99 and up("midnight") < -0.99
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
    for s in ('use_pass_mist', '"distance_multiplier"', 'env.get("sun_scale"', 'env.get("moon_scale"',
              'cl.get("offset")', 'cl.get("detail"', 'wt["fog_color"]', 'wt.get("fresnel_scale"', 'if HD and kind == "scene" and M.get("env"): env_post'):
        assert s in RM, s


def test_photo_env_noenv_falls_back_to_default_sky():
    """HD photos of a noenv export (no parcel/region EEP) use the viewer Default sky, not a missing env."""
    d = {"environment": "skipped: noenv", "exported_at": "2026-11-23T17:25:52.7634633-08:00", "sun_dir": [0.9, 0, 0.44]}
    e = llsky.photo_env(d)
    assert e is not None and e["source"].startswith("default-fallback:")
    assert e["sun_dir"][2] > 0.9 and e["sky"]["horizon"][2] > e["sky"]["horizon"][0]  # day sky, sun up
    assert llsky.photo_env(d, "shared")["source"].startswith("default-fallback:")
    named = llsky.photo_env(d, "sunset")
    assert named["source"] == "preset:Sunset"
    # a usable shared environment is unchanged
    assert llsky.photo_env(shared())["source"] == "shared"


def far_env(frames, sky_keys, water_frames=None, water_keys=None, region_time_s=0, day_length=14400, day_offset=0,
            me_z=0.0, track_altitudes=None, extra_sky_tracks=None, exported_at_utc="2026-11-23T18:00:00Z"):
    """A `look around far env` wrap: raw ExtEnvironment plus source, timestamps, and region_time_s."""
    tracks = [water_keys or [{"key_keyframe": 0, "key_name": "w"}],
              [{"key_keyframe": k if k is not None else 0.0, "key_name": n} for k, n in sky_keys]]
    if extra_sky_tracks:
        tracks.extend(extra_sky_tracks)
    frames = dict(frames)
    if water_frames:
        frames.update(water_frames)
    elif "w" not in frames:
        frames["w"] = WATER
    inner = {"day_length": day_length, "day_offset": day_offset, "day_cycle": {"frames": frames, "tracks": tracks},
             "flags": 0, "version": 1}
    wrap = {"environment": inner, "source": "region", "region": "Test", "parcel_id": -1,
            "exported_at_utc": exported_at_utc, "region_time_s": region_time_s}
    if track_altitudes is not None:
        wrap["track_altitudes"] = track_altitudes
        inner["track_altitudes"] = track_altitudes
    return {"environment": wrap, "me": {"pos": [128.0, 128.0, me_z]}, "sun_dir": [0, 0, 1]}


def test_region_time_blends_the_two_surrounding_keyframes():
    """region_time_s / day_length selects the two sky keys around that instant and blends them."""
    # keys at 0 (midnight, sun down) and 0.5 (noon, sun up); half-way at 0.25 should lift the sun
    d = far_env({"m": MIDNIGHT, "n": NOON}, [(0.0, "m"), (0.5, "n")], region_time_s=int(0.25 * 14400))
    e = llsky.env_at(d)
    assert e is not None and e["source"] == "shared"
    assert abs(e["frac"] - 0.25) < 1e-9
    assert e["sun_dir"][2] > llsky.qrot_x(MIDNIGHT["sun_rotation"])[2]
    assert e["sun_dir"][2] < llsky.qrot_x(NOON["sun_rotation"])[2]
    # water also blends when the water track has two keys
    w0 = {**WATER, "water_fog_color": [0.0, 0.0, 0.0], "water_fog_density": 0}
    w1 = {**WATER, "water_fog_color": [1.0, 1.0, 1.0], "water_fog_density": 10}
    d2 = far_env({"m": MIDNIGHT, "n": NOON}, [(0.0, "m"), (0.5, "n")],
                 water_frames={"w0": w0, "w1": w1},
                 water_keys=[{"key_keyframe": 0.0, "key_name": "w0"}, {"key_keyframe": 0.5, "key_name": "w1"}],
                 region_time_s=int(0.25 * 14400))
    w = llsky.env_at(d2)["water"]
    assert all(abs(c - 0.5) < 1e-9 for c in w["fog_color"]) and abs(w["fog_density"] - 5) < 1e-9


def test_night_frame_has_stars_and_the_moon():
    d = far_env({"m": MIDNIGHT}, [(0.0, "m")], region_time_s=0)
    e = llsky.env_at(d)
    assert e["sun_dir"][2] < -0.7 and e["moon_dir"][2] > 0.7
    assert "night_sky" in e and e["night_sky"]["stars"] > 0.99 and e["stars"] > 0.99
    assert e["moon"] == 0.5


def test_altitude_track_choice_ground_unless_above_threshold():
    # ground sky vs a darker high-altitude sky on track 2 (floor 1000 m)
    high = {**NOON, "sunlight_color": [0.05, 0.05, 0.05], "ambient": [0.02, 0.02, 0.02]}
    extra = [[{"key_keyframe": 0.0, "key_name": "h"}], [], []]  # tracks 2, 3, 4
    low = far_env({"n": NOON, "h": high}, [(0.0, "n")], extra_sky_tracks=extra, me_z=10.0,
                  track_altitudes=[0.0, 1000.0, 2000.0, 3000.0], region_time_s=0)
    high_d = far_env({"n": NOON, "h": high}, [(0.0, "n")], extra_sky_tracks=extra, me_z=1500.0,
                     track_altitudes=[0.0, 1000.0, 2000.0, 3000.0], region_time_s=0)
    assert llsky.sky_track(low, low["environment"]["environment"]["day_cycle"]) == 1
    assert llsky.sky_track(high_d, high_d["environment"]["environment"]["day_cycle"]) == 2
    assert llsky.env_at(low)["sunlight"][0] > 0.5
    assert llsky.env_at(high_d)["sunlight"][0] < 0.1
    # an explicit camera altitude wins over the agent z
    assert llsky.sky_track(low, low["environment"]["environment"]["day_cycle"], alt=1500.0) == 2


def test_photo_env_error_string_falls_back_to_default_sky():
    """A failed env fetch (`error: ...`) is not a usable day cycle: HD uses the viewer Default sky."""
    d = {"environment": "error: no environment", "exported_at_utc": "2026-11-23T18:00:00Z", "sun_dir": [0.9, 0, 0.44]}
    e = llsky.photo_env(d)
    assert e is not None and e["source"].startswith("default-fallback:")
    assert e["sun_dir"][2] > 0.9
    # a missing environment block is the same fallback
    assert llsky.photo_env({})["source"].startswith("default-fallback:")
    assert llsky.env_at({"environment": "error: no environment"}) is None

