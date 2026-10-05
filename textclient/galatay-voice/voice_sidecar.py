#!/usr/bin/env python3
# voice_sidecar.py (2026-10-05): LISTEN-ONLY Second Life WebRTC voice + local transcription for Galatay's text client.
# The text client (galatay-text/Voice.cs) spawns this process and brokers the signalling over the region caps
# (ProvisionVoiceAccountRequest / VoiceSignalingRequest); this process does the WebRTC side (aiortc), Opus decode,
# speech segmenting and speech-to-text (faster-whisper, CPU). It is a separate process so a native crash in WebRTC or
# the STT runtime can never take the SL session down, and it drains its transcription backlog even after the client exits.
#
# stdin  (client -> sidecar, JSON lines): {"t":"answer","sdp"} | {"t":"pos","sp":[x,y,z],"sh":[x,y,z,w],"lp":[..],"lh":[..]}
#        (global coords / quaternion, already x100 ints) | {"t":"name","id","name"} | {"t":"meta","region","parcel","channel","self"}
#        | {"t":"stop"}            EOF on stdin = stop.
# stdout (sidecar -> client, JSON lines): {"t":"offer","sdp"} | {"t":"state",...} | {"t":"peer","id"} | {"t":"line",...}
#        | {"t":"error","msg"} | {"t":"bye"}
# stderr: short human log lines (the client copies them into textclient.log as [voice-sc]).
#
# NEVER TRANSMITS AUDIO: the audio m-line is negotiated (sendrecv by default, like the LL viewer, or recvonly) but no
# track is ever attached, and aiortc sends no RTP without a track. There is no code path that attaches one.
# ponytail: speaking later = attach a TTS MediaStreamTrack to `audio_tr.sender` behind an explicit client flag.
import argparse, asyncio, collections, datetime as dt, json, os, re, sys, threading, time, queue, wave

import numpy as np

UUID_RX = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")
SR = 16000            # STT sample rate
FRAME = 320           # 20 ms at 16 kHz
FRAME_S = 0.02
_out_lock = threading.Lock()


def emit(obj):
    with _out_lock:
        try:
            sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n"); sys.stdout.flush()
        except (BrokenPipeError, ValueError):
            pass  # client gone: keep draining the transcription backlog anyway


def log(msg):
    try:
        sys.stderr.write(f"{dt.datetime.now():%H:%M:%S} {msg}\n"); sys.stderr.flush()
    except (BrokenPipeError, ValueError):
        pass


def munge_offer(sdp: str) -> str:
    """Opus as the LL viewer offers it: opus/48000/2 + its fmtp line (stereo, in-band FEC, 48 kHz)."""
    out, pt = [], None
    for line in sdp.splitlines():
        m = re.match(r"a=rtpmap:(\d+) opus/48000(/2)?$", line)
        if m:
            pt = m.group(1)
            out.append(f"a=rtpmap:{pt} opus/48000/2")
            out.append(f"a=fmtp:{pt} minptime=10;useinbandfec=1;stereo=1;sprop-stereo=1;maxplaybackrate=48000;"
                       "sprop-maxplaybackrate=48000;sprop-maxcapturerate=48000")
            continue
        if pt and line.startswith(f"a=fmtp:{pt} "):
            continue
        out.append(line)
    return "\r\n".join(out) + "\r\n"


def sanitize_answer(sdp: str) -> str:
    """Drop remote candidates with port 0 (seen in SL answers; LibreMetaverse drops them too)."""
    keep = []
    for line in sdp.splitlines():
        if line.startswith("a=candidate:"):
            parts = line.split()
            if len(parts) > 5 and parts[5] == "0":
                continue
        keep.append(line)
    return "\r\n".join(keep) + "\r\n"


class Peers:
    """Participant state from the SLData channel: {"<agent uuid>": {"j":{"p":bool}, "l":bool, "p":0-128, "v":bool, "m":bool}}."""
    def __init__(self):
        self.lock = threading.Lock()
        self.p = {}            # id -> dict(power, speaking, joined, left, last)
        self.self_id = None

    def handle(self, msg: str):
        try:
            data = json.loads(msg)
        except ValueError:
            return []
        if not isinstance(data, dict):
            return []
        new = []
        with self.lock:
            for k, v in data.items():
                if not UUID_RX.match(k) or not isinstance(v, dict):
                    continue
                k = k.lower()
                st = self.p.get(k)
                if st is None:
                    st = self.p[k] = {"power": 0, "speaking": False, "joined": False, "left": False, "last": 0.0}
                    new.append(k)
                st["last"] = time.time()
                if isinstance(v.get("j"), dict):
                    st["joined"], st["left"] = True, False
                if v.get("l") is True:
                    st["left"], st["speaking"] = True, False
                if isinstance(v.get("p"), (int, float)):
                    st["power"] = int(v["p"])
                if isinstance(v.get("v"), bool):
                    st["speaking"] = v["v"]
        return new

    def speaking(self):
        with self.lock:
            return {k for k, s in self.p.items() if s["speaking"] and not s["left"] and k != self.self_id}

    def snapshot(self, names):
        with self.lock:
            return [{"id": k, "name": names.get(k), "speaking": s["speaking"], "power": s["power"], "left": s["left"]}
                    for k, s in self.p.items() if k != self.self_id]


class Segmenter:
    """Cuts the received (mixed) audio into utterances: starts on energy or a server 'speaking' flag, ends after
    hang_s of quiet (or no packets at all), or at max_s. Votes which participant was flagged speaking per 20 ms frame."""
    def __init__(self, on_segment, max_s=28.0, hang_s=0.8, pre_s=0.3, min_active_s=0.5, thresh=120.0):
        self.on_segment, self.max_f, self.hang_f = on_segment, int(max_s / FRAME_S), int(hang_s / FRAME_S)
        self.min_active_f, self.thresh = int(min_active_s / FRAME_S), thresh
        self.pre = collections.deque(maxlen=int(pre_s / FRAME_S))
        self.carry = np.zeros(0, dtype=np.int16)
        self.lock = threading.Lock()
        self._reset()
        self.last_rms = 0.0
        self.last_frame_t = 0.0

    def _reset(self):
        self.buf, self.active_f, self.silent_f, self.votes, self.t0 = None, 0, 0, collections.Counter(), None

    def feed(self, pcm: np.ndarray, now: float, speakers: set):
        with self.lock:
            self.last_frame_t = now
            pcm = np.concatenate([self.carry, pcm]) if self.carry.size else pcm
            n = (pcm.size // FRAME) * FRAME
            self.carry = pcm[n:].copy()
            for i in range(0, n, FRAME):
                self._frame(pcm[i:i + FRAME], now, speakers)

    def _frame(self, fr, now, speakers):
        rms = float(np.sqrt(np.mean(fr.astype(np.float32) ** 2)))
        self.last_rms = rms
        active = rms >= self.thresh or bool(speakers)
        if self.buf is None:
            if active:
                self.t0 = now - len(self.pre) * FRAME_S
                self.buf = list(self.pre) + [fr]
                self.pre.clear()
                self.active_f, self.silent_f = 1, 0
                self.votes.update(speakers)
            else:
                self.pre.append(fr)
            return
        self.buf.append(fr)
        if active:
            self.active_f += 1; self.silent_f = 0; self.votes.update(speakers)
        else:
            self.silent_f += 1
        if self.silent_f >= self.hang_f or len(self.buf) >= self.max_f:
            self._close()

    def _close(self):
        if self.buf is not None and self.active_f >= self.min_active_f:
            self.on_segment(np.concatenate(self.buf), self.t0, self.votes)
        self._reset()

    def tick(self, now):
        """Packets can simply stop during silence (DTX / nobody talking): close an open segment after hang_s without frames."""
        with self.lock:
            if self.buf is not None and now - self.last_frame_t >= self.hang_f * FRAME_S:
                self._close()

    def flush(self):
        with self.lock:
            self._close()


class Transcriber(threading.Thread):
    def __init__(self, args, names, meta):
        super().__init__(daemon=True)
        self.args, self.names, self.meta = args, names, meta
        self.q = queue.Queue()
        self.model, self.prev, self.lines, self.header_done = None, "", 0, False
        self.busy = False

    def run(self):
        while True:
            job = self.q.get()
            if job is None:
                self.model = None  # free CTranslate2 in this thread, not in interpreter teardown
                return
            self.busy = True
            try:
                self._do(*job)
            except Exception as e:  # never lose the loop over one bad chunk
                log(f"transcribe error: {type(e).__name__}: {e}")
            finally:
                self.busy = False

    def backlog(self):
        return self.q.qsize() + (1 if self.busy else 0)

    def _load(self):
        from faster_whisper import WhisperModel
        t = time.time()
        self.model = WhisperModel(self.args.model, device="cpu", compute_type="int8", cpu_threads=self.args.threads,
                                  download_root=self.args.models)
        log(f"model {self.args.model} loaded in {time.time() - t:.1f}s ({self.args.threads} threads)")

    def _do(self, pcm, t0, votes):
        if self.model is None:
            self._load()
        audio = pcm.astype(np.float32) / 32768.0
        t = time.time()
        segs, _info = self.model.transcribe(audio, language=self.args.lang, beam_size=self.args.beam, vad_filter=True,
                                            condition_on_previous_text=False,
                                            initial_prompt=(self.prev[-200:] or None))
        parts = [s.text.strip() for s in segs if not (s.no_speech_prob > 0.6 and s.avg_logprob < -1.0)]
        text = " ".join(p for p in parts if p).strip()
        took = time.time() - t
        if not text:
            return
        self.prev = text
        speaker, speaker_id = self._speaker(votes)
        when = dt.datetime.fromtimestamp(t0)
        self._write(when, speaker, text)
        self.lines += 1
        line = {"t": "line", "time": when.strftime("%H:%M:%S"), "speaker": speaker, "text": text,
                "audio_s": round(len(pcm) / SR, 1), "stt_s": round(took, 1)}
        if speaker_id:
            line["speaker_id"] = speaker_id
        emit(line)

    def _speaker(self, votes):
        """Return (display name, primary agent UUID or "")."""
        if not votes:
            return "(unattributed)", ""
        top = votes.most_common(2)
        def nm(i): return self.names.get(i) or ("speaker " + i[:8])
        primary = top[0][0]
        if len(top) > 1 and top[1][1] >= 0.4 * top[0][1]:
            return f"{nm(primary)} / {nm(top[1][0])}", primary
        return nm(primary), primary

    def _write(self, when, speaker, text):
        path = os.path.join(self.args.out, f"transcript-{when:%Y-%m-%d}.md")
        new_file = not os.path.exists(path)
        tz = time.strftime("%Z", time.localtime(when.timestamp()))
        with open(path, "a", encoding="utf-8") as f:
            if new_file:
                f.write(f"# Voice transcript {when:%Y-%m-%d} (listen-only, stored locally for Galatea; times {tz})\n")
            if not self.header_done:
                m = self.meta
                f.write(f"\n## {when:%H:%M} {tz} · region {m.get('region') or '?'} · {m.get('channel') or 'voice'}"
                        f"{(' · parcel ' + repr(m['parcel'])) if m.get('parcel') else ''}\n\n")
                self.header_done = True
            f.write(f"- {when:%H:%M:%S} **{speaker}:** {text}\n")
            f.flush(); os.fsync(f.fileno())


def write_wav(path, pcm):
    with wave.open(path, "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(SR); w.writeframes(pcm.tobytes())


def prune_chunks(root, keep_min):
    cutoff = time.time() - keep_min * 60
    for dp, _dn, fn in os.walk(root):
        for f in fn:
            p = os.path.join(dp, f)
            try:
                if f.endswith(".wav") and os.path.getmtime(p) < cutoff:
                    os.remove(p)
            except OSError:
                pass


async def run(args):
    from aiortc import RTCPeerConnection, RTCSessionDescription, RTCConfiguration, RTCIceServer, RTCRtpReceiver
    from aiortc.mediastreams import MediaStreamError
    import av

    loop = asyncio.get_running_loop()
    inq: asyncio.Queue = asyncio.Queue()
    names, meta = {}, {}
    peers = Peers()
    st = {"started": time.time(), "pc": "new", "ice": "new", "dc": "closed", "frames": 0, "segments": 0,
          "answer": False, "last_audio": 0.0, "error": None}
    tr = Transcriber(args, names, meta); tr.start()
    chunk_dir = os.path.join(args.out, "chunks")
    os.makedirs(chunk_dir, exist_ok=True)

    def on_segment(pcm, t0, votes):
        st["segments"] += 1
        if args.keep_audio_min > 0:
            d = os.path.join(chunk_dir, dt.datetime.fromtimestamp(t0).strftime("%Y-%m-%d"))
            os.makedirs(d, exist_ok=True)
            try:
                write_wav(os.path.join(d, dt.datetime.fromtimestamp(t0).strftime("%H%M%S_%f")[:-3] + ".wav"), pcm)
            except OSError as e:
                log(f"wav write failed: {e}")
        if tr.backlog() > args.max_backlog:  # ponytail: drop the oldest when CPU can't keep up; ceiling = gaps in a long talk
            try:
                tr.q.get_nowait(); log("backlog full: dropped the oldest untranscribed chunk")
            except queue.Empty:
                pass
        tr.q.put((pcm, t0, collections.Counter(votes)))

    seg = Segmenter(on_segment, thresh=args.thresh)

    def stdin_reader():
        for line in sys.stdin:
            line = line.strip()
            if line:
                loop.call_soon_threadsafe(inq.put_nowait, line)
        loop.call_soon_threadsafe(inq.put_nowait, '{"t":"stop","why":"stdin closed"}')
    threading.Thread(target=stdin_reader, daemon=True).start()

    pc = RTCPeerConnection(RTCConfiguration(iceServers=[RTCIceServer(urls=u) for u in args.stun]))
    audio_tr = pc.addTransceiver("audio", direction=args.direction)
    opus = [c for c in RTCRtpReceiver.getCapabilities("audio").codecs if c.mimeType.lower() == "audio/opus"]
    audio_tr.setCodecPreferences(opus)
    dc = pc.createDataChannel("SLData", ordered=True)
    done = asyncio.Event()
    last_pos = {"json": None, "t": 0.0}

    def push_state(**kw):
        st.update(kw)
        emit({"t": "state", "pc": st["pc"], "ice": st["ice"], "dc": st["dc"], **kw})

    def on_dc_message(m):
        if isinstance(m, bytes):
            m = m.decode("utf-8", "replace")
        if m.startswith('{"ping"'):
            try: dc.send('{"pong":true}')
            except Exception: pass
        for pid in peers.handle(m):
            if pid != peers.self_id:
                emit({"t": "peer", "id": pid})

    @dc.on("open")
    def _open():
        dc.send('{"j":{"p":true}}')   # join as the primary (own-region) connection
        push_state(dc="open")
        if last_pos["json"]:
            dc.send(last_pos["json"]); last_pos["t"] = time.time()

    @dc.on("close")
    def _close():
        push_state(dc="closed")

    dc.on("message", on_dc_message)

    @pc.on("datachannel")
    def _srv_dc(ch):
        ch.on("message", on_dc_message)

    @pc.on("connectionstatechange")
    async def _pcs():
        push_state(pc=pc.connectionState)
        if pc.connectionState in ("failed", "closed"):
            done.set()

    @pc.on("iceconnectionstatechange")
    async def _ices():
        push_state(ice=pc.iceConnectionState)

    async def consume(track):
        res = av.AudioResampler(format="s16", layout="mono", rate=SR)
        while True:
            try:
                frame = await track.recv()
            except MediaStreamError:
                return
            now = time.time()
            st["frames"] += 1; st["last_audio"] = now
            spk = peers.speaking()
            for f in res.resample(frame):
                seg.feed(f.to_ndarray().reshape(-1).astype(np.int16), now, spk)

    @pc.on("track")
    def _track(track):
        if track.kind == "audio":
            log("remote audio track received")
            asyncio.ensure_future(consume(track))

    def write_status():
        now = time.time()
        s = {"pid": os.getpid(), "updated": now, "state": st, "direction": args.direction, "model": args.model,
             "peers": peers.snapshot(names), "speaking_now": sorted(names.get(i, i) for i in peers.speaking()),
             "backlog": tr.backlog(), "lines": tr.lines, "meta": meta,
             "rms_dbfs": round(20 * np.log10(max(seg.last_rms, 1.0) / 32768.0), 1),
             "audio_age_s": round(now - st["last_audio"], 1) if st["last_audio"] else None}
        tmp = os.path.join(args.out, ".status.json.tmp")
        with open(tmp, "w") as f:
            json.dump(s, f)
        os.replace(tmp, os.path.join(args.out, "status.json"))

    async def ticker():
        n = 0
        while not done.is_set():
            await asyncio.sleep(0.25)
            seg.tick(time.time())
            n += 1
            if n % 8 == 0:
                try: write_status()
                except OSError as e: log(f"status write failed: {e}")
            if n % 240 == 0 and args.keep_audio_min > 0:
                prune_chunks(chunk_dir, args.keep_audio_min)
    tick_task = asyncio.ensure_future(ticker())

    await pc.setLocalDescription(await pc.createOffer())   # aiortc gathers all candidates here (no trickle)
    emit({"t": "offer", "sdp": munge_offer(pc.localDescription.sdp)})
    log(f"offer sent ({args.direction}, no local track)")

    async def dispatcher():
        while True:
            raw = await inq.get()
            try:
                m = json.loads(raw)
            except ValueError:
                continue
            t = m.get("t")
            if t == "answer" and not st["answer"]:
                try:
                    await pc.setRemoteDescription(RTCSessionDescription(sanitize_answer(m["sdp"]), "answer"))
                    push_state(answer=True)
                    log("answer applied; ICE checking")
                except Exception as e:
                    emit({"t": "error", "msg": f"bad answer: {e}"}); done.set(); return
            elif t == "pos":
                try:
                    j = json.dumps({k: dict(zip("xyzw", [int(x) for x in m[k]])) for k in ("sp", "sh", "lp", "lh")},
                                   separators=(",", ":"))
                except (KeyError, TypeError, ValueError):
                    continue
                if dc.readyState == "open" and (j != last_pos["json"] or time.time() - last_pos["t"] >= 5):
                    dc.send(j); last_pos["t"] = time.time()
                last_pos["json"] = j
            elif t == "name" and isinstance(m.get("id"), str) and UUID_RX.match(m["id"]):
                names[m["id"].lower()] = str(m.get("name") or "")[:64] or None
            elif t == "meta":
                for k in ("region", "parcel", "channel"):
                    if m.get(k) is not None: meta[k] = str(m[k])[:80]
                if isinstance(m.get("self"), str) and UUID_RX.match(m["self"]):
                    peers.self_id = m["self"].lower()
            elif t == "stop":
                log(f"stop ({m.get('why', 'client')})")
                done.set(); return
    disp = asyncio.ensure_future(dispatcher())

    try:
        await asyncio.wait_for(done.wait(), timeout=None)
    finally:
        disp.cancel(); tick_task.cancel()
        if st["pc"] == "failed":
            emit({"t": "error", "msg": "peer connection failed"})
        try:
            await asyncio.wait_for(pc.close(), 5)
        except Exception:
            pass
        seg.flush()
        try: write_status()
        except OSError: pass
        emit({"t": "bye", "backlog": tr.backlog()})
        # drain what was already heard (bounded), then exit; the client does not wait for this
        deadline = time.time() + args.drain_s
        while tr.backlog() > 0 and time.time() < deadline:
            await asyncio.sleep(0.5)
        st["pc"] = "closed"
        try: write_status()
        except OSError: pass
        tr.q.put(None)
        await asyncio.get_running_loop().run_in_executor(None, tr.join, 30)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--out", default="/workspace/secondlife/voice")
    ap.add_argument("--model", default="small.en")
    ap.add_argument("--models", default="/home/box/viewers/textclient/voice-models")
    ap.add_argument("--threads", type=int, default=2)
    ap.add_argument("--beam", type=int, default=1)
    ap.add_argument("--lang", default="en")
    ap.add_argument("--direction", choices=["sendrecv", "recvonly"], default="sendrecv")
    ap.add_argument("--stun", nargs="*", default=["stun:stun1.agni.secondlife.io:3478"])
    ap.add_argument("--thresh", type=float, default=120.0, help="speech gate RMS (int16); ~-49 dBFS")
    ap.add_argument("--keep-audio-min", type=int, default=30, help="rolling WAV chunk retention (0 = never write WAVs)")
    ap.add_argument("--max-backlog", type=int, default=60)
    ap.add_argument("--drain-s", type=int, default=600)
    ap.add_argument("--nice", type=int, default=10)
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    try:
        os.nice(args.nice)
    except OSError:
        pass
    try:
        asyncio.run(run(args))
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
