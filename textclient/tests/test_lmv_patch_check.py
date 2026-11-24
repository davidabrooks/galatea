"""check-lmv-patches.sh: a staged/deployed LibreMetaverse.dll without the seed-retry fix must be refused
(deploying one crashed the connector 4 times right after login). Since LibreMetaverse v3.3.0 (2026-11-09) the fix is upstream's
Caps.ScheduleSeedRetry. Since 2026-11-23 the on-disk object cache lives in LibreMetaverse.Caching.dll (ObjectDiskCache),
which must be staged next to LibreMetaverse.dll. Pure file checks, no login."""
import pathlib, subprocess
import pytest

TC = pathlib.Path(__file__).resolve().parents[1]
SCRIPT = TC / "check-lmv-patches.sh"


def run(*dirs):
    return subprocess.run([str(SCRIPT), *map(str, dirs)], capture_output=True, text=True)


def write_good(path):
    (path / "LibreMetaverse.dll").write_bytes(b"MZ....#Strings\0ScheduleSeedRetry\0Caps\0")
    (path / "LibreMetaverse.Caching.dll").write_bytes(b"MZ....#Strings\0ObjectDiskCache\0CacheProvider\0")


def test_patched_dll_passes(tmp_path):
    write_good(tmp_path)
    r = run(tmp_path)
    assert r.returncode == 0 and "OK" in r.stdout


def test_unpatched_dll_is_refused(tmp_path):
    (tmp_path / "LibreMetaverse.dll").write_bytes(b"MZ....#Strings\0Seed capability returned\0Caps\0")
    r = run(tmp_path)
    assert r.returncode == 6 and "UNPATCHED" in r.stdout


def test_old_seed_retry_patch_marker_alone_is_refused(tmp_path):
    # the pre-3.3.0 build (v3.1.6 + our seed-retry patch) lacks upstream's fix and must not pass as current
    (tmp_path / "LibreMetaverse.dll").write_bytes(b"MZ....#Strings\0SeedMaxAttempts\0")
    (tmp_path / "LibreMetaverse.Caching.dll").write_bytes(b"MZ....#Strings\0ObjectDiskCache\0")
    r = run(tmp_path)
    assert r.returncode == 6 and "upstream-seed-retry-fix" in r.stdout


def test_seed_fix_without_caching_dll_is_refused(tmp_path):
    (tmp_path / "LibreMetaverse.dll").write_bytes(b"MZ....#Strings\0ScheduleSeedRetry\0Caps\0")
    r = run(tmp_path)
    assert r.returncode == 6 and "LibreMetaverse.Caching.dll" in r.stdout


def test_caching_dll_without_object_disk_cache_is_refused(tmp_path):
    (tmp_path / "LibreMetaverse.dll").write_bytes(b"MZ....#Strings\0ScheduleSeedRetry\0Caps\0")
    (tmp_path / "LibreMetaverse.Caching.dll").write_bytes(b"MZ....#Strings\0SomethingElse\0")
    r = run(tmp_path)
    assert r.returncode == 6 and "LibreMetaverse.Caching.ObjectDiskCache" in r.stdout


def test_missing_dll_is_refused(tmp_path):
    assert run(tmp_path).returncode == 6


def test_one_bad_directory_fails_the_set(tmp_path):
    good, bad = tmp_path / "good", tmp_path / "bad"; good.mkdir(); bad.mkdir()
    write_good(good)
    (bad / "LibreMetaverse.dll").write_bytes(b"nothing")
    assert run(good, bad).returncode == 6


def test_the_staged_build_when_present_is_patched():
    for d in ("app-staging", "mcp-app-staging"):
        if (TC / d / "LibreMetaverse.dll").exists():
            assert run(TC / d).returncode == 0, d


def test_build_all_runs_the_check():
    assert "check-lmv-patches.sh" in (TC / "build-all.sh").read_text()
