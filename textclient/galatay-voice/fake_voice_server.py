#!/usr/bin/env python3
# fake_voice_server.py (2026-10-05): offline stand-in for SL's WebRTC voice service, for the voice selftests.
# Serves the two caps on 127.0.0.1 the way the region does (LLSD XML over HTTP POST):
#   /provision  {jsep:{type:offer,sdp}, channel_type:local, voice_server_type:webrtc[, parcel_local_id]}
#               -> {jsep:{type:answer,sdp}, viewer_session}      and {logout:true, viewer_session, ...} -> {}
#   /signal     {viewer_session, voice_server_type, candidates:[..]} | {.., candidate:{completed:true}} -> {}
#   GET /report -> JSON of what was received (checked by the selftest)
# and answers as an aiortc peer that streams a speech clip (Opus) and, on the SLData channel, flags one participant
# (SPEAKER) as speaking while the clip plays, like the real server's {"<agent id>":{"p":..,"v":true}} messages.
# Modes:  --serve          print {"port":N,"speaker":"<uuid>"} on stdout, then serve until stdin closes (C# selftest)
#         --drive          also run voice_sidecar.py as the client would and check the transcript (pure-Python selftest)
import argparse, asyncio, json, os, subprocess, sys, tempfile, time, uuid
import xml.etree.ElementTree as ET

SPEAKER = "5ee1c0de-0000-4000-8000-00000000d0e5"
HERE = os.path.dirname(os.path.abspath(__file__))


# ---- minimal LLSD XML ------------------------------------------------------------------------------
def llsd_parse(data: bytes):
    def val(e):
        t = e.tag
        if t == "map":
            ch, out = list(e), {}
            for i in range(0, len(ch) - 1, 2):
                out[ch[i].text or ""] = val(ch[i + 1])
            return out
        if t == "array": return [val(c) for c in e]
        if t == "integer": return int(e.text or 0)
        if t == "real": return float(e.text or 0)
        if t == "boolean": return (e.text or "").strip().lower() in ("1", "true")
        if t == "undef": return None
        return e.text or ""
    root = ET.fromstring(data)
    return val(root[0]) if len(root) else None


def llsd_xml(o) -> bytes:
    def enc(v):
        if isinstance(v, dict):
            return "<map>" + "".join(f"<key>{k}</key>{enc(x)}" for k, x in v.items()) + "</map>"
        if isinstance(v, list): return "<array>" + "".join(enc(x) for x in v) + "</array>"
        if isinstance(v, bool): return f"<boolean>{'true' if v else 'false'}</boolean>"
        if isinstance(v, int): return f"<integer>{v}</integer>"
        if v is None: return "<undef />"
        s = str(v).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
        return f"<string>{s}</string>"
    return ('<?xml version="1.0" ?><llsd>' + enc(o) + "</llsd>").encode()


class FakeVoice:
    def __init__(self, audio):
        self.audio, self.pcs, self.report = audio, [], {"provision": [], "signal": [], "logout": [], "dc_in": []}

    async def answer(self, offer_sdp):
        from aiortc import RTCPeerConnection, RTCSessionDescription
        from aiortc.contrib.media import MediaPlayer
        pc = RTCPeerConnection(); self.pcs.append(pc)
        player = MediaPlayer(self.audio)

        @pc.on("datachannel")
        def on_dc(ch):
            @ch.on("message")
            def on_msg(m):
                self.report["dc_in"].append(m if isinstance(m, str) else m.decode())
                if isinstance(m, str) and m.startswith('{"j"'):
                    asyncio.ensure_future(self.speak(ch))

        await pc.setRemoteDescription(RTCSessionDescription(offer_sdp, "offer"))
        for t in pc.getTransceivers():
            if t.kind == "audio":
                t.sender.replaceTrack(player.audio)
                t.direction = "sendrecv" if t.direction != "recvonly" else "sendonly"
        await pc.setLocalDescription(await pc.createAnswer())
        return pc.localDescription.sdp

    async def speak(self, ch):
        ch.send(json.dumps({SPEAKER: {"j": {"p": True}, "p": 0, "v": False}}))
        t_end = time.time() + 11.5
        while time.time() < t_end:
            ch.send(json.dumps({SPEAKER: {"p": 90, "v": True}}))
            await asyncio.sleep(0.5)
        ch.send(json.dumps({SPEAKER: {"p": 0, "v": False}}))

    async def handle(self, reader, writer):
        try:
            head = await reader.readuntil(b"\r\n\r\n")
            lines = head.decode().split("\r\n")
            method, path, _ = lines[0].split(" ", 2)
            hdr = {l.split(":", 1)[0].lower(): l.split(":", 1)[1].strip() for l in lines[1:] if ":" in l}
            body = await reader.readexactly(int(hdr.get("content-length", "0")))
            status, out, ctype = 200, llsd_xml({}), "application/llsd+xml"
            if method == "GET" and path == "/report":
                out, ctype = json.dumps(self.report).encode(), "application/json"
            elif method == "POST" and path == "/provision":
                req = llsd_parse(body)
                if req.get("logout"):
                    self.report["logout"].append(req)
                else:
                    self.report["provision"].append({k: v for k, v in req.items() if k != "jsep"} |
                                                    {"jsep_type": req["jsep"]["type"], "sdp_len": len(req["jsep"]["sdp"]),
                                                     "opus_stereo": "stereo=1" in req["jsep"]["sdp"]})
                    ok = req.get("voice_server_type") == "webrtc" and req.get("channel_type") == "local"
                    if not ok:
                        status, out = 472, b"Invalid SDP offer"
                    else:
                        sdp = await self.answer(req["jsep"]["sdp"])
                        out = llsd_xml({"jsep": {"type": "answer", "sdp": sdp}, "viewer_session": str(uuid.uuid4())})
            elif method == "POST" and path == "/signal":
                self.report["signal"].append(llsd_parse(body))
            else:
                status, out = 404, b"not found"
            writer.write(f"HTTP/1.1 {status} X\r\nContent-Type: {ctype}\r\nContent-Length: {len(out)}\r\nConnection: close\r\n\r\n".encode() + out)
            await writer.drain()
        except Exception as e:
            sys.stderr.write(f"fake server error: {type(e).__name__}: {e}\n")
        finally:
            writer.close()


async def serve(args):
    fv = FakeVoice(args.audio)
    srv = await asyncio.start_server(fv.handle, "127.0.0.1", 0)
    port = srv.sockets[0].getsockname()[1]
    if args.drive:
        return await drive(args, fv, port)
    print(json.dumps({"port": port, "speaker": SPEAKER}), flush=True)
    loop = asyncio.get_running_loop()
    await loop.run_in_executor(None, sys.stdin.read)   # until the selftest closes our stdin
    for pc in fv.pcs:
        await pc.close()


async def drive(args, fv, port):
    """Pure-Python selftest: act as the text client towards the sidecar, using this fake server's caps."""
    import urllib.request
    out = tempfile.mkdtemp(prefix="voice-selftest-")
    p = await asyncio.create_subprocess_exec(sys.executable, os.path.join(HERE, "voice_sidecar.py"), "--out", out,
                                             "--model", args.model, "--keep-audio-min", "5", "--stun",
                                             stdin=subprocess.PIPE, stdout=subprocess.PIPE)
    base = f"http://127.0.0.1:{port}"

    def post(path, obj):
        r = urllib.request.urlopen(urllib.request.Request(base + path, data=llsd_xml(obj),
                                                          headers={"Content-Type": "application/llsd+xml"}), timeout=30)
        return llsd_parse(r.read())

    def send(o):
        p.stdin.write((json.dumps(o) + "\n").encode())
    send({"t": "meta", "region": "Selftest", "channel": "parcel voice", "self": str(uuid.uuid4())})
    loop, result, t_end = asyncio.get_running_loop(), None, time.time() + 150
    while time.time() < t_end:
        line = await asyncio.wait_for(p.stdout.readline(), timeout=max(1, t_end - time.time()))
        if not line:
            break
        m = json.loads(line)
        if m["t"] == "offer":
            ans = await loop.run_in_executor(None, post, "/provision", {"jsep": {"type": "offer", "sdp": m["sdp"]},
                                             "channel_type": "local", "voice_server_type": "webrtc"})
            send({"t": "answer", "sdp": ans["jsep"]["sdp"]})
            await loop.run_in_executor(None, post, "/signal", {"viewer_session": ans["viewer_session"],
                                       "voice_server_type": "webrtc", "candidate": {"completed": True}})
        elif m["t"] == "peer":
            send({"t": "name", "id": m["id"], "name": "Test Speaker"})
        elif m["t"] == "line":
            print("transcript line:", m["speaker"], "|", m["text"], flush=True)
            if "country" in m["text"].lower():
                result = m
                break
        elif m["t"] in ("state", "error"):
            print("sidecar:", m, flush=True)
    send({"t": "stop"}); await p.stdin.drain(); p.stdin.close()
    await asyncio.wait_for(p.wait(), 60)
    for pc in fv.pcs:
        await pc.close()
    tfile = [f for f in os.listdir(out) if f.startswith("transcript-")]
    ok = bool(result) and result["speaker"] == "Test Speaker" and tfile
    print(("PASS" if ok else "FAIL") + f": transcript line attributed to {result and result['speaker']!r}; files in {out}: {sorted(os.listdir(out))}")
    return 0 if ok else 1


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--serve", action="store_true")
    ap.add_argument("--drive", action="store_true")
    ap.add_argument("--model", default="small.en")
    ap.add_argument("--audio", default=os.path.join(HERE, "testdata", "jfk-ask-not.ogg"))
    args = ap.parse_args()
    sys.exit(asyncio.run(serve(args)) or 0)


if __name__ == "__main__":
    main()
