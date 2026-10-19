"""2026-10-19 Warehouse 21 too bright + dark dots in her hair.
- EEP sky track by altitude (LLEnvironment::calculateSkyTrackForAltitude): the club floats at 2003 m, its parcel day cycle has a dark night
  sky on tracks 2-4 and a bright default on the ground track; we always used the ground track.
- indoor ambient: the sky ambient (emission on every face) is scaled by open-sky AO, down to GT_INDOOR_AMB under a roof.
- batch holdouts: blended alpha cut to a mask so a holdout is never a dithered partial holdout (dark dots over her hair)."""
import os, sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(HERE, "cloud-render"))
import llsky  # noqa: E402
SRC = open(os.path.join(HERE, "cloud-render", "render_mesh.py")).read()


def _doc(z, tracks=None):
    dc = {"tracks": tracks or [[{"key_name": "w"}], [{"key_name": "g"}], [{"key_name": "s"}], [{"key_name": "s"}], [{"key_name": "s"}]]}
    return {"me": {"pos": [10, 10, z]}, "environment": {"environment": {"day_cycle": dc}}}, dc


def test_default_is_ground_without_region_altitudes():
    d, dc = _doc(2003); assert llsky.sky_track(d, dc) == 1
    d["environment"]["track_altitudes"] = [0, 1000, 2000, 3000]; assert llsky.sky_track(d, dc) == 3


def test_track_by_altitude():
    os.environ["GT_SKY_TRACK"] = "auto"
    try: _track_by_altitude()
    finally: del os.environ["GT_SKY_TRACK"]


def _track_by_altitude():
    for z, t in ((20, 1), (999, 1), (1500, 2), (2003, 3), (2500, 3), (3500, 4)):
        d, dc = _doc(z); assert llsky.sky_track(d, dc) == t, (z, t)


def test_empty_track_falls_back_and_override(monkeypatch=None):
    os.environ["GT_SKY_TRACK"] = "auto"
    try:
        d, dc = _doc(2003, [[{"key_name": "w"}], [{"key_name": "g"}], [], [], []]); assert llsky.sky_track(d, dc) == 1
        d, dc = _doc(2003, [[{"key_name": "w"}], [{"key_name": "g"}], [{"key_name": "s"}], [], []]); assert llsky.sky_track(d, dc) == 2
    finally: del os.environ["GT_SKY_TRACK"]
    d, dc = _doc(2003)
    os.environ["GT_SKY_TRACK"] = "ground"
    try: assert llsky.sky_track(d, dc) == 1
    finally: del os.environ["GT_SKY_TRACK"]
    os.environ["GT_SKY_TRACK"] = "4"
    try: assert llsky.sky_track(d, dc) == 4
    finally: del os.environ["GT_SKY_TRACK"]


def test_env_at_uses_the_altitude_track():
    assert 'dc["tracks"][sky_track(doc, dc)]' in open(os.path.join(HERE, "cloud-render", "llsky.py")).read()


def test_indoor_ambient_and_holdout_cut_wired():
    assert 'INDOOR_AMB = float(os.environ.get("GT_INDOOR_AMB", "0.25"))' in SRC and 'ia.inputs["Distance"].default_value = INDOOR_DIST' in SRC
    assert 'kk.inputs[3].default_value = INDOOR_AMB' in SRC and 'if HD and INDOOR_AMB < 1:' in SRC
    assert 'HOLD_ALPHA_CUT = float(os.environ.get("GT_HOLD_ALPHA_CUT", "0.5"))' in SRC
    assert 'def material(b, imgs=None, hold=False):' in SRC and 'me.materials.append(material(b, imgs, hold))' in SRC
    assert 'mat = {**mat, "alpha": "mask", "cutoff": HOLD_ALPHA_CUT}' in SRC


def test_indoor_haze_dims_fog_colour_under_a_roof():
    assert 'INDOOR_HAZE = os.environ.get("GT_INDOOR_HAZE", "")' in SRC and "def indoor_haze():" in SRC
    assert "s *= indoor_haze()" in SRC   # before the batch branch, so layer haze colours match the base
    i = SRC.index("s *= indoor_haze()"); assert i < SRC.index('if LAYER == "batch":', i)
