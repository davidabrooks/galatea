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
// Commands: voice on | voice off | voice status | voice tail [n]       offline check: galatay-text --voice-selftest
// ponytail: primary region only (no neighbour-region connections like the LL estate session); ceiling = speakers across
// a region border are not heard.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
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
            default: return "usage: voice on | voice off | voice status | voice tail [n]";
        }
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
                        s.Lines++; s.LastLine = $"{m["time"]} {m["speaker"]}: {m["text"]}";
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
