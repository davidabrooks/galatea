"""Voice latency: whisper model is warmed on a 'preload' job before any speech (no model, no aiortc)."""
import os, re, threading, queue

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = open(os.path.join(ROOT, "voice_sidecar.py")).read()


def _transcriber():
    m = re.search(r"^class Transcriber\(threading\.Thread\):\n(?:(?:    .*)?\n)+", SRC, re.M)
    assert m
    ns = {"threading": threading, "queue": queue, "log": lambda *_: None}
    exec(m.group(0), ns)
    return ns["Transcriber"]


def test_preload_job_loads_model_once_and_keeps_running():
    T = _transcriber()
    loads = []
    t = T(None, None, None)
    t._load = lambda: (loads.append(1), setattr(t, "model", object()))
    t.start()
    t.q.put("preload"); t.q.put("preload"); t.q.put(None)
    t.join(2)
    assert not t.is_alive() and loads == [1]


def test_main_queues_preload_unless_disabled():
    assert 'tr.q.put("preload")' in SRC and "--no-preload" in SRC


class _Pcm:
    def astype(self, _): return self
    def __truediv__(self, _): return self
    def __len__(self): return 16000


class _Model:
    def __init__(self, segs): self.segs = segs
    def transcribe(self, *a, **k): return iter(self.segs), None


def _run_do(text_segs, speech_s):
    T = _transcriber()
    ns = T.__init__.__globals__
    emitted = []
    ns.update(np=type("np", (), {"float32": None}), time=__import__("time"), dt=__import__("datetime"),
              SR=16000, emit=emitted.append, UNCLEAR_MIN_SPEECH_S=0.8)
    t = T(None, None, None)
    t.args = type("A", (), {"lang": "en", "beam": 1})()
    t.model = _Model(text_segs)
    t._write = lambda *a: None
    t._speaker = lambda v: ("David Nightingale", "44ce5a36-c1c7-4a68-ac9a-635ddfff6233")
    t._do(_Pcm(), 0.0, {}, speech_s)
    return emitted


def test_undecodable_real_speech_is_still_emitted():
    out = _run_do([], 1.4)
    assert len(out) == 1 and out[0]["text"] == "" and out[0]["speech_s"] == 1.4


def test_undecodable_short_blip_is_dropped():
    assert _run_do([], 0.4) == [] and _run_do([], None) == []


def test_segmenter_reports_active_speech_seconds():
    assert "round(self.active_f * FRAME_S, 2)" in SRC
