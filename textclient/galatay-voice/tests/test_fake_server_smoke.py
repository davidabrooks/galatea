"""PR #47: fake_voice_server module loads; the full --drive (whisper) run is in the opt-in slow tier."""
import importlib.util
import os

import pytest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
path = os.path.join(ROOT, "fake_voice_server.py")


def test_pr47_fake_voice_server_loads():
    spec = importlib.util.spec_from_file_location("fake_voice_server", path)
    mod = importlib.util.module_from_spec(spec)
    # Don't exec main; just ensure file exists and is parseable
    assert os.path.isfile(path)
    src = open(path).read()
    compile(src, path, "exec")
    assert "ProvisionVoiceAccountRequest" in src or "provision" in src


@pytest.mark.slow
def test_pr47_fake_drive_transcribes_the_speaker():
    """Slow tier (GALATEA_SLOW_TESTS=1): the full pure-Python voice path: fake server + voice_sidecar.py + faster-whisper
    (needs the voice venv, GT_VOICE_PY, and may download the speech model). Never runs in CI."""
    import subprocess
    py = os.environ.get("GT_VOICE_PY", "/home/box/viewers/textclient/voice-venv/bin/python")
    if not os.path.isfile(py):
        pytest.skip(f"voice venv python not found ({py}); set GT_VOICE_PY")
    r = subprocess.run([py, path, "--drive"], capture_output=True, text=True, timeout=400)
    assert r.returncode == 0 and "PASS" in r.stdout, (r.stdout[-1500:], r.stderr[-1500:])
