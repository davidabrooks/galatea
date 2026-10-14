"""2026-10-13 (David): presets as Firestorm draws them (the library EEP assets its World > Environment menu loads) and
HD photos in HDR: an OpenEXR/.hdr master + Firestorm's tonemap/exposure pass for the PNG/JPG."""
import json, os, sys
import numpy as np

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(HERE, "cloud-render"))
import hdr_post, llsky  # noqa: E402

RM = open(os.path.join(HERE, "cloud-render", "render_mesh.py")).read()


def test_presets_are_the_firestorm_library_assets():
    p = json.load(open(os.path.join(HERE, "cloud-render", "sky_presets.json")))["presets"]
    assert p["midday"]["asset_id"] == "c46226b4-0e43-5a56-9708-d27ca1df3292"            # KNOWN_SKY_MIDDAY
    assert p["midday-legacy"]["asset_id"] == "6c83e853-e7f8-cad7-8ee6-5f31c453721c"     # KNOWN_SKY_LEGACY_MIDDAY
    assert p["sunset"]["asset_id"] == "084e26cd-a900-28e8-08d0-64a9de5c15e2"
    assert p["sunrise"]["asset_id"] == "01e41537-ff51-2f1f-8ef7-17e4df760bfb"
    assert p["midnight"]["asset_id"] == "8a01b97a-cb20-c1ea-ac63-f7ea84ad0090"


def test_firestorm_midday_is_a_pbr_sky_others_classic():
    m = llsky.preset_env("midday")
    assert m["classic"] is False and m["probe_ambiance"] == 1.0 and m["ambient"] == [0, 0, 0]
    assert 0.8 < m["sun_dir"][2] < 0.95   # not straight overhead, unlike the legacy midday
    for n in ("sunset", "sunrise", "midnight", "midday-legacy"):
        assert llsky.preset_env(n)["classic"] is True


def test_classic_sky_is_not_tonemapped():
    rgb = np.full((4, 4, 3), 0.5)
    out, info = hdr_post.firestorm_ldr(rgb, {"classic": True, "gamma": 1.0})
    assert info["tonemap_mix"] == 0 and info["exposure"] == 1.0
    assert np.allclose(out, hdr_post.linear_to_srgb(rgb))
    out, _ = hdr_post.firestorm_ldr(np.full((2, 2, 3), 3.0), {"classic": True})
    assert np.allclose(out, 1.0)   # clamped, not compressed


def test_pbr_sky_aces_mix_and_dynamic_exposure():
    env = {"classic": False, "probe_ambiance": 1.0, "gamma": 1.0}
    assert hdr_post.exposure_range(env) == (0.5, 2.0)
    bright = np.full((4, 4, 3), 2.0); dark = np.full((4, 4, 3), 0.01)
    assert hdr_post.dynamic_exposure(bright, env) == 0.5 and abs(hdr_post.dynamic_exposure(dark, env) - 2.0) < 0.01
    out, info = hdr_post.firestorm_ldr(bright, env)
    assert info["tonemap_mix"] == 0.7 and 0 < out.max() <= 1.0


def test_aces_matches_the_shader_constants():
    c = hdr_post.aces_hill(np.array([[0.18, 0.18, 0.18]]))
    assert 0.1 < c[0, 0] < 0.3 and np.allclose(c[0, 0], c[0, 2], atol=0.01)


def test_rgbe_roundtrip(tmp_path):
    rgb = np.array([[[1.0, 0.5, 0.25], [8.0, 0.0, 0.0]]])
    p = tmp_path / "x.hdr"; hdr_post.write_rgbe(str(p), rgb)
    d = p.read_bytes(); body = d[d.index(b"+X 2\n") + 5:]
    px = np.frombuffer(body, np.uint8).reshape(1, 2, 4).astype(float)
    dec = px[..., :3] * (2.0 ** (px[..., 3:] - 136))
    assert np.allclose(dec, rgb, rtol=0.01)


def test_renderer_writes_hdr_masters():
    for s in ('im.file_format = "OPEN_EXR"', 'im.color_depth = "32"', "hdr_post.firestorm_ldr(", "hdr_post.write_rgbe(", 'env.get("classic") is False'):
        assert s in RM, s
