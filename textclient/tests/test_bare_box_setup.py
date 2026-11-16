"""2026-11-15: bare-box-setup.sh downloads only checksum-pinned artifacts (no unverified dotnet-install.sh),
pins the current .NET SDK, never installs EOL .NET 8, and the venv pins a patched Pillow."""
import os, re, subprocess

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SETUP = os.path.join(ROOT, "scripts", "bare-box-setup.sh")


def _src():
    return open(SETUP).read()


def test_setup_script_parses():
    subprocess.run(["bash", "-n", SETUP], check=True)


def test_dotnet_sdk_is_a_sha512_pinned_tarball():
    s = _src()
    m = re.search(r'^SDK_VERSION="(10\.0\.\d+)"$', s, re.M)
    assert m, "SDK_VERSION must pin a 10.0.x SDK"
    assert re.search(r'^SDK_SHA512="[0-9a-f]{128}"$', s, re.M)
    assert 'SDK_URL="https://builds.dotnet.microsoft.com/dotnet/Sdk/${SDK_VERSION}/${SDK_TAR}"' in s
    assert 'sha512sum "$sdk_tmp"' in s and "exit 4" in s


def test_no_unverified_installer_and_no_net8():
    s = _src()
    assert "dot.net/v1/dotnet-install.sh" not in s
    assert "dotnet-install.sh" not in s.split("# ---------- 1. .NET ----------", 1)[1]
    assert not re.search(r"--channel 8|SDK_VERSION=\"8\.|8\.0\.4\d\d", s)


def test_downloads_are_sha256_pinned():
    s = _src()
    for var in ("FS_SHA256", "BLENDER_SHA256"):
        assert re.search(rf'^{var}="[0-9a-f]{{64}}"$', s, re.M), var
    assert s.count('fetch_and_check "$') == 2


def test_blender_pin_matches_vision_default():
    s = _src()
    ver = re.search(r'^BLENDER_VER="([\d.]+)"$', s, re.M).group(1)
    look = open(os.path.join(ROOT, "vision", "look.py")).read()
    assert f"/home/box/tools/blender-{ver}-linux-x64/blender" in look


def test_venv_pins_patched_pillow():
    req = open(os.path.join(ROOT, "scripts", "requirements.txt")).read()
    m = re.search(r"^pillow==(\d+)\.(\d+)\.(\d+)$", req, re.M)
    assert m and int(m.group(1)) >= 13
