"""PR #47: voice sidecar SDP munge — no STT model, no aiortc install required."""
import os
import re

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = open(os.path.join(ROOT, "voice_sidecar.py")).read()

def _load():
    uuid_line = re.search(r"^UUID_RX = .+$", SRC, re.M).group(0)
    m = re.search(
        r"^def munge_offer\(sdp: str\) -> str:\n(?:    .*\n)+",
        SRC,
        re.M,
    )
    assert m, "munge_offer not found"
    ns = {}
    exec("import re\n" + uuid_line + "\n" + m.group(0), ns)
    return ns

NS = _load()


def test_pr47_munge_offer_sets_opus_fmtp():
    sdp = "\n".join([
        "v=0",
        "m=audio 9 UDP/TLS/RTP/SAVPF 111",
        "a=rtpmap:111 opus/48000/2",
        "a=fmtp:111 minptime=10",
    ])
    out = NS["munge_offer"](sdp)
    assert "opus/48000/2" in out
    assert "useinbandfec=1" in out
    assert "111" in out


def test_pr47_uuid_rx():
    assert NS["UUID_RX"].match("4bd5592d-fc3d-41c1-a7a4-2dd9ca24f65a")
    assert not NS["UUID_RX"].match("not-a-uuid")
