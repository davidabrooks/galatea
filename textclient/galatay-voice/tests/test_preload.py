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
