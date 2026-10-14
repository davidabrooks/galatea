"""2026-10-13 audit, last items: SSR/mirrors off, SSAO on the ambient, LOD factor, impostor limit, projectors."""
import os, sys, types
HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE); sys.path.insert(0, os.path.join(HERE, "cloud-render"))
for _m in ("handler", "make_job"):
    sys.modules.setdefault(_m, types.ModuleType(_m))
import look  # noqa: E402
SRC = open(os.path.join(HERE, "cloud-render", "render_mesh.py")).read()


def test_profiles_turn_ssr_off_ao_on_and_carry_lod():
    hp = look.hd_settings("", "view", "highplus")[1]; ul = look.hd_settings("", "view", "ultra")[1]
    assert hp["GT_SSR"] == "0" and hp["GT_SSAO"] == "1.0" and hp["GT_FULLBRIGHT"] == "1.0" and hp["GT_LOD_FACTOR"] == "2" and ul["GT_LOD_FACTOR"] == "3"
    assert look.lod_args(hp) == ["--lod-factor=2", "--avatar-max=16"] and look.lod_args({}) == []


def test_projector_textures_distinct():
    m = {"lights": [{"proj": {"tex": "b"}}, {"proj": {"tex": "a"}}, {"proj": {"tex": "a"}}, {"radius": 3}]}
    assert look.projector_textures(m) == ["a", "b"]


def test_renderer_wires_the_settings():
    assert 'GT_SSR' in SRC and 'Specular IOR Level"].default_value = 0.0' in SRC
    assert 'ShaderNodeAmbientOcclusion' in SRC and '"SPOT" if pj' in SRC and 'def projector(' in SRC


def test_png_is_optimised_losslessly(tmp_path):
    import numpy as np, pytest
    Image = pytest.importorskip("PIL.Image")
    a = (np.random.default_rng(1).random((32, 48, 4)) * 255).astype("uint8"); p = str(tmp_path / "x.png")
    Image.fromarray(a, "RGBA").save(p); look.optimize_png(p)
    b = np.asarray(Image.open(p)); assert b.shape == (32, 48, 3) and (b == a[..., :3]).all()
    cmd = look.oxipng_cmd("f.png", True)
    assert cmd is None or (cmd[-1] == "f.png" and "--strip" in cmd and "--zopfli" in cmd)
