"""check-lmv-patches.sh (2026-10-28): a staged/deployed LibreMetaverse.dll without the seed-retry patch must be refused
(deploying one crashed the connector 4 times right after login). Pure file checks, no login."""
import pathlib, subprocess, shutil
import pytest

TC = pathlib.Path(__file__).resolve().parents[1]
SCRIPT = TC / "check-lmv-patches.sh"


def run(*dirs):
    return subprocess.run([str(SCRIPT), *map(str, dirs)], capture_output=True, text=True)


def test_patched_dll_passes(tmp_path):
    (tmp_path / "LibreMetaverse.dll").write_bytes(b"MZ....#Strings\0SeedMaxAttempts\0Caps\0")
    r = run(tmp_path)
    assert r.returncode == 0 and "patched" in r.stdout


def test_unpatched_dll_is_refused(tmp_path):
    (tmp_path / "LibreMetaverse.dll").write_bytes(b"MZ....#Strings\0Seed capability returned\0Caps\0")
    r = run(tmp_path)
    assert r.returncode == 6 and "UNPATCHED" in r.stdout


def test_missing_dll_is_refused(tmp_path):
    assert run(tmp_path).returncode == 6


def test_one_bad_directory_fails_the_set(tmp_path):
    good, bad = tmp_path / "good", tmp_path / "bad"; good.mkdir(); bad.mkdir()
    (good / "LibreMetaverse.dll").write_bytes(b"SeedMaxAttempts"); (bad / "LibreMetaverse.dll").write_bytes(b"nothing")
    assert run(good, bad).returncode == 6


def test_the_staged_build_when_present_is_patched():
    for d in ("app-staging", "mcp-app-staging"):
        if (TC / d / "LibreMetaverse.dll").exists():
            assert run(TC / d).returncode == 0, d


def test_build_all_runs_the_check():
    assert "check-lmv-patches.sh" in (TC / "build-all.sh").read_text()
