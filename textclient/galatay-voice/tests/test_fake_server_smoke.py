"""PR #47: fake_voice_server module loads; full --drive (whisper) skipped in CI."""
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


@pytest.mark.skip(reason="Downloads/runs faster-whisper; use fake_voice_server.py --drive locally")
def test_pr47_fake_drive_skipped():
    assert False
