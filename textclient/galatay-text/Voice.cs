// Voice.cs (2026-10-05): LISTEN-ONLY Second Life WebRTC voice (spatial / parcel channel) + local transcription.
// This client brokers the signalling over the region caps; the sidecar (galatay-voice/voice_sidecar.py: Python aiortc +
// faster-whisper, its own process so a native crash there can never take the SL session down) does WebRTC, Opus decode,
// speech segmenting and speech-to-text, and writes /workspace/secondlife/voice/transcript-<date>.md.
// Protocol (as the LL viewer's llvoicewebrtc.cpp, reimplemented; see notes.md "Voice"):
//   1. sidecar -> {"t":"offer"}: SDP with all ICE candidates (aiortc does not trickle), opus/48000/2 + LL's fmtp line,
//      an audio m-line WITHOUT a local track (nothing is ever sent) and the "SLData" data channel.
//   2. POST ProvisionVoiceAccountRequest {jsep:{type:"offer",sdp}, channel_type:"local", voice_server_type:"webrtc"
//      [, parcel_local_id when the parcel has its own channel]} -> {jsep:{type:"answer",sdp}, viewer_session}
//      (409 = channel full, 401 = channel locked, 472 = voice not allowed here).
//   3. answer -> sidecar; POST VoiceSignalingRequest {viewer_session, voice_server_type, candidates:[{sdpMid,
//      sdpMLineIndex,candidate}]} with the offer's candidates, then {.., candidate:{completed:true}}.
//   4. data channel: sidecar sends {"j":{"p":true}} (join as the primary region), then our position relayed from here
//      every second ({sp,sh,lp,lh}: global position in cm, rotation x100; listener = avatar head, no camera here).
//      The server sends {"<agent id>":{"j":{..},"l":bool,"p":0-128,"v":speaking}} -> speaker names on the transcript.
//   5. voice off / region or parcel-channel change: sidecar stop + POST ProvisionVoiceAccountRequest {logout:true,
//      viewer_session, voice_server_type}; on a change it reconnects for the new place.
// Off by default and after every login: Galatea only joins voice when David asks (design doc §4.5).
// Wake (2026-10-05): finished transcript lines can POST the chat webhook (type "voice") so the chat routine can reply
// in local text chat. Modes: voice wake off|name|all (default name = only when a line mentions Galatea/Galatay/Gal/
// Nightingale, STT-tolerant, plus open floor invitations like "questions or comments?"; name mode includes prior ~30 s as context and marks trigger name|invitation). Debounce ~3 s per speaker
// batch; rate-limit ~1 wake / 10 s (extras merge). Own lines never wake. Transcription/file logging unchanged.
// Commands: voice on|off|status|tail [n] | voice wake off|name|all|test|selftest
// offline: galatay-text --voice-selftest (broker+sidecar) and voice wake selftest (filter/rate/own)
// ponytail: primary region only (no neighbour-region connections like the LL estate session); ceiling = speakers across
// a region border are not heard.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.StructuredData;

namespace GalatayText;

public static partial class Program
{
    static readonly string VoicePy = Env("GT_VOICE_PY", "/home/box/viewers/textclient/voice-venv/bin/python");
    static readonly string VoiceScript = Env("GT_VOICE_SIDECAR", "/home/box/viewers/textclient/voice/voice_sidecar.py");
    static string VoiceDir = Env("GT_VOICE_DIR", "/workspace/secondlife/voice");
    static readonly string VoiceModel = Env("GT_VOICE_MODEL", "small.en");
    static readonly string VoiceDirection = Env("GT_VOICE_DIRECTION", "sendrecv"); // sendrecv (as LL, no track) | recvonly
    const int VoiceConnectTimeoutS = 45;

    sealed class VSess
    {
        public Process P; public UUID ViewerSession; public ulong Region; public int ParcelId = -1; public string ParcelName, Channel;
        public Uri Prov, Sig; public DateTime Started = DateTime.Now; public DateTime? NotConnectedSince = DateTime.Now; public volatile bool Stopped;
        public string Pc = "new", Ice = "new", Dc = "closed", LastLine; public int Lines;
        public readonly ConcurrentDictionary<UUID, bool> Named = new();
    }
    static VSess vCur;
    static readonly SemaphoreSlim vLock = new(1, 1);
    static volatile bool vWanted;
    static Task vLoop;
    static string vNote = "";
    static int vFails; static DateTime vNextTry = DateTime.MinValue;
    static (Uri prov, Uri sig)? vCapsOverride; // selftest: fake caps on 127.0.0.1
    static GridClient vHttpOnly;              // selftest: an HttpCapsClient without a login
    static HttpCapsClient VHttp => client?.HttpCapsClient ?? (vHttpOnly ??= new GridClient()).HttpCapsClient;
    static void VLog(string m) => Log("voice", m);

    // ---- webhook wake (type "voice") -----------------------------------------------------------------
    public enum VoiceWakeMode { Off, Name, All }
    public static VoiceWakeMode VWakeMode = VoiceWakeMode.Name; // default: only when a line mentions me
    static readonly object vWakeGate = new();
    sealed record VLine(DateTime At, string Speaker, string SpeakerId, string Text, string Trigger = null);
    static readonly List<VLine> vRecent = new();   // rolling ~30 s of all others' lines (context)
    static readonly List<VLine> vPending = new();  // wake-worthy, waiting for debounce / rate limit
    static DateTime vLastWakeAt = DateTime.MinValue;
    static int vWakeFlushGen;
    static Func<DateTime> vNow = () => DateTime.Now; // selftest clock hook
    public static double VoiceDebounceS = 1.5; // 2026-10-07: was 3 (latency)
    public static double VoiceMinWakeS = 5;    // first wake is never delayed (vLastWakeAt = MinValue)
    public static double VoiceContextS = 30;
    // Galatea / Galatay / Gal / Nightingale, tolerant of common STT splits/misspellings
    static readonly Regex VoiceNameRx = new(
        @"\b(?:gal(?:at(?:ay|ai|ae?a?|ea|ia|iya))?|gala\s*t(?:ea|ay|ai|ey)|galla?\s*tay|night[\s\-]?ingales?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Open floor at talks (Shi Wayne etc.): "questions, comments", "questions or comments?", "any questions", ...
    // Pair of invite-nouns (any order) joined by comma / and / or / . / spaces; optional anyone/anything/please.
    // Adjacent-only separators keep "questions about comments in the sutra" from matching.
    static readonly Regex VoiceInviteRx = new(
        @"\b(?:(?:any|anyone'?s?|anybody'?s?)\s+(?:questions?|comments?|thoughts?|remarks?)(?:\s+or\s+(?:questions?|comments?|thoughts?|remarks?))?|\b(questions?|comments?|thoughts?|remarks?)\b(?:\s*,\s*|\s+(?:and|or)\s+|[.!?]+\s*|\s+)(?!\1\b)\b(questions?|comments?|thoughts?|remarks?)\b(?:\s*,\s*|\s+)?(?:(?:anyone|anything|anybody|please)\??)?|anyone\s+(?:want(?:s|ed)?\s+to\s+)?(?:share|speak|comment|ask)|(?:want(?:s|ed)?|like)\s+to\s+(?:share|speak|comment|ask)|open\s+(?:floor|discussion)|(?:questions?|comments?)\s*\??\s*$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);


    static async Task<string> VoiceCmd(string[] a)
    {
        var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
        switch (sub)
        {
            case "on":
                vWanted = true; vFails = 0; vNextTry = DateTime.MinValue;
                if (vCur != null && !vCur.Stopped && !vCur.P.HasExited) return "voice already on: " + VoiceStatusLine();
                var r = await VoiceStart("voice on");
                vLoop ??= Task.Run(VoiceLoop); // never exits; idles while voice is off
                return r;
            case "off":
                vWanted = false;
                var had = vCur != null;
                await VoiceStop("voice off");
                return had ? "voice off (left the channel; the sidecar finishes transcribing what it already heard)" : "voice was already off";
            case "status": return VoiceStatus();
            case "tail": return VoiceTail(a.Length > 1 && int.TryParse(a[1], out var n) ? Math.Clamp(n, 1, 200) : 15);
            case "wake": return VoiceWakeCmd(a.Length > 1 ? a[1..] : Array.Empty<string>());
            default: return "usage: voice on | voice off | voice status | voice tail [n] | voice wake off|name|all|test|selftest";
        }
    }

    static string VoiceWakeCmd(string[] a)
    {
        var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "";
        if (sub is "off" or "name" or "all")
        {
            VWakeMode = sub switch { "off" => VoiceWakeMode.Off, "all" => VoiceWakeMode.All, _ => VoiceWakeMode.Name };
            VLog($"wake mode set to {VWakeMode.ToString().ToLowerInvariant()}");
            return $"voice wake {VWakeMode.ToString().ToLowerInvariant()}" +
                   (VWakeMode == VoiceWakeMode.Name ? " (any line from David, mentions of me, or open invitations like questions/comments; prior ~30 s context; trigger=name|invitation)" :
                    VWakeMode == VoiceWakeMode.All ? " (every utterance, debounced ~1.5 s / rate-limited ~5 s)" :
                    " (transcript still written; webhook never woken for voice)");
        }
        if (sub == "test")
        {
            // synthetic wake for end-to-end checks; clearly marked so the chat routine should not reply
            var parcel = vCur?.ParcelName ?? "(test)";
            var channel = vCur?.Channel ?? "voice";
            Notify("voice", "VoiceWakeTest", UUID.Zero,
                "[VOICE WAKE TEST — no reply needed] Synthetic voice event to verify the webhook path. Ignore this.",
                null, null, parcel, null, "voice");
            return "voice wake test: queued one synthetic type=voice webhook event (text says no reply needed)";
        }
        if (sub == "selftest") return VoiceWakeSelfTest();
        if (sub.Length == 0)
            return $"voice wake {VWakeMode.ToString().ToLowerInvariant()} (off|name|all; default name). Debounce {VoiceDebounceS:g} s, min interval {VoiceMinWakeS:g} s, context {VoiceContextS:g} s in name mode.";
        return "usage: voice wake off|name|all|test|selftest";
    }

    // ---- session start / stop ---------------------------------------------------------------------------
    static async Task<string> VoiceStart(string why)
    {
        if (!await vLock.WaitAsync(30000)) return "voice: another start/stop is still running";
        try
        {
            if (vCur != null) await VoiceStopLocked("restart: " + why);
            var s = new VSess();
            if (vCapsOverride is { } ov) { s.Prov = ov.prov; s.Sig = ov.sig; s.ParcelId = 7; s.Channel = "parcel voice"; s.ParcelName = "Selftest parcel"; }
            else
            {
                if (!LoggedIn) return "not logged in";
                var sim = client.Network.CurrentSim;
                if (sim == null) return "voice: no current region";
                if (!sim.Flags.HasFlag(RegionFlags.AllowVoice)) return vNote = $"voice is disabled in region {sim.Name}; not joining (will retry if I move)";
                var pos = client.Self.SimPosition;
                var p = await ParcelAt(sim, pos.X, pos.Y, 8000);
                if (p != null && !p.Flags.HasFlag(ParcelFlags.AllowVoiceChat)) return vNote = $"voice is disabled on parcel '{p.Name}'; not joining (will retry if I move)";
                s.ParcelId = p == null || p.Flags.HasFlag(ParcelFlags.UseEstateVoiceChan) ? -1 : p.LocalID;
                s.ParcelName = p?.Name; s.Region = sim.Handle;
                s.Channel = p == null ? "region voice (parcel unknown)" : s.ParcelId > 0 ? "parcel voice" : "estate/region voice";
                s.Prov = sim.Caps?.CapabilityURI("ProvisionVoiceAccountRequest");
                s.Sig = sim.Caps?.CapabilityURI("VoiceSignalingRequest");
                if (s.Prov == null || s.Sig == null) return vNote = $"region {sim.Name} offers no WebRTC voice caps yet; will retry";
            }
            if (!File.Exists(VoicePy) || !File.Exists(VoiceScript)) return vNote = $"voice sidecar not installed ({VoicePy}, {VoiceScript}); run textclient/galatay-voice/install.sh";
            Directory.CreateDirectory(VoiceDir);
            var psi = new ProcessStartInfo(VoicePy) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var x in new[] { VoiceScript, "--out", VoiceDir, "--model", VoiceModel, "--direction", VoiceDirection }) psi.ArgumentList.Add(x);
            s.P = Process.Start(psi);
            if (s.P == null) return vNote = "voice: could not start the sidecar";
            vCur = s;
            _ = Task.Run(() => VoiceReadOut(s));
            _ = Task.Run(() => VoiceReadErr(s));
            VSend(s, new { t = "meta", region = vCapsOverride != null ? "Selftest" : client.Network.CurrentSim?.Name, parcel = s.ParcelName, channel = s.Channel,
                           self = client?.Self?.AgentID.ToString() });
            VoiceSendPos(s);
            vNote = "";
            VLog($"joining {s.Channel}{(s.ParcelName != null ? $" on parcel '{s.ParcelName}'" : "")} ({why}); sidecar pid {s.P.Id}, listen-only");
            return $"voice: joining {s.Channel}{(s.ParcelName != null ? $" on parcel '{s.ParcelName}'" : "")}, listen-only (mic never sent). 'voice status' shows the connection; transcript: {VoiceTranscriptPath()}";
        }
        catch (Exception ex) { return vNote = "voice start failed: " + ex.GetBaseException().Message; }
        finally { vLock.Release(); }
    }

    static async Task VoiceStop(string why, int logoutTimeoutMs = 10000)
    {
        if (!await vLock.WaitAsync(30000)) { VLog("stop: lock busy, stopping anyway"); }
        else try { await VoiceStopLocked(why, logoutTimeoutMs); } finally { vLock.Release(); }
    }

    static async Task VoiceStopLocked(string why, int logoutTimeoutMs = 10000)
    {
        var s = vCur; vCur = null;
        if (s == null || s.Stopped) return;
        s.Stopped = true;
        try { VSend(s, new { t = "stop", why }); s.P.StandardInput.Close(); } catch { }
        if (s.ViewerSession != UUID.Zero && s.Prov != null)
        {
            try
            {
                using var t = new CancellationTokenSource(logoutTimeoutMs);
                var (resp, _) = await VHttp.PostAsync(s.Prov, OSDFormat.Xml,
                    new OSDMap { ["logout"] = OSD.FromBoolean(true), ["viewer_session"] = OSD.FromUUID(s.ViewerSession), ["voice_server_type"] = "webrtc" }, t.Token);
                VLog($"left voice ({why}): logout HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex) { VLog($"left voice ({why}); logout POST failed: {ex.GetBaseException().Message}"); }
        }
        else VLog($"voice session stopped ({why}) before it was provisioned");
        // the sidecar finishes transcribing its backlog on its own (bounded); kill only if it hangs far past that
        var p = s.P;
        _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromMinutes(12)); try { if (!p.HasExited) p.Kill(); } catch { } });
    }

    static void VoiceFail(VSess s, string why, bool definitive = false)
    {
        if (s.Stopped) return;
        vFails++;
        var wait = definitive ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(3, Math.Min(vFails - 1, 4))));
        vNextTry = DateTime.Now + wait;
        vNote = $"{why} (attempt {vFails}; next try {vNextTry:HH:mm:ss} unless I move)";
        VLog("FAILED: " + vNote);
        if (ReferenceEquals(vCur, s)) _ = VoiceStop("failure: " + why, 5000);
    }

    // ---- sidecar I/O ------------------------------------------------------------------------------------
    static void VSend(VSess s, object o)
    {
        try { lock (s) { s.P.StandardInput.WriteLine(JsonSerializer.Serialize(o)); s.P.StandardInput.Flush(); } } catch { }
    }

    static async Task VoiceReadErr(VSess s)
    {
        try { string l; while ((l = await s.P.StandardError.ReadLineAsync()) != null) Log("voice-sc", l.Length > 300 ? l[..300] : l); } catch { }
    }

    static async Task VoiceReadOut(VSess s)
    {
        try
        {
            string l;
            while ((l = await s.P.StandardOutput.ReadLineAsync()) != null)
            {
                JsonNode m; try { m = JsonNode.Parse(l); } catch { continue; }
                switch ((string)m?["t"])
                {
                    case "offer": _ = Task.Run(() => VoiceProvision(s, (string)m["sdp"])); break;
                    case "state":
                        var (pc, ice, dc) = ((string)m["pc"] ?? s.Pc, (string)m["ice"] ?? s.Ice, (string)m["dc"] ?? s.Dc);
                        if (pc != s.Pc || dc != s.Dc) VLog($"connection {pc}, ICE {ice}, data channel {dc}");
                        s.Pc = pc; s.Ice = ice; s.Dc = dc;
                        if (pc == "connected") vFails = 0;
                        if (pc == "failed") VoiceFail(s, $"WebRTC connection failed (ICE {ice})");
                        break;
                    case "peer":
                        if (UUID.TryParse((string)m["id"], out var id)) _ = Task.Run(() => VoiceName(s, id));
                        break;
                    case "line":
                        VoiceHandleLine(s, m);
                        break;
                    case "error": if (!s.Stopped) VoiceFail(s, "sidecar: " + (string)m["msg"]); break;
                }
            }
        }
        catch { }
        if (!s.Stopped && ReferenceEquals(vCur, s)) VoiceFail(s, $"sidecar exited (code {(s.P.HasExited ? s.P.ExitCode : -1)})");
    }

    static async Task VoiceName(VSess s, UUID id)
    {
        if (!s.Named.TryAdd(id, true)) return;
        if (NameOf(id) == id.ToString()) { try { client?.Avatars.RequestAvatarName(id); } catch { } await Task.Delay(3000); }
        var n = NameOf(id);
        if (n != id.ToString()) VSend(s, new { t = "name", id = id.ToString(), name = n });
        else s.Named.TryRemove(id, out _); // unknown for now; the next 'peer' sighting retries
    }

    static async Task VoiceProvision(VSess s, string sdp)
    {
        try
        {
            var body = new OSDMap { ["jsep"] = new OSDMap { ["type"] = "offer", ["sdp"] = sdp }, ["channel_type"] = "local", ["voice_server_type"] = "webrtc" };
            if (s.ParcelId > 0) body["parcel_local_id"] = OSD.FromInteger(s.ParcelId);
            OSDMap r; int code;
            using (var t = new CancellationTokenSource(30000))
            {
                var (resp, data) = await VHttp.PostAsync(s.Prov, OSDFormat.Xml, body, t.Token);
                code = (int)resp.StatusCode;
                r = data != null && data.Length > 0 ? (OSDParser.Deserialize(data) as OSDMap) : null;
            }
            if (s.Stopped) return;
            if (code < 200 || code >= 300)
            {
                var why = code switch { 409 => "voice channel is full", 401 => "voice channel is locked", 472 => "the server refused voice here (472: voice disabled or offer rejected)", _ => $"provisioning HTTP {code}" };
                VoiceFail(s, why, code is 401 or 409 or 472); return;
            }
            var jsep = r?["jsep"] as OSDMap;
            if (jsep == null || jsep["type"].AsString() != "answer" || string.IsNullOrEmpty(jsep["sdp"].AsString()))
            { VoiceFail(s, $"provisioning reply without an SDP answer (keys: {string.Join(",", r?.Keys ?? new List<string>())})", true); return; }
            s.ViewerSession = r["viewer_session"].AsUUID();
            VSend(s, new { t = "answer", sdp = jsep["sdp"].AsString() });
            VLog($"provisioned: viewer session {s.ViewerSession.ToString()[..8]}…, answer applied");
            var cands = VoiceOfferCandidates(sdp);
            if (cands.Count > 0) await VoiceSignal(s, new OSDMap { ["candidates"] = cands });
            await VoiceSignal(s, new OSDMap { ["candidate"] = new OSDMap { ["completed"] = OSD.FromBoolean(true) } });
        }
        catch (Exception ex) { VoiceFail(s, "provisioning failed: " + ex.GetBaseException().Message); }
    }

    static async Task VoiceSignal(VSess s, OSDMap body)
    {
        body["viewer_session"] = OSD.FromUUID(s.ViewerSession); body["voice_server_type"] = "webrtc";
        using var t = new CancellationTokenSource(20000);
        var (resp, _) = await VHttp.PostAsync(s.Sig, OSDFormat.Xml, body, t.Token);
        if ((int)resp.StatusCode >= 300) VLog($"VoiceSignalingRequest HTTP {(int)resp.StatusCode} ({string.Join(",", body.Keys)})");
    }

    // candidates of the first m-section (BUNDLE: all sections share them), as the LL viewer trickles them
    static OSDArray VoiceOfferCandidates(string sdp)
    {
        var arr = new OSDArray(); int mline = -1; string mid = null;
        foreach (var raw in sdp.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("m=")) { mline++; if (mline > 0) break; mid = null; continue; }
            if (line.StartsWith("a=mid:")) mid = line[6..];
            else if (line.StartsWith("a=candidate:") && mline == 0)
                arr.Add(new OSDMap { ["sdpMid"] = mid ?? "0", ["sdpMLineIndex"] = OSD.FromInteger(0), ["candidate"] = line[2..] });
        }
        return arr;
    }

    static object VoicePosObj(Vector3d gp, Quaternion rot)
    {
        long C(double v) => (long)Math.Round(v * 100);
        var sp = new[] { C(gp.X), C(gp.Y), C(gp.Z + 1.0) };          // head height, as the LL viewer
        var sh = new[] { C(rot.X), C(rot.Y), C(rot.Z), C(rot.W) };
        return new { t = "pos", sp, sh, lp = sp, lh = sh };            // listener = avatar (headless: no camera)
    }

    static void VoiceSendPos(VSess s)
    {
        if (client?.Self == null || !LoggedIn) return;
        var gp = client.Self.GlobalPosition;
        if (gp.X == 0 && gp.Y == 0) return;
        VSend(s, VoicePosObj(gp, client.Self.SimRotation));
    }

    // ---- supervision: position, reconnects, region / parcel-channel changes --------------------------------
    static async Task VoiceLoop()
    {
        int tick = 0;
        while (true)
        {
            await Task.Delay(1000);
            if (!vWanted) continue; // started once, idles while off (no start/stop race)
            tick++;
            try
            {
                var s = vCur;
                if (s != null && !s.Stopped) VoiceSendPos(s);
                if (s != null && !s.Stopped)
                {
                    if (s.Pc == "connected") s.NotConnectedSince = null; else s.NotConnectedSince ??= DateTime.Now;
                    if (s.NotConnectedSince is { } since && (DateTime.Now - since).TotalSeconds > VoiceConnectTimeoutS)
                        VoiceFail(s, $"not connected for {VoiceConnectTimeoutS} s (connection {s.Pc}, ICE {s.Ice})");
                }
                if (vCapsOverride != null) continue;
                if (!LoggedIn) continue;
                var sim = client.Network.CurrentSim;
                if (s != null && !s.Stopped && sim != null && sim.Handle != s.Region)
                { vFails = 0; vNextTry = DateTime.MinValue; await VoiceStart($"region changed to {sim.Name}"); continue; }
                if (s != null && !s.Stopped && sim != null && tick % 15 == 0)
                {
                    var p = await ParcelAt(sim, client.Self.SimPosition.X, client.Self.SimPosition.Y, 5000);
                    var id = p == null ? s.ParcelId : p.Flags.HasFlag(ParcelFlags.UseEstateVoiceChan) ? -1 : p.LocalID;
                    if (p != null && (id != s.ParcelId || !p.Flags.HasFlag(ParcelFlags.AllowVoiceChat)))
                    { vFails = 0; await VoiceStart($"voice channel changed (now parcel '{p.Name}')"); continue; }
                }
                if ((s == null || s.Stopped) && DateTime.Now >= vNextTry && tick % 5 == 0)
                {
                    var r = await VoiceStart("retry");
                    if (vCur == null) { vFails++; vNextTry = DateTime.Now + TimeSpan.FromSeconds(Math.Min(300, 15 * vFails)); VLog("retry: " + r); }
                }
            }
            catch (Exception ex) { VLog("loop error: " + ex.GetBaseException().Message); }
        }
    }

    public static async Task VoiceShutdown()
    {
        vWanted = false;
        if (vCur != null) await VoiceStop("client shutdown", 3000);
    }

    // ---- status / tail -----------------------------------------------------------------------------------
    static string VoiceTranscriptPath(DateTime? d = null) => Path.Combine(VoiceDir, $"transcript-{(d ?? DateTime.Now):yyyy-MM-dd}.md");

    static string VoiceStatusLine()
    {
        var s = vCur;
        if (s == null) return vWanted ? $"wanted but not joined: {vNote}" : "off";
        return $"{s.Channel}{(s.ParcelName != null ? $" '{s.ParcelName}'" : "")}: connection {s.Pc}, ICE {s.Ice}, data channel {s.Dc}, up {(int)(DateTime.Now - s.Started).TotalMinutes} min";
    }

    static string VoiceStatus()
    {
        var sb = new StringBuilder($"voice {(vWanted ? "ON (listen-only, mic never sent)" : "off")}: {VoiceStatusLine()}");
        sb.Append($"\n  wake: {VWakeMode.ToString().ToLowerInvariant()} (off|name|all; name = mention me, open invitation, or any David line; debounce {VoiceDebounceS:g} s, min {VoiceMinWakeS:g} s)");
        if (vNote.Length > 0 && vCur != null) sb.Append($"\n  note: {vNote}");
        try
        {
            var f = Path.Combine(VoiceDir, "status.json");
            if (File.Exists(f) && (vCur != null || (DateTime.Now - File.GetLastWriteTime(f)).TotalMinutes < 15))
            {
                var j = JsonNode.Parse(File.ReadAllText(f));
                var st = j["state"];
                var peers = (j["peers"] as JsonArray)?.Where(p => p?["left"]?.GetValue<bool>() != true)
                    .Select(p => ((string)p["name"] ?? ((string)p["id"])[..8]) + ((bool?)p["speaking"] == true ? " (speaking)" : "")).ToList() ?? new();
                sb.Append($"\n  in channel: {(peers.Count == 0 ? "nobody else" : string.Join(", ", peers))}");
                sb.Append($"\n  audio: {st?["frames"]} frames received, last {(j["audio_age_s"] is JsonNode age ? age + " s ago" : "never")}, level {j["rms_dbfs"]} dBFS; {st?["segments"]} speech segments");
                sb.Append($"\n  transcription: model {j["model"]}, {j["lines"]} lines, backlog {j["backlog"]} chunk(s); sidecar pid {j["pid"]}{(vCur == null ? " (draining or finished)" : "")}");
            }
        }
        catch (Exception ex) { sb.Append($"\n  (status.json unreadable: {ex.GetType().Name})"); }
        sb.Append($"\n  transcript: {VoiceTranscriptPath()}");
        if (vCur?.LastLine != null) sb.Append($"\n  last line: {vCur.LastLine}");
        return sb.ToString();
    }

    static string VoiceTail(int n)
    {
        var f = VoiceTranscriptPath();
        if (!File.Exists(f))
            f = Directory.Exists(VoiceDir) ? Directory.GetFiles(VoiceDir, "transcript-*.md").OrderBy(x => x).LastOrDefault() : null;
        if (f == null) return "no transcript yet";
        var lines = File.ReadAllLines(f).Where(l => l.StartsWith("- ") || l.StartsWith("## ")).ToList();
        return $"{f} (last {Math.Min(n, lines.Count)} of {lines.Count(l => l.StartsWith("- "))} lines):\n" + string.Join("\n", lines.TakeLast(n));
    }

    // ---- webhook wake from transcript lines --------------------------------------------------------------
    // pure: does this STT text mention Galatea / Galatay / Gal / Nightingale (tolerant of common misspellings)?
    public static bool VoiceMentionsMe(string text) =>
        !string.IsNullOrWhiteSpace(text) && VoiceNameRx.IsMatch(text);

    // pure: open invitation to the group (Buddha Center floor, etc.), STT-tolerant
    public static bool VoiceIsInvitation(string text) =>
        !string.IsNullOrWhiteSpace(text) && VoiceInviteRx.IsMatch(text);

    // pure: why would name-mode wake on this line? "name", "invitation", or null (no wake)
    // Name wins when both match (someone said "Galatea, any questions?").
    // 2026-10-07 (David): every line from David wakes (trigger "david"); his pet names (babe/baby/honey/love) count as
    // "name". Other speakers stay name/invitation only, and pet names from them never wake.
    public static string VoiceNameModeTrigger(string text, string speakerId = null)
    {
        bool david = VoiceIsDavid(speakerId);
        if (VoiceMentionsMe(text) || (david && VoicePetNameRx.IsMatch(text ?? ""))) return "name";
        if (VoiceIsInvitation(text)) return "invitation";
        if (david && !string.IsNullOrWhiteSpace(text)) return "david";
        return null;
    }
    static readonly Regex VoicePetNameRx = new(@"\b(babe|baby|honey|love)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static bool VoiceIsDavid(string speakerId) => UUID.TryParse(speakerId ?? "", out var id) && id == DavidId;

    // pure (2026-10-07): is this STT line worth waking for? Drops lines with no letters/digits (".", "...", "♪"),
    // under 2 letters/digits, and bare filler like "uh", "um", "hmm", "mm-hmm". Such lines are still logged, never woken.
    static readonly HashSet<string> VoiceFiller = new(StringComparer.OrdinalIgnoreCase) { "uh", "um", "umm", "uhm", "hm", "hmm", "hmmm", "mm", "mmm", "mhm", "mmhmm", "ah", "oh", "eh", "er", "erm" };
    public static bool VoiceIsMeaningful(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var alnum = new string(text.Where(char.IsLetterOrDigit).ToArray());
        if (alnum.Length < 2) return false;
        return !VoiceFiller.Contains(alnum);
    }

    // pure (2026-10-07): noise/empty STT line -> "unclear" only for David with >= ~0.8 s of real speech (so I can say
    // I didn't catch that); short blips and everyone else's noise -> null (drop).
    public const double VoiceUnclearMinSpeechS = 0.8;
    public static string VoiceNoiseTrigger(string speakerId, double? speechS) =>
        VoiceIsDavid(speakerId) && speechS is double s && s >= VoiceUnclearMinSpeechS ? "unclear" : null;

    // pure: is this line from Galatea herself? (she has no mic; still filter defensively)
    public static bool VoiceIsOwn(string speaker, string speakerId, UUID? selfId, string selfName, string selfDisplay)
    {
        if (selfId is UUID sid && sid != UUID.Zero && UUID.TryParse(speakerId, out var id) && id == sid) return true;
        if (string.IsNullOrWhiteSpace(speaker)) return false;
        var sp = speaker.Trim();
        foreach (var n in new[] { selfName, selfDisplay, "Galatay Resident", "Galatea Nightingale", "Galatay", "Galatea" })
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            if (sp.Equals(n, StringComparison.OrdinalIgnoreCase)) return true;
            if (sp.StartsWith(n + " ", StringComparison.OrdinalIgnoreCase)) return true;
            if (sp.EndsWith(" / " + n, StringComparison.OrdinalIgnoreCase) || sp.EndsWith("/ " + n, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    static bool VoiceIsOwnLive(string speaker, string speakerId)
    {
        var self = client?.Self;
        return VoiceIsOwn(speaker, speakerId, self?.AgentID, self == null ? null : $"{self.FirstName} {self.LastName}", null);
    }

    static void VoiceHandleLine(VSess s, JsonNode m)
    {
        var text = ((string)m?["text"] ?? "").Trim();
        var speaker = ((string)m?["speaker"] ?? "(unattributed)").Trim();
        var speakerId = ((string)m?["speaker_id"] ?? "").Trim();
        var timeStr = (string)m?["time"] ?? vNow().ToString("HH:mm:ss");
        double? speechS = m?["speech_s"] is JsonNode sn ? (double)sn : null;
        if (text.Length == 0 && speechS is null) return;
        s.Lines++; s.LastLine = $"{timeStr} {speaker}: {(text.Length == 0 ? "[unclear]" : text)}";
        VoiceNoteLine(speaker, speakerId, text, s.ParcelName, s.Channel, speechS);
    }

    // testable entry: record a line and maybe schedule a wake (transcript logging stays in the sidecar)
    public static void VoiceNoteLine(string speaker, string speakerId, string text, string parcel = null, string channel = null, double? speechS = null)
    {
        var at = vNow();
        var own = VoiceIsOwnLive(speaker, speakerId);
        lock (vWakeGate)
        {
            if (!own)
            {
                vRecent.Add(new VLine(at, speaker, speakerId ?? "", text));
                var cut = at.AddSeconds(-(VoiceContextS + 5));
                vRecent.RemoveAll(x => x.At < cut);
            }
        }
        if (own) { VLog($"line (own, not waking): {speaker}: {text}"); return; }
        if (VWakeMode == VoiceWakeMode.Off) { if (!VoiceIsMeaningful(text)) VLog($"line (noise, not waking): {speaker}: {text}"); return; }
        string trigger = null;
        if (!VoiceIsMeaningful(text))
        {
            trigger = VoiceNoiseTrigger(speakerId, speechS);
            if (trigger == null) { VLog($"line (noise, not waking): {speaker}: {text}" + (speechS is double ss ? $" ({ss:0.0} s speech)" : "")); return; }
            if (string.IsNullOrWhiteSpace(text)) text = "[unclear]";
        }
        else if (VWakeMode == VoiceWakeMode.All) trigger = "all";
        else if (VWakeMode == VoiceWakeMode.Name) trigger = VoiceNameModeTrigger(text, speakerId);
        if (trigger == null) return;
        lock (vWakeGate) vPending.Add(new VLine(at, speaker, speakerId ?? "", text, trigger));
        var gen = Interlocked.Increment(ref vWakeFlushGen);
        var parcelCap = parcel; var channelCap = channel;
        _ = Task.Run(() => VoiceWakeFlushAfterDebounce(gen, parcelCap, channelCap));
    }

    static async Task VoiceWakeFlushAfterDebounce(int gen, string parcel, string channel)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(VoiceDebounceS));
            if (gen != Volatile.Read(ref vWakeFlushGen)) return;
            while (true)
            {
                TimeSpan wait;
                lock (vWakeGate)
                {
                    var since = vNow() - vLastWakeAt;
                    wait = since.TotalSeconds >= VoiceMinWakeS ? TimeSpan.Zero : TimeSpan.FromSeconds(VoiceMinWakeS - since.TotalSeconds);
                }
                if (wait <= TimeSpan.Zero) break;
                await Task.Delay(wait);
                if (gen != Volatile.Read(ref vWakeFlushGen)) return;
            }
            VoiceWakeFlush(parcel, channel);
        }
        catch (Exception ex) { VLog("wake flush error: " + ex.GetBaseException().Message); }
    }

    static string VoiceFmtLines(IReadOnlyList<VLine> list, string primarySpeaker)
    {
        if (list == null || list.Count == 0) return null;
        if (list.Count == 1 && list[0].Speaker == primarySpeaker) return list[0].Text;
        return string.Join("\n", list.Select(l => $"{l.Speaker}: {l.Text}"));
    }


    static void VoiceWakeFlush(string parcel, string channel)
    {
        List<VLine> batch; List<VLine> context;
        lock (vWakeGate)
        {
            if (vPending.Count == 0) return;
            batch = vPending.ToList();
            vPending.Clear();
            vLastWakeAt = vNow();
            var batchKeys = new HashSet<(DateTime, string, string)>(batch.Select(b => (b.At, b.Speaker, b.Text)));
            var since = vLastWakeAt.AddSeconds(-VoiceContextS);
            context = vRecent.Where(r => r.At >= since && !batchKeys.Contains((r.At, r.Speaker, r.Text))).ToList();
        }
        var primary = batch[0];
        // Prefer an invitation or name line as primary when present (name over invitation if both in batch)
        if (VWakeMode == VoiceWakeMode.Name)
        {
            var named = batch.FirstOrDefault(b => b.Trigger == "name" || VoiceMentionsMe(b.Text));
            var invited = batch.FirstOrDefault(b => b.Trigger == "invitation" || VoiceIsInvitation(b.Text));
            if (named != null) primary = named;
            else if (invited != null) primary = invited;
        }
        var trigger = primary.Trigger
            ?? (VWakeMode == VoiceWakeMode.All ? "all" : VoiceNameModeTrigger(primary.Text, primary.SpeakerId) ?? "name");
        // If the batch mixed name + invitation, mark name (more specific address)
        if (batch.Any(b => (b.Trigger ?? VoiceNameModeTrigger(b.Text, b.SpeakerId)) == "name")) trigger = "name";
        else if (batch.Any(b => (b.Trigger ?? VoiceNameModeTrigger(b.Text, b.SpeakerId)) == "invitation")) trigger = "invitation";
        var text = VoiceFmtLines(batch, primary.Speaker);
        // context for name-mode wakes (mention or invitation) so she can reply relevantly
        var ctx = VWakeMode == VoiceWakeMode.Name ? VoiceFmtLines(context, primary.Speaker) : null;
        UUID.TryParse(string.IsNullOrEmpty(primary.SpeakerId) ? null : primary.SpeakerId, out var fromId);
        double? dist = null;
        try { if (fromId != UUID.Zero && client?.Self != null && FindAvatarAnySim(fromId) is { } nb) dist = Math.Round(Vector3.Distance(nb.pos, client.Self.SimPosition), 1); } catch { }
        Notify("voice", primary.Speaker, fromId, text, dist, null, parcel, ctx, channel ?? "voice", trigger, primary.Text);
        VLog($"wake ({VWakeMode.ToString().ToLowerInvariant()}/{trigger}): {batch.Count} line(s) from {primary.Speaker}" + (ctx != null ? " (+ context)" : ""));
    }

    public static void VoiceWakeReset()
    {
        lock (vWakeGate) { vRecent.Clear(); vPending.Clear(); vLastWakeAt = DateTime.MinValue; }
        Interlocked.Increment(ref vWakeFlushGen);
        VWakeMode = VoiceWakeMode.Name;
        VoiceDebounceS = 1.5; VoiceMinWakeS = 5; VoiceContextS = 30;
        vNow = () => DateTime.Now;
    }

    static string VoiceWakeSelfTest()
    {
        var sb = new StringBuilder(); int fail = 0;
        void C(bool ok, string what) { sb.AppendLine((ok ? "ok   " : "FAIL ") + what); if (!ok) fail++; }
        void Sleep(double s) => Task.Delay(TimeSpan.FromSeconds(s)).GetAwaiter().GetResult();
        var keepMode = VWakeMode; var keepDeb = VoiceDebounceS; var keepMin = VoiceMinWakeS; var keepCtx = VoiceContextS; var keepNow = vNow;
        var posted = new List<string>();
        var keepPost = GalatayMcp.Webhook.PostOverride;
        var keepDetect = GalatayMcp.Webhook.Detect; var keepQuiet = GalatayMcp.Webhook.Quiet;
        var keepMaxHold = GalatayMcp.Webhook.MaxHold; var keepMinWh = GalatayMcp.Webhook.MinInterval;
        try
        {
            VoiceWakeReset();
            C(VoiceDebounceS <= 1.5 && VoiceMinWakeS <= 5, "low-latency defaults (debounce <= 1.5 s, min <= 5 s)");
            C(GalatayMcp.Webhook.PostsImmediately("voice") && !GalatayMcp.Webhook.PostsImmediately("im"), "voice skips webhook flush loop");
            C(VoiceMentionsMe("Hey Galatea, can you hear me?"), "mentions Galatea");
            C(VoiceMentionsMe("galatay are you there"), "mentions galatay");
            C(VoiceMentionsMe("thanks Gal"), "mentions Gal as a word");
            C(VoiceMentionsMe("David Nightingale is here"), "mentions Nightingale");
            C(VoiceMentionsMe("gala tea come sit"), "STT split gala tea");
            C(VoiceMentionsMe("hey galatai"), "STT galatai");
            C(!VoiceMentionsMe("welcome everyone to the talk"), "no false positive on plain lecture line");
            C(!VoiceMentionsMe("the galaxy is vast"), "no match inside galaxy");
            C(!VoiceMentionsMe(""), "empty text");

            C(VoiceIsInvitation("questions or comments?"), "invitation: questions or comments");
            C(VoiceIsInvitation("Any questions"), "invitation: any questions");
            C(VoiceIsInvitation("any thoughts?"), "invitation: any thoughts");
            C(VoiceIsInvitation("anyone want to share"), "invitation: anyone want to share");
            C(VoiceIsInvitation("Comments?"), "invitation: comments?");
            C(VoiceIsInvitation("question or comment"), "invitation: STT singular question or comment");
            C(VoiceIsInvitation("any question or comments"), "invitation: any question or comments");
            C(VoiceIsInvitation("Questions, comments?"), "invitation: Questions, comments?");
            C(VoiceIsInvitation("questions, comments"), "invitation: questions, comments (Wayne today)");
            C(VoiceIsInvitation("Questions, comments. Anyone?"), "invitation: Questions, comments. Anyone?");
            C(VoiceIsInvitation("Questions comments, please"), "invitation: Questions comments, please");
            C(VoiceIsInvitation("Okay. Questions, comments, anything?"), "invitation: Okay. Questions, comments, anything?");
            C(VoiceIsInvitation("Questions. Comments."), "invitation: STT Questions. Comments.");
            C(VoiceIsInvitation("comments, questions"), "invitation: comments, questions (order swapped)");
            C(VoiceIsInvitation("questions and thoughts"), "invitation: questions and thoughts");
            C(!VoiceIsInvitation("the question of suffering"), "no invitation on 'the question of...'");
            C(!VoiceIsInvitation("I have some questions about comments in the sutra"), "no invitation on 'questions about comments'");
            C(VoiceNameModeTrigger("Galatea any questions?") == "name", "name wins over invitation when both match");
            C(VoiceNameModeTrigger("questions or comments?") == "invitation", "trigger=invitation");
            C(VoiceNameModeTrigger("hey Galatay") == "name", "trigger=name");
            C(VoiceNameModeTrigger("Bodhidharma came west") == null, "no trigger on plain lecture");
            C(!VoiceIsMeaningful("."), "noise: '.' dropped");
            C(!VoiceIsMeaningful(" ... "), "noise: '...' dropped");
            C(!VoiceIsMeaningful("♪"), "noise: music symbol dropped");
            C(!VoiceIsMeaningful("I"), "noise: single letter dropped");
            C(!VoiceIsMeaningful("Hmm."), "noise: filler hmm dropped");
            C(!VoiceIsMeaningful("Mm-hmm."), "noise: filler mm-hmm dropped");
            C(VoiceIsMeaningful("Hi"), "short real word kept");
            C(VoiceIsMeaningful("Hey babe, you're looking good today."), "real line kept");
            C(VoiceNoiseTrigger("44ce5a36-c1c7-4a68-ac9a-635ddfff6233", 1.2) == "unclear", "David noise with 1.2 s speech -> unclear");
            C(VoiceNoiseTrigger("44ce5a36-c1c7-4a68-ac9a-635ddfff6233", 0.5) == null, "David blip under 0.8 s dropped");
            C(VoiceNoiseTrigger("44ce5a36-c1c7-4a68-ac9a-635ddfff6233", null) == null, "David noise without speech length dropped");
            C(VoiceNoiseTrigger("22222222-2222-2222-2222-222222222222", 3.0) == null, "others' noise dropped even when long");
            const string davidId = "44ce5a36-c1c7-4a68-ac9a-635ddfff6233", otherId = "22222222-2222-2222-2222-222222222222";
            C(VoiceNameModeTrigger("you're looking good today", davidId) == "david", "David: any line wakes (trigger=david)");
            C(VoiceNameModeTrigger("Hey babe, you're looking good today.", davidId) == "name", "David: pet name counts as name");
            C(VoiceNameModeTrigger("Hey babe, you're looking good today.", otherId) == null, "others: pet name never wakes");
            C(VoiceNameModeTrigger("you're looking good today", otherId) == null, "others: plain line never wakes");
            C(VoiceNameModeTrigger("I love this place", otherId) == null, "others: 'love' never wakes");

            var me = UUID.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            C(VoiceIsOwn("Galatay Resident", me.ToString(), me, "Galatay Resident", "Galatea Nightingale"), "own by UUID");
            C(VoiceIsOwn("Galatea Nightingale", "", me, "Galatay Resident", "Galatea Nightingale"), "own by display name");
            C(!VoiceIsOwn("David Nightingale", "44ce5a36-c1c7-4a68-ac9a-635ddfff6233", me, "Galatay Resident", "Galatea Nightingale"), "David is not own");

            GalatayMcp.Webhook.PostOverride = async body => { lock (posted) posted.Add(body); await Task.CompletedTask; return (200, null); };
            GalatayMcp.Webhook.Init();
            GalatayMcp.Webhook.Detect = TimeSpan.FromMilliseconds(50);
            GalatayMcp.Webhook.Quiet = TimeSpan.FromMilliseconds(80);
            GalatayMcp.Webhook.MaxHold = TimeSpan.FromMilliseconds(200);
            GalatayMcp.Webhook.MinInterval = TimeSpan.FromMilliseconds(50);

            // all mode: batches nearby lines into one POST
            posted.Clear();
            VoiceWakeReset(); VoiceDebounceS = 0.2; VoiceMinWakeS = 0.5;
            var t0 = DateTime.Now; var clock = t0; vNow = () => clock;
            VWakeMode = VoiceWakeMode.All;
            VoiceNoteLine("Shi Wayne", "11111111-1111-1111-1111-111111111111", "welcome to the history of Buddhism", "Sangha", "parcel voice");
            clock = clock.AddSeconds(1);
            VoiceNoteLine("Shi Wayne", "11111111-1111-1111-1111-111111111111", "Bodhidharma came from the west", "Sangha", "parcel voice");
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (posted.Count == 0 && DateTime.UtcNow < deadline) { clock = DateTime.Now; Sleep(0.05); }
            C(posted.Count >= 1, $"all-mode produces a webhook POST ({posted.Count})");
            if (posted.Count >= 1)
            {
                var body = posted[0];
                C(body.Contains("\"type\":\"voice\"") || body.Contains("\"kind\":\"voice\""), "payload marked voice");
                C(body.Contains("Bodhidharma") || body.Contains("welcome to the history"), "batched text present");
                C(body.Contains("parcel voice"), "channel field present");
            }

            // name mode: no mention -> no wake; mention -> wake with context
            posted.Clear();
            VoiceWakeReset(); VoiceDebounceS = 0.2; VoiceMinWakeS = 0.5; VoiceContextS = 30;
            clock = DateTime.Now; vNow = () => clock;
            VWakeMode = VoiceWakeMode.Name;
            VoiceNoteLine("Shi Wayne", "11111111-1111-1111-1111-111111111111", "today we study Bodhidharma", "Sangha", "parcel voice");
            clock = clock.AddSeconds(2);
            VoiceNoteLine("Visitor", "22222222-2222-2222-2222-222222222222", "interesting point about zen", "Sangha", "parcel voice");
            clock = clock.AddSeconds(2);
            Sleep(0.6);
            C(posted.Count == 0, "name mode: no wake without a mention");
            VoiceNoteLine("David Nightingale", "44ce5a36-c1c7-4a68-ac9a-635ddfff6233", "Galatea what do you think?", "Sangha", "parcel voice");
            deadline = DateTime.UtcNow.AddSeconds(6);
            while (posted.Count == 0 && DateTime.UtcNow < deadline) { clock = DateTime.Now; Sleep(0.05); }
            C(posted.Count >= 1, $"name mode wakes on mention ({posted.Count})");
            if (posted.Count >= 1)
            {
                var body = posted[0];
                C(body.Contains("Galatea what do you think"), "mention text in event");
                C(body.Contains("\"context\":") && (body.Contains("Bodhidharma") || body.Contains("interesting point")), "prior ~30 s context included");
                C(body.Contains("\"trigger\":\"name\""), "trigger=name on mention wake");
                C(body.Contains("\"line\":\"Galatea what do you think?\""), "exact triggering line in payload");
            }

            // name mode: David's plain line wakes with trigger=david
            posted.Clear();
            VoiceWakeReset(); VoiceDebounceS = 0.2; VoiceMinWakeS = 0.5; VoiceContextS = 30;
            clock = DateTime.Now; vNow = () => clock;
            VWakeMode = VoiceWakeMode.Name;
            VoiceNoteLine("Visitor", "22222222-2222-2222-2222-222222222222", "nice house you have", "Sangha", "parcel voice");
            Sleep(0.5);
            C(posted.Count == 0, "name mode: visitor plain line does not wake");
            VoiceNoteLine("David Nightingale", "44ce5a36-c1c7-4a68-ac9a-635ddfff6233", "you're looking good today", "Sangha", "parcel voice");
            deadline = DateTime.UtcNow.AddSeconds(6);
            while (posted.Count == 0 && DateTime.UtcNow < deadline) { clock = DateTime.Now; Sleep(0.05); }
            C(posted.Count >= 1 && posted[0].Contains("\"trigger\":\"david\"") && posted[0].Contains("looking good today"), $"name mode: David plain line wakes with trigger=david ({posted.Count})");

            // noise from David never wakes, even though every David line normally does
            posted.Clear();
            VoiceWakeReset(); VoiceDebounceS = 0.2; VoiceMinWakeS = 0.5;
            clock = DateTime.Now; vNow = () => clock;
            VWakeMode = VoiceWakeMode.Name;
            VoiceNoteLine("David Nightingale", "44ce5a36-c1c7-4a68-ac9a-635ddfff6233", ".", "Sangha", "parcel voice");
            VWakeMode = VoiceWakeMode.All;
            VoiceNoteLine("Visitor", "22222222-2222-2222-2222-222222222222", "...", "Sangha", "parcel voice");
            Sleep(0.6);
            C(posted.Count == 0, "noise-only lines never wake (name or all mode)");
            VoiceNoteLine("David Nightingale", "44ce5a36-c1c7-4a68-ac9a-635ddfff6233", ".", "Sangha", "parcel voice", 0.4);
            VoiceNoteLine("Visitor", "22222222-2222-2222-2222-222222222222", "", "Sangha", "parcel voice", 2.0);
            Sleep(0.6);
            C(posted.Count == 0, "short David blip and long visitor noise never wake");
            VWakeMode = VoiceWakeMode.Name;
            VoiceNoteLine("David Nightingale", "44ce5a36-c1c7-4a68-ac9a-635ddfff6233", ".", "Sangha", "parcel voice", 1.4);
            deadline = DateTime.UtcNow.AddSeconds(6);
            while (posted.Count == 0 && DateTime.UtcNow < deadline) { clock = DateTime.Now; Sleep(0.05); }
            C(posted.Count >= 1 && posted[0].Contains("\"trigger\":\"unclear\""), $"David real-speech '.' wakes with trigger=unclear ({posted.Count})");

            // name mode: open invitation wakes with context + trigger=invitation
            posted.Clear();
            VoiceWakeReset(); VoiceDebounceS = 0.2; VoiceMinWakeS = 0.5; VoiceContextS = 30;
            clock = DateTime.Now; vNow = () => clock;
            VWakeMode = VoiceWakeMode.Name;
            VoiceNoteLine("Shi Wayne", "11111111-1111-1111-1111-111111111111", "Bodhidharma sat facing a wall for nine years", "Sangha", "parcel voice");
            clock = clock.AddSeconds(3);
            VoiceNoteLine("Shi Wayne", "11111111-1111-1111-1111-111111111111", "questions or comments?", "Sangha", "parcel voice");
            deadline = DateTime.UtcNow.AddSeconds(6);
            while (posted.Count == 0 && DateTime.UtcNow < deadline) { clock = DateTime.Now; Sleep(0.05); }
            C(posted.Count >= 1, $"name mode wakes on invitation ({posted.Count})");
            if (posted.Count >= 1)
            {
                var body = posted[0];
                C(body.Contains("questions or comments"), "invitation text in event");
                C(body.Contains("\"trigger\":\"invitation\""), "trigger=invitation on open floor");
                C(body.Contains("\"context\":") && body.Contains("Bodhidharma"), "invitation includes prior context");
            }

            // rate limit
            posted.Clear();
            VoiceWakeReset(); VoiceDebounceS = 0.15; VoiceMinWakeS = 1.0;
            clock = DateTime.Now; vNow = () => clock;
            VWakeMode = VoiceWakeMode.All;
            VoiceNoteLine("A", "11111111-1111-1111-1111-111111111111", "first", "P", "voice");
            deadline = DateTime.UtcNow.AddSeconds(4);
            while (posted.Count < 1 && DateTime.UtcNow < deadline) { clock = DateTime.Now; Sleep(0.05); }
            var n1 = posted.Count;
            C(n1 >= 1, "first wake posted");
            VoiceNoteLine("A", "11111111-1111-1111-1111-111111111111", "second soon", "P", "voice");
            Sleep(0.3);
            C(posted.Count == n1, "rate limit holds the second wake briefly");
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (posted.Count < n1 + 1 && DateTime.UtcNow < deadline) { clock = DateTime.Now; Sleep(0.05); }
            C(posted.Count >= n1 + 1, $"second wake after min interval ({posted.Count})");

            // off
            posted.Clear();
            VoiceWakeReset(); VoiceDebounceS = 0.15;
            clock = DateTime.Now; vNow = () => clock;
            VWakeMode = VoiceWakeMode.Off;
            VoiceNoteLine("A", "11111111-1111-1111-1111-111111111111", "Galatea hello", "P", "voice");
            Sleep(0.5);
            C(posted.Count == 0, "wake off never POSTs");
        }
        catch (Exception ex) { C(false, "exception: " + ex); }
        finally
        {
            GalatayMcp.Webhook.PostOverride = keepPost;
            GalatayMcp.Webhook.Detect = keepDetect; GalatayMcp.Webhook.Quiet = keepQuiet;
            GalatayMcp.Webhook.MaxHold = keepMaxHold; GalatayMcp.Webhook.MinInterval = keepMinWh;
            vNow = keepNow;
            VoiceDebounceS = keepDeb; VoiceMinWakeS = keepMin; VoiceContextS = keepCtx;
            VoiceWakeReset();
            VWakeMode = keepMode;
        }
        return sb.ToString().TrimEnd() + $"\nvoice wake selftest: {fail} FAIL";
    }


    // ---- offline selftest: real broker code + real sidecar against fake_voice_server.py (no SL login) -----------
    static async Task<string> VoiceSelfTest()
    {
        var sb = new StringBuilder(); int fail = 0;
        void Check(bool ok, string what) { sb.AppendLine((ok ? "ok   " : "FAIL ") + what); if (!ok) fail++; }
        var offer = "v=0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 96\r\na=mid:0\r\na=candidate:1 1 udp 2130706431 10.0.0.2 5000 typ host\r\na=candidate:2 1 udp 1694498815 1.2.3.4 6000 typ srflx raddr 10.0.0.2 rport 5000\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\na=mid:1\r\na=candidate:1 1 udp 2130706431 10.0.0.2 5000 typ host\r\n";
        var c = VoiceOfferCandidates(offer);
        Check(c.Count == 2 && ((OSDMap)c[0])["sdpMid"].AsString() == "0" && ((OSDMap)c[1])["candidate"].AsString().StartsWith("candidate:2 "), $"offer candidates -> VoiceSignalingRequest list ({c.Count})");
        var pos = JsonSerializer.Serialize(VoicePosObj(new Vector3d(256000.5, 256128.25, 30), Quaternion.Identity));
        Check(pos.Contains("\"sp\":[25600050,25612825,3100]") && pos.Contains("\"sh\":[0,0,0,100]"), "position in cm, head +1 m, rotation x100: " + pos);

        var serverScript = Path.Combine(Path.GetDirectoryName(VoiceScript)!, "fake_voice_server.py");
        if (!File.Exists(serverScript) || !File.Exists(VoicePy)) { Check(false, $"sidecar installed ({VoicePy}, {serverScript})"); return sb + $"{fail} FAIL"; }
        var psi = new ProcessStartInfo(VoicePy) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add(serverScript); psi.ArgumentList.Add("--serve");
        using var fake = Process.Start(psi)!;
        var hello = JsonNode.Parse(await fake.StandardOutput.ReadLineAsync() ?? "{}");
        var port = (int?)hello?["port"] ?? 0; var speaker = UUID.Parse((string)hello?["speaker"] ?? UUID.Zero.ToString());
        Check(port > 0, $"fake voice server on 127.0.0.1:{port}");
        var baseUri = $"http://127.0.0.1:{port}";
        vCapsOverride = (new Uri(baseUri + "/provision"), new Uri(baseUri + "/signal"));
        var keepDir = VoiceDir; VoiceDir = Path.Combine(Path.GetTempPath(), "voice-selftest-" + DateTime.Now.ToString("HHmmss"));
        Remember("Test Speaker", speaker);
        try
        {
            vWanted = true;
            var r = await VoiceCmd(new[] { "on" });
            Check(r.StartsWith("voice: joining"), "voice on -> " + r);
            var deadline = DateTime.Now.AddSeconds(150);
            while (DateTime.Now < deadline && (vCur?.LastLine == null || !vCur.LastLine.Contains("country", StringComparison.OrdinalIgnoreCase))) await Task.Delay(500);
            var s = vCur;
            Check(s?.Pc == "connected" && s.Dc == "open", $"WebRTC connected, data channel open ({s?.Pc}/{s?.Dc})");
            Check(s?.LastLine?.Contains("Test Speaker: ") == true && s.LastLine.Contains("country", StringComparison.OrdinalIgnoreCase), "transcript line with the speaker's name: " + s?.LastLine);
            var st = VoiceStatus();
            Check(st.Contains("Test Speaker") && st.Contains("frames received"), "voice status shows the participant + audio");
            Check(VoiceTail(5).Contains("**Test Speaker:**"), "voice tail reads the transcript file");
            var off = await VoiceCmd(new[] { "off" });
            Check(off.StartsWith("voice off"), "voice off -> " + off);
            using var http = new HttpClient();
            var rep = JsonNode.Parse(await http.GetStringAsync(baseUri + "/report"));
            var prov = rep?["provision"]?[0];
            Check((string)prov?["channel_type"] == "local" && (string)prov?["voice_server_type"] == "webrtc" && (int?)prov?["parcel_local_id"] == 7 && (bool?)prov?["opus_stereo"] == true,
                  "ProvisionVoiceAccountRequest body: " + prov?.ToJsonString());
            var sig = rep?["signal"] as JsonArray;
            Check(sig?.Any(x => x?["candidates"] is JsonArray) == true && sig.Any(x => (bool?)x?["candidate"]?["completed"] == true), $"VoiceSignalingRequest: candidates + completed ({sig?.Count} posts)");
            Check((rep?["logout"] as JsonArray)?.Count >= 1, "logout POST on voice off");
            Check((rep?["dc_in"] as JsonArray)?.Any(x => (string)x == "{\"j\":{\"p\":true}}") == true, "data channel join {\"j\":{\"p\":true}} sent");
        }
        catch (Exception ex) { Check(false, "exception: " + ex.GetBaseException().Message); }
        finally
        {
            vWanted = false; vCapsOverride = null; VoiceDir = keepDir;
            try { fake.StandardInput.Close(); if (!fake.WaitForExit(5000)) fake.Kill(); } catch { }
        }
        return sb + $"voice selftest: {fail} FAIL";
    }
}
