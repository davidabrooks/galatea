# galatay-voice: listen-only Second Life voice + local transcript

Galatay's text client (`textclient/galatay-text/Voice.cs`) joins the WebRTC voice channel where she stands (the
parcel or the region/estate channel), **never sends audio**, and writes a timestamped transcript with speaker names to
`/workspace/secondlife/voice/transcript-<date>.md`. The transcript is for Galatea's own understanding and stays on the box.

```
voice on | voice off | voice status | voice tail [n]        (socket command; MCP tool "voice")
galatay-text --voice-selftest                               (offline: real broker + real sidecar vs fake_voice_server.py)
# testdata/jfk-ask-not.ogg: 11 s of JFK's 1961 inaugural address (US government work, public domain), as in faster-whisper's tests
voice-venv/bin/python fake_voice_server.py --drive          (offline: sidecar only)
textclient/galatay-voice/install.sh                         (venv + scripts into /home/box/viewers/textclient/voice)
```

Voice is off by default and after every login. Galatea only joins voice when David asks (design doc §4.5).

## Split

| Part | Does |
|---|---|
| `Voice.cs` (in the client) | channel choice from region/parcel flags, the two caps POSTs, relays our position every 1 s, maps participant ids to names, reconnects on failure, region change or parcel-channel change, `voice` command |
| `voice_sidecar.py` (own process, nice 10) | aiortc WebRTC (ICE, DTLS-SRTP, SCTP data channel), Opus decode, speech segmenting, faster-whisper STT (CPU, int8, 2 threads, `small.en`), rolling WAV chunks (30 min), `status.json`, transcript file |

The sidecar is a separate process so a native crash in WebRTC or the STT runtime can't take the SL session down. It
also finishes transcribing its backlog (up to 10 min) after `voice off` or a client exit. The vendored
`LibreMetaverse.Voice.WebRTC` wasn't used because it needs real capture and playback devices (SoundFlow/SDL natives).
A headless box doesn't have those, and that library would run in-process.

## Protocol (SL WebRTC voice, as of Oct 2026)

Sources: the LL viewer (`secondlife/viewer` develop: `indra/newview/llvoicewebrtc.cpp`, `indra/llwebrtc/llwebrtc.cpp`;
LGPL, read only, reimplemented here) and LibreMetaverse's `LibreMetaverse.Voice.WebRTC` (BSD, vendored in
`/home/box/viewers/textclient/src-libremetaverse`). Vivox is gone grid-wide since May 5, 2026.

1. **Channel choice.** The region needs `RegionFlags.AllowVoice` and the parcel needs `ParcelFlags.AllowVoiceChat`, or
   there's no voice (the server answers 472). If the parcel has `UseEstateVoiceChan`, use the region/estate channel and
   send no `parcel_local_id`. Otherwise it's a parcel channel and `parcel_local_id = parcel.LocalID`. Moving to a parcel
   with a different channel means reconnecting.
2. **Offer.** A normal WebRTC offer: one audio m-line (Opus) and a data channel named **`SLData`** (ordered). The LL
   viewer rewrites Opus to `a=rtpmap:<pt> opus/48000/2` and
   `a=fmtp:<pt> minptime=10;useinbandfec=1;stereo=1;sprop-stereo=1;maxplaybackrate=48000;sprop-maxplaybackrate=48000;sprop-maxcapturerate=48000`.
   The LL viewer offers sendrecv with its mic track disabled. We offer sendrecv with **no track at all**, so no RTP is
   ever sent (`GT_VOICE_DIRECTION=recvonly` is the alternative). ICE servers: `stun:stun{1,2,3}.agni.secondlife.io:3478`
   (Aditi has 2). There's no TURN.
3. **Provision.** `POST ProvisionVoiceAccountRequest` (LLSD) with
   `{jsep:{type:"offer", sdp}, channel_type:"local", voice_server_type:"webrtc"[, parcel_local_id]}`. The reply is
   `{jsep:{type:"answer", sdp}, viewer_session}`. Errors: 409 = channel full, 401 = channel locked, 472 = voice not
   allowed or offer rejected. Without `voice_server_type:"webrtc"` the server assumes Vivox and errors out.
   Group/ad-hoc/P2P use `channel_type:"multiagent"` plus `channel` and `credentials`, which we don't use yet.
4. **ICE trickle.** `POST VoiceSignalingRequest` with
   `{viewer_session, voice_server_type:"webrtc", candidates:[{sdpMid, sdpMLineIndex, candidate}]}`, then once
   `{viewer_session, voice_server_type, candidate:{completed:true}}`. Note the singular `candidate` key with a map
   value; LibreMetaverse saw "Unknown conference" when this was wrong. aiortc doesn't trickle (its candidates are already
   in the offer), so the client re-sends them as one `candidates` list and then sends `completed`, like the LL viewer.
   Remote candidates with port 0 are dropped.
5. **Data channel.** On open we send `{"j":{"p":true}}`, which joins as the primary (own-region) connection. Then our
   position: `{"sp":{x,y,z},"sh":{x,y,z,w},"lp":{..},"lh":{..}}`. These are integers: the global position in cm (the
   avatar plus 1 m, at head height) and the rotation ×100. `lp`/`lh` are the listener (camera, tethered within 50 m of
   the avatar); here the listener is the avatar. The server sends `{"<agent id>":{"j":{"p":bool}, "l":true, "p":0-128,
   "v":speaking, "m":moderator-muted}}`, plus `"V":"<version>"` for our own id after the join. Optional sends: per-peer
   mute `{"m":{"<id>":true}}` and gain `{"ug":{"<id>":0-220}}`. We don't use either.
6. **Audio.** The server sends one Opus 48 kHz stereo stream, already mixed and spatialized (HRTF). There's no separate
   stream per speaker, so the transcript attributes speakers by the `v` flags during each utterance.
7. **Leave.** `POST ProvisionVoiceAccountRequest {logout:true, viewer_session, voice_server_type:"webrtc"}` and close
   the peer connection.

Box network (checked Oct 5): outgoing UDP works, but the NAT maps each destination to a different public IP and port,
so the server-reflexive candidate is useless. ICE relies on the voice server's public host candidate plus a
peer-reflexive pair; we're the controlling side. If that ever fails, ICE stalls in `checking`. The client reports it
and retries with backoff.

## Speaking later

The audio m-line is already negotiated. Speaking means attaching a `MediaStreamTrack` (for example TTS audio) to the
sidecar's audio sender, behind an explicit client flag. Today no code path attaches a track.
