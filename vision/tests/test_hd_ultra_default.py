"""2026-10-14: David's Firestorm Ultra = photo hd default; SSR / mirrors / DOF / SMAA opt-outs; DOF only with an avatar subject."""
import math, os, sys, types
import pytest
HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE); sys.path.insert(0, os.path.join(HERE, "cloud-render"))
for _m in ("handler", "make_job"):
    sys.modules.setdefault(_m, types.ModuleType(_m))
import look  # noqa: E402
SRC = open(os.path.join(HERE, "cloud-render", "render_mesh.py")).read()
ns = {"math": math, "os": os}
exec(SRC[SRC.index("MIRRORS = "):SRC.index("def light_linear")], ns)


def test_ultra_is_default():
    _, e = look.hd_settings("", "self")
    assert e["GT_HD_PROFILE"] == "ultra" and look.DEFAULT_PROFILE == "ultra"
    assert e["GT_SSR"] == "1" and e["GT_MIRRORS"] == "1" and e["GT_DOF"] == "1" and e["GT_DOF_FNUMBER"] == "9.0"
    assert e["GT_TONEMAP_MIX"] == "0.0" and e["GT_SHARPEN"] == "0.4" and e["GT_LOD_FACTOR"] == "3" and e["GT_AVATAR_MAX"] == "16"
    assert look.hd_settings("", "self", "highplus")[1]["GT_SSR"] == "0"


def test_opt_outs():
    _, e = look.hd_settings("", "view", no=["ssr", "mirrors", "dof", "smaa"])
    assert (e["GT_SSR"], e["GT_MIRRORS"], e["GT_DOF"], e["GT_SMAA"]) == ("0", "0", "0", "0")
    assert look.lod_args(look.hd_settings("", "view", no=["impostors"])[1]) == ["--lod-factor=3", "--avatar-max=0"]
    with pytest.raises(ValueError): look.hd_settings("", "view", no=["bloom"])


def test_dof_only_with_avatar_subject():
    heads = {"me": 1, "david nightingale": 2}
    assert ns["dof_subject"]("portrait", heads) and ns["dof_subject"]("portrait:90", heads)
    assert ns["dof_subject"]("at:david nightingale", heads)
    assert not ns["dof_subject"]("at:ultra sofa", heads)        # object target: sharp
    assert not ns["dof_subject"]("eye", heads) and not ns["dof_subject"](None, heads)   # view / scenery: sharp


def test_dof_fstop_matches_firestorm_cof():
    fovy = math.radians(60)
    F = 0.018 / math.tan(fovy / 2)                                          # Blender lens (m), 36 mm sensor
    nb = ns["dof_fstop"](9.0, 50, 60, fovy, F * 1000)
    f = 0.05; S, d = 3.0, 10.0
    fs = 1.414 * f * abs(d - S) / (d * 9.0 * S * fovy)                       # Firestorm CoF / height
    bl = F * abs(d - S) / (d * nb * S * 2 * math.tan(fovy / 2))
    assert abs(bl / fs - 1) < 0.02
    assert ns["max_cof_ok"](nb, F * 1000, fovy, 0.3) > nb                   # close focus: clamped to CameraMaxCoF


def test_mirrors_off_floors_roughness():
    assert ns["roughness_floor"](0.0) == 0.0
    ns["MIRRORS"] = False
    assert ns["roughness_floor"](0.0) == ns["MIRROR_OFF_ROUGH"]
