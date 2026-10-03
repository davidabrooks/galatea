// galatay-text: headless Second Life text client built on LibreMetaverse.
// - Reads the password in-process from a JSON secrets file (never argv, never logged).
// - Logs chat/IM/events to a log file (local time).
// - Accepts one-line commands on a Unix domain socket (mode 600); see "help".
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LibreMetaverse;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;

namespace GalatayText;

public static partial class Program
{
    static string Env(string k, string d) => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(k)) ? d : Environment.GetEnvironmentVariable(k)!;

    static readonly string First = Env("GT_FIRST", "Galatay");
    static readonly string Last = Env("GT_LAST", "Resident");
    static readonly string Start = Env("GT_START", "last");
    static readonly string LoginUri = Env("GT_LOGIN_URI", Settings.AgniLoginServer);
    static readonly string LogPath = Env("GT_LOG", "/workspace/secondlife/textclient.log");
    static readonly string SockPath = Env("GT_SOCK", "/home/box/viewers/textclient/run/galatay.sock");
    static readonly string SecretsPath = Env("GT_SECRETS", "/home/box/agent-data/box-secrets.json");
    static readonly string SecretKey = Env("GT_SECRET_KEY", "card.SECONDLIFE_PASSWORD");
    static readonly string[] LureAllow = Env("GT_LURE_ALLOW", "David Nightingale,SophieJeanneLaDouce Resident,d695b17a-6504-4697-a945-0b71c53e4771").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    const string Channel = "GalatayText";
    const string Version = "0.1.0.0";

    static GridClient client;
    static readonly object logLock = new();
    static readonly DateTime started = DateTime.Now;
    static readonly Dictionary<string, UUID> nameToId = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<UUID, string> idToName = new();
    static bool autoLure = Env("GT_AUTO_LURE", "1") != "0";
    static (UUID from, string name, UUID session, string msg)? pendingLure;
    static ScriptDialogEventArgs lastDialog;
    static UUID followId = UUID.Zero; static string followName = "";
    static volatile bool shuttingDown;
    static readonly CancellationTokenSource cts = new();

    public static bool StderrLog;          // MCP mode: stdout is reserved for the protocol
    public static bool ExitOnLogout = true; // daemon mode: process ends after logout
    public record EventItem(string time, string kind, string text);
    public static readonly ConcurrentQueue<EventItem> Events = new();
    static readonly HashSet<string> NonEvents = new() { "greet-reply", "cmd", "ready", "webhook", "profile", "height", "muted-drop", "mute" };
    // Incoming avatar chat/IM notification (MCP webhook wake-up). Invoked AFTER the event is queued for poll_events.
    public record IncomingChat(string type, string from, string from_id, string text, string time, double? distance, string region);
    public static Action<IncomingChat> OnIncoming;
    static void Notify(string type, string from, UUID fromId, string text, double? dist)
    {
        var h = OnIncoming; if (h == null) return;
        // 2026-09-30 token saving (David): while nearby-quiet.txt exists, local chat only wakes the webhook if it is from David or names Galatea
        if (type == "local_chat" && System.IO.File.Exists("/home/box/viewers/textclient/nearby-quiet.txt") && fromId.ToString() != "44ce5a36-c1c7-4a68-ac9a-635ddfff6233" && (text ?? "").IndexOf("galat", StringComparison.OrdinalIgnoreCase) < 0) return;
        try { h(new IncomingChat(type, from, fromId.ToString(), text, DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture), dist, client?.Network?.CurrentSim?.Name)); }
        catch (Exception ex) { Log("webhook", "notify error: " + ex.GetType().Name); }
    }
    public static string RegionName => client?.Network?.CurrentSim?.Name;
    static readonly Regex BridgeAuth = new("<bridgeAuth>[^<]*</bridgeAuth>", RegexOptions.Compiled);
    static readonly string LockPath = Path.Combine(Path.GetDirectoryName(SockPath)!, "sl-session.pid");

    public static void Log(string kind, string msg)
    {
        msg = BridgeAuth.Replace(msg, "<bridgeAuth>***</bridgeAuth>");
        var now = DateTime.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss} [{kind}] {msg}";
        lock (logLock)
        {
            try { File.AppendAllText(LogPath, line + "\n"); } catch { }
            (StderrLog ? Console.Error : Console.Out).WriteLine(line);
        }
        if (!NonEvents.Contains(kind))
        {
            Events.Enqueue(new EventItem(now.ToString("yyyy-MM-dd HH:mm:ss"), kind, msg));
            while (Events.Count > 2000) Events.TryDequeue(out _);
        }
    }

    // ---- session guard (never kick Firestorm or another galatay client) ----
    static bool ProcAlive(int pid)
    {
        try { return File.ReadAllText($"/proc/{pid}/comm").Trim().StartsWith("galatay"); } catch { return false; }
    }
    public static string SessionGuard()
    {
        try
        {
            var psi = new ProcessStartInfo("pgrep") { RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("-x"); psi.ArgumentList.Add("do-not-directly");
            using var pr = Process.Start(psi)!; pr.WaitForExit(5000);
            if (pr.ExitCode == 0) return "Firestorm is running (same account would be kicked); refusing to log in";
        }
        catch { }
        try
        {
            if (File.Exists(LockPath) && int.TryParse(File.ReadAllText(LockPath).Trim(), out var pid) && pid != Environment.ProcessId && ProcAlive(pid))
                return $"another galatay client (pid {pid}) is logged in; log it out first";
        }
        catch { }
        return null;
    }
    public static bool LoggedIn => client?.Network?.Connected == true;

    // ---- send-rate guard (keep speech deliberate; SL cap is 5000 msgs/day) ----
    static readonly Queue<DateTime> sentTimes = new(); static int sentToday; static DateTime sentDay = DateTime.Today;
    static string RateGuard()
    {
        lock (sentTimes)
        {
            if (DateTime.Today != sentDay) { sentDay = DateTime.Today; sentToday = 0; }
            while (sentTimes.Count > 0 && (DateTime.Now - sentTimes.Peek()).TotalSeconds > 60) sentTimes.Dequeue();
            if (sentTimes.Count >= 8) return "rate limit: max 8 messages per minute";
            if (sentToday >= 500) return "rate limit: max 500 messages per day (self-imposed)";
            sentTimes.Enqueue(DateTime.Now); sentToday++;
            return null;
        }
    }

    // ---- secrets -------------------------------------------------------
    static string ReadPassword()
    {
        var env = Environment.GetEnvironmentVariable("SECONDLIFE_PASSWORD");
        if (!string.IsNullOrEmpty(env)) { Environment.SetEnvironmentVariable("SECONDLIFE_PASSWORD", null); return env; }
        using var doc = JsonDocument.Parse(File.ReadAllText(SecretsPath));
        JsonElement cur = doc.RootElement;
        bool ok = true;
        foreach (var part in SecretKey.Split('.'))
        {
            if (cur.ValueKind == JsonValueKind.Object && cur.TryGetProperty(part, out var nx)) cur = nx; else { ok = false; break; }
        }
        if (ok && cur.ValueKind == JsonValueKind.String) return cur.GetString() ?? "";
        // fallback: recursive search for the last key segment
        var leaf = SecretKey.Split('.').Last();
        string found = null;
        void Walk(JsonElement e)
        {
            if (found != null) return;
            if (e.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == leaf && p.Value.ValueKind == JsonValueKind.String) { found = p.Value.GetString(); return; }
                    Walk(p.Value);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) Walk(x);
        }
        Walk(doc.RootElement);
        return found ?? "";
    }

    // ---- main ----------------------------------------------------------
    public static async Task<int> Main(string[] args)
    {
        Settings.LogLevel = Microsoft.Extensions.Logging.LogLevel.Warning;
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(SockPath)!);

        if (args.Contains("--check"))
        {
            string pw = "";
            try { pw = ReadPassword(); } catch (Exception ex) { Console.WriteLine("secrets read FAILED: " + ex.GetType().Name); return 1; }
            Console.WriteLine($"account: {First} {Last}; start: {Start}; login uri: {LoginUri}");
            Console.WriteLine($"channel/version: {Channel} {Version}; LibreMetaverse: {typeof(GridClient).Assembly.GetName().Version}");
            Console.WriteLine($"log: {LogPath}; socket: {SockPath}; lure allow: {string.Join(", ", LureAllow)}");
            Console.WriteLine($"password read OK: length {pw.Length} (value not shown)");
            try { Console.WriteLine("selftest: platform " + NetworkManager.GetPlatformVersion() + ", md5 len " + Utils.MD5("x").Length); } catch (Exception ex) { var b = ex.GetBaseException(); Console.WriteLine($"selftest FAILED: {b.GetType().Name}: {b.Message}\n{b.StackTrace}"); }
            pw = null;
            return pw == null ? 0 : 1;
        }
        if (args.Length > 0) { Console.Error.WriteLine("usage: galatay-text [--check]  (config via GT_* env vars; no secrets on the command line)"); return 2; }

        LoadLocalMutes(); // muted avatars are dropped from the first packet on (MuteGuard.cs)
        GalatayMcp.Webhook.Init(); // chat/IM wake-up push (silent no-op until URL + key exist)
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; _ = Task.Run(() => Shutdown("SIGTERM")); });
        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; _ = Task.Run(() => Shutdown("SIGINT")); });
        var res = await LoginAsync();
        if (!res.StartsWith("OK")) { Log("exit", $"login failed ({res}); exit {ExitRelaunch} (supervisor retries with backoff)"); return ExitRelaunch; }
        _ = Task.Run(HeartbeatLoop); StartWatchdog(); // Watchdog.cs
        _ = Task.Run(WanderAutoResume); // resumes if wander-state.json is on, unless stale after a deliberate stop (Wander.cs WanderResumeDecision)
        _ = Task.Run(QuietLoop); // session detector / quiet mode every 10 s (Quiet.cs)

        var server = Task.Run(SocketServer);
        try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (TaskCanceledException) { }
        try { File.Delete(SockPath); } catch { }
        Log("exit", $"process exiting (code {ExitCode})");
        return ExitCode;
    }


    public static async Task<string> LoginAsync()
    {
        if (LoggedIn) return "OK already logged in";
        var guard = SessionGuard();
        if (guard != null) { Log("login", "refused: " + guard); return "REFUSED: " + guard; }
        shuttingDown = false;
        client = new GridClient();
        client.Settings.World.StoreLandPatches = true; // terrain heights for walking (NavWalk.cs)
        client.Settings.Connection.MfaEnabled = true;
        // (2026-09-26 12:58, David) login outfit re-send: off (default) = leave the sim's attachments exactly as they are;
        // dedupe = re-send each COF object once, never detach-all; add-missing = dedupe + only objects not yet attached; legacy = old library behaviour (detach-all + every object twice)
        // 13:00: 'off' left the bracelet + earrings (2nd/3rd object on RightHand) off: the sim only restores one object per point,
        // so the default is now 'add-missing': wait for attachments to settle, then add only COF objects not attached, no detach-all
        var outfitMode = Env("GT_OUTFIT_RESEND", "add-missing").Trim().ToLowerInvariant();
        client.Settings.Agent.SendOutfitAfterBake = outfitMode != "off";
        client.Settings.Agent.OutfitSendDetachAll = outfitMode == "legacy";
        client.Settings.Agent.OutfitSendSkipAttachedSettleMs = outfitMode == "add-missing" ? 15000 : 0;
        // 13:03 (David): the Firestorm LSL Bridge is only for Firestorm (which re-adds it itself): never re-attach it from the text client.
        // The COF link stays as it is. Comma-separated name prefixes, GT_OUTFIT_KEEP_OFF.
        var keepOff = Env("GT_OUTFIT_KEEP_OFF", "#Firestorm LSL Bridge").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        client.Settings.Agent.OutfitSendExclude = (id, name) => keepOff.Any(k => name.StartsWith(k, StringComparison.OrdinalIgnoreCase));
        Log("appearance", $"login outfit send keeps off: {string.Join(", ", keepOff)}");
        Log("appearance", $"login outfit re-send mode: {outfitMode} (SendOutfitAfterBake={client.Settings.Agent.SendOutfitAfterBake}, detach-all={client.Settings.Agent.OutfitSendDetachAll})");
        client.Throttle.Wind = 0; client.Throttle.Cloud = 0;
        client.Throttle.Land = 200000; client.Throttle.Task = 1000000; client.Throttle.Texture = 50000; client.Throttle.Asset = 100000;
        Hook();
        HookAttachWatch(); // own AvatarAnimation watch with sources (AttachWatch.cs)
        HookGroups(); // current-groups cache + JoinGroupReply log (GroupPicks.cs)
        HookRestart(); // region restart warnings -> evacuate + return (RegionRestart.cs)
        HookWatchdog(); // packet-arrival stamp for the stale-connection check (Watchdog.cs)
        HookQuiet(); // other avatars' ground-sit animations for the session detector (Quiet.cs)
        string password;
        try { password = ReadPassword(); } catch (Exception ex) { Log("error", "cannot read secrets file: " + ex.GetType().Name); return "FAILED: cannot read secrets file"; }
        if (string.IsNullOrEmpty(password)) { Log("error", "password empty; refusing to log in"); return "FAILED: password empty"; }
        var lp = client.Network.DefaultLoginParams(First, Last, password, Channel, Version);
        password = null;
        lp.Start = startOverride ?? Start; lp.URI = LoginUri; startOverride = null;
        Log("login", $"logging in {First} {Last} to '{lp.Start}' via {LoginUri} (channel {Channel} {Version})");
        var sw = Stopwatch.StartNew();
        bool ok;
        try { ok = await client.Network.LoginAsync(lp); }
        catch (Exception ex) { var b = ex.GetBaseException(); Log("error", $"login exception: {ex.Message} / {b.GetType().Name}: {b.Message}"); return "FAILED: " + b.Message; }
        lp.Password = "";
        if (!ok)
        {
            Log("login", $"FAILED after {sw.Elapsed.TotalSeconds:F1}s: {client.Network.LoginMessage}");
            return "FAILED: " + client.Network.LoginMessage;
        }
        try { File.WriteAllText(LockPath, Environment.ProcessId.ToString()); } catch { }
        await Set360();
        var msg = $"OK in {sw.Elapsed.TotalSeconds:F1}s: region {client.Network.CurrentSim?.Name} pos {Fmt(client.Self.SimPosition)} agent {client.Self.AgentID}";
        Log("login", msg);
        if (tickerTask == null) tickerTask = Task.Run(Ticker);
        _ = Task.Run(AfterLoginHeight); // pin hover + verify appearance/size (HeightGuard.cs)
        animLogUntil = DateTime.Now.AddMinutes(10);
        _ = Task.Run(() => RefreshMutes(20000));
        _ = Task.Run(FetchOfflineIms); // IMs stored while logged out / session dead (OfflineIm.cs)
        if (attachBlockTask == null) attachBlockTask = Task.Run(SeatAttachLoop); // seat-off attachments (AttachWatch.cs)
        GC.Collect();
        return msg;
    }
    static Task tickerTask;
    static Task attachBlockTask;
    // Default SL interest list is frustum-based (objects behind the camera are not sent); a headless
    // client wants everything around it, like Firestorm's 360 capture.
    static async Task Set360()
    {
        try { await client.InterestList.SetModeAsync(LibreMetaverse.Messages.Linden.InterestListMode.Panoramic360); }
        catch (Exception ex) { Log("warn", "360 interest list failed: " + ex.GetBaseException().Message); }
    }

    // deliberate-stop marker (2026-09-26): written by text-galatay.sh stop (and by a logout command in the daemon), cleared
    // only by a manual text-galatay.sh start; galatay-autostart.sh does nothing while it exists. run/stop-requested is the
    // supervisor's own stop flag (also written by stop).
    static readonly string StopMarkerPath = Env("GT_STOP_MARKER", "/home/box/viewers/textclient/run/deliberate-stop");
    static readonly string StopRequestPath = Env("GT_STOP_REQUEST", "/home/box/viewers/textclient/run/stop-requested");
    static bool DeliberateStopRequested() { try { return File.Exists(StopMarkerPath) || File.Exists(StopRequestPath); } catch { return false; } }
    static void MarkDeliberateStop(string why)
    {
        try { if (!File.Exists(StopMarkerPath)) File.WriteAllText(StopMarkerPath, $"stopped deliberately at {DateTime.Now:yyyy-MM-dd HH:mm:ss} by the client ({why})\n"); }
        catch (Exception ex) { Log("warn", "deliberate-stop marker write failed: " + ex.Message); }
    }

    public static async Task Shutdown(string why)
    {
        if (shuttingDown) return; shuttingDown = true;
        bool deliberate = why == "logout command" || DeliberateStopRequested();
        // 2026-09-26: a signal without a stop request (hang monitor's TERM, a stray kill) exits 75 so the supervisor relaunches
        ExitCode = ShutdownExitCode(why, deliberate, ExitCode);
        try { WanderOnShutdown(deliberate); } catch { }
        if (deliberate && ExitOnLogout) MarkDeliberateStop(why); // daemon only: a logout command also keeps her down across a reboot
        Log("logout", $"logging out ({why}{(deliberate ? "; deliberate stop" : "")})");
        try
        {
            if (client?.Network?.Connected == true)
            {
                var t = Task.Run(() => client.Network.Logout());
                await Task.WhenAny(t, Task.Delay(15000));
            }
        }
        catch (Exception ex) { Log("error", "logout: " + ex.Message); }
        try { if (File.Exists(LockPath) && File.ReadAllText(LockPath).Trim() == Environment.ProcessId.ToString()) File.Delete(LockPath); } catch { }
        followId = UUID.Zero;
        if (ExitOnLogout) cts.Cancel();
    }

    // ---- events --------------------------------------------------------
    static void Remember(string name, UUID id)
    {
        if (string.IsNullOrWhiteSpace(name) || id == UUID.Zero) return;
        lock (nameToId) { nameToId[name] = id; idToName[id] = name; }
    }
    static string NameOf(UUID id)
    {
        lock (nameToId) if (idToName.TryGetValue(id, out var n)) return n;
        return id.ToString();
    }

    // allow-list entries are names (First Last / first.last) or avatar UUIDs
    // SL system notices that arrive as agent IMs ("User not online - message will be stored...", sender "Second Life",
    // null sender, busy/away auto-responses): logged as [im-system], not forwarded to the webhook
    static readonly Regex SystemImText = new(@"^(User not online - message will be stored|.{0,80}\b(is (away|busy|in do not disturb)|auto-?respon))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static bool SystemImNotice(InstantMessage im) =>
        im.FromAgentID == UUID.Zero || string.Equals(im.FromAgentName?.Trim(), "Second Life", StringComparison.OrdinalIgnoreCase)
        || im.Dialog == InstantMessageDialog.BusyAutoResponse || SystemImText.IsMatch(im.Message ?? "");
    static bool LureAllowed(string name, UUID id = default) =>
        LureAllow.Any(a => (id != UUID.Zero && UUID.TryParse(a, out var u) && u == id) ||
                           string.Equals(a, name, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(a.Replace(' ', '.'), name, StringComparison.OrdinalIgnoreCase));

    static void Hook()
    {
        client.Self.ChatFromSimulator += (s, e) =>
        {
            if (e.Type is ChatType.StartTyping or ChatType.StopTyping or ChatType.Debug) return;
            if (string.IsNullOrEmpty(e.Message)) return;
            if (e.SourceType == ChatSourceType.Agent) Remember(e.FromName, e.SourceID);
            if (e.SourceType == ChatSourceType.Agent ? MutedDrop(e.SourceID, e.FromName, "chat") : MutedDrop(e.OwnerID, NameOf(e.OwnerID), "object chat")) return;
            if (e.SourceType == ChatSourceType.Agent && e.SourceID != UUID.Zero && e.SourceID != client.Self.AgentID
                && e.Type is ChatType.Normal or ChatType.Whisper or ChatType.Shout)
            {   // 2026-09-27 (David): a short acknowledgement of her greeting is not a conversation: no pause, no approach, no webhook
                var (ack, why) = ShortGreetReply(e.Message, GreetedAt(e.SourceID), DateTime.Now, InChatConversation);
                if (ack)
                {
                    Log("greet-reply", $"[short greeting reply, ignored] {e.FromName}: {e.Message} ({why})");
                    WanderShortReply(e.SourceID, e.FromName);
                    return;
                }
            }
            var src = e.SourceType == ChatSourceType.Agent ? "" : e.SourceType == ChatSourceType.Object ? " (object)" : " (system)";
            var t = e.Type == ChatType.Normal ? "" : $" {e.Type.ToString().ToLowerInvariant()}";
            Log("chat", $"{e.FromName}{src}{t}: {e.Message}");
            if (e.SourceType == ChatSourceType.Agent && e.SourceID != UUID.Zero && e.SourceID != client.Self.AgentID
                && e.Type is ChatType.Normal or ChatType.Whisper or ChatType.Shout && !string.IsNullOrWhiteSpace(e.Message))
            {
                double? dist = null;
                try { if (e.Position != Vector3.Zero) dist = Math.Round(Vector3.Distance(e.Position, client.Self.SimPosition), 1); } catch { }
                Notify("local_chat", e.FromName, e.SourceID, e.Message, dist);
                WanderChatIn(e.SourceID, e.FromName, e.Position, false);
            }
        };
        client.Self.IM += (s, e) => HandleIm(e.IM);
        client.Self.AlertMessage += (s, e) => Log("alert", e.Message);
        HookRest();
    }

    // Every incoming IM, live or stored-offline (OfflineIm.cs feeds the ReadOfflineMsgs reply through here too).
    static void HandleIm(InstantMessage im)
    {
            bool offline = IsOfflineIm(im);
            string offTag = offline ? $" [offline, {OfflineSentText(im)}]" : "";
            if (offline && im.Dialog is not (InstantMessageDialog.StartTyping or InstantMessageDialog.StopTyping)) Interlocked.Increment(ref offlineImCount);
            if (im.FromAgentID != UUID.Zero && !string.IsNullOrEmpty(im.FromAgentName) && im.Dialog != InstantMessageDialog.MessageFromObject)
                Remember(im.FromAgentName, im.FromAgentID);
            if (im.Dialog is not (InstantMessageDialog.StartTyping or InstantMessageDialog.StopTyping) && MutedDrop(im.FromAgentID, im.FromAgentName, im.Dialog.ToString())) return;
            switch (im.Dialog)
            {
                case InstantMessageDialog.StartTyping:
                case InstantMessageDialog.StopTyping:
                    return;
                case InstantMessageDialog.MessageFromAgent:
                    if (string.IsNullOrEmpty(im.Message)) return;
                    if (SystemImNotice(im)) { Log("im-system", $"{im.FromAgentName} ({im.FromAgentID}): {im.Message}"); return; } // logged only, never to the webhook
                    Log("im", $"{im.FromAgentName} ({im.FromAgentID}){offTag}: {im.Message}");
                    if (im.FromAgentID != UUID.Zero && im.FromAgentID != client.Self.AgentID && !im.GroupIM && !string.IsNullOrWhiteSpace(im.Message))
                    {
                        double? dist = null;
                        try
                        {
                            var sim = client.Network.CurrentSim;
                            var av = sim?.ObjectsAvatars.Values.FirstOrDefault(x => x != null && x.ID == im.FromAgentID);
                            if (av != null && (av.ParentID == 0 || sim.ObjectsPrimitives.ContainsKey(av.ParentID)))
                                dist = Math.Round(Vector3.Distance(PositionHelper.GetAvatarPosition(sim, av), client.Self.SimPosition), 1);
                        }
                        catch { }
                        Notify("im", im.FromAgentName, im.FromAgentID, offline ? $"[offline IM, {OfflineSentText(im)}] {im.Message}" : im.Message, offline ? null : dist);
                        if (ImAutoReact(im)) WanderChatIn(im.FromAgentID, im.FromAgentName, Vector3.Zero, true); // old messages: no wander pause/approach/reply
                    }
                    return;
                case InstantMessageDialog.MessageFromObject:
                    Log("objim", $"{im.FromAgentName}{offTag}: {im.Message}"); return;
                case InstantMessageDialog.SessionSend:
                    Log("groupim", $"{im.FromAgentName} (session {im.IMSessionID}){offTag}: {im.Message}"); return;
                case InstantMessageDialog.RequestTeleport:
                    if (!LureMayAutoAccept(im)) { Log("lure", $"stored teleport offer from {im.FromAgentName}{offTag} ('{im.Message}'): IGNORED (old)"); return; }
                    Notify("teleport_offer", im.FromAgentName, im.FromAgentID, $"teleport offer from {im.FromAgentName}: '{im.Message}' ({(LureAllowed(im.FromAgentName, im.FromAgentID) ? (autoLure ? "allow-listed: auto-accept" : "allow-listed: pending") : "NOT allow-listed: ignored")})", null); // urgent webhook (immediate)
                    if (LureAllowed(im.FromAgentName, im.FromAgentID))
                    {
                        pendingLure = (im.FromAgentID, im.FromAgentName, im.IMSessionID, im.Message);
                        if (autoLure && RobeMaybeWorn(out var robeHowL))
                            Log("lure", $"WARNING teleport offer from {im.FromAgentName} ('{im.Message}'): NOT auto-accepted because the VIOLETTE robe is worn ({robeHowL}) and the robe stays at the Buddha Center; held as pending - change to the 'Original' outfit, then 'accept' (or 'decline')");
                        else if (autoLure)
                        {
                            Log("lure", $"teleport offer from {im.FromAgentName} ('{im.Message}'): ACCEPTING (allow-listed)");
                            client.Self.TeleportLureRespond(im.FromAgentID, im.IMSessionID, true);
                            pendingLure = null;
                        }
                        else Log("lure", $"teleport offer from {im.FromAgentName} ('{im.Message}'): pending; send 'accept' to go");
                    }
                    else Log("lure", $"teleport offer from {im.FromAgentName} ('{im.Message}'): IGNORED (not allow-listed)");
                    return;
                case InstantMessageDialog.FriendshipOffered:
                    Log("offer", $"friendship offer from {im.FromAgentName}{offTag}: NOT accepted (pending; see 'offers')"); RecordOffer(im, offline); return;
                case InstantMessageDialog.InventoryOffered:
                case InstantMessageDialog.TaskInventoryOffered:
                    if (!OfferIn(im)) RecordOffer(im, offline); return; // WearOps.cs: logs it; accepts ONLY an armed 'offer allow' from her own named object; else pending (NewCmds.cs 'offers')
                case InstantMessageDialog.GroupInvitation:
                    Log("offer", $"group invitation from {im.FromAgentName}{offTag}: IGNORED"); return;
                default:
                    if (!string.IsNullOrEmpty(im.Message))
                        Log("im-" + im.Dialog, $"{im.FromAgentName}{offTag}: {im.Message}");
                    return;
            }
    }

    static void HookRest()
    {
        client.Self.ScriptDialog += (s, e) => { lastDialog = e; WanderDialogIn(e); Log("dialog", $"'{e.ObjectName}' ({e.FirstName} {e.LastName}) ch {e.Channel}: {e.Message} [buttons: {string.Join(" | ", e.ButtonLabels ?? new())}] (answer with: dialog <button>)"); };
        client.Self.TeleportProgress += (s, e) =>
        {
            if (e.Status is TeleportStatus.Finished or TeleportStatus.Failed or TeleportStatus.Cancelled or TeleportStatus.Start)
                Log("teleport", $"{e.Status}: {e.Message}");
        };
        client.Self.AvatarSitResponse += (s, e) => Log("sit", $"sit response for object {e.ObjectID} (autopilot={e.Autopilot})");
        client.Network.SimChanged += (s, e) => { Log("region", $"now in {client.Network.CurrentSim?.Name}"); if (LoggedIn) { _ = Set360(); _ = Task.Run(async () => { await Task.Delay(3000); await PostHover(PinnedHover(), "region change"); }); } };
        client.Network.Disconnected += (s, e) =>
        {
            Log("disconnect", $"{e.Reason}: {e.Message}");
            try { if (File.Exists(LockPath) && File.ReadAllText(LockPath).Trim() == Environment.ProcessId.ToString()) File.Delete(LockPath); } catch { }
            if (!shuttingDown && MaybeScheduleReconnect(e.Reason, e.Message)) return; // region restart/shutdown -> re-login later (RegionRestart.cs)
            if (!shuttingDown && e.Reason != NetworkManager.DisconnectType.ClientInitiated)
            {   // unexpected (NetworkTimeout, kicked, ...): keep the wander flag, exit 75 so the supervisor logs back in
                try { WanderOnShutdown(); } catch { }
                ExitCode = ExitRelaunch; Log("disconnect", $"unexpected disconnect: exit {ExitRelaunch} for a relaunch by the supervisor");
            }
            if (!shuttingDown) { shuttingDown = true; if (ExitOnLogout) cts.Cancel(); }
        };
        client.Self.ScriptQuestion += (s, e) => Log("perm", $"'{e.ObjectName}' (owner {e.ObjectOwnerName}) asks permissions [{e.Questions}]: IGNORED (never granted; debit is never granted)");
        client.Network.LoggedOut += (s, e) => Log("logout", "logged out by server/client");
        client.Avatars.UUIDNameReply += (s, e) => { foreach (var kv in e.Names) Remember(kv.Value, kv.Key); };
        client.Friends.FriendshipOffered += (s, e) => { /* ignored on purpose (logged via IM handler) */ };
    }

    // ---- periodic work (follow) ---------------------------------------
    static async Task Ticker()
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                if (followId != UUID.Zero && LoggedIn)
                {
                    var sim = client.Network.CurrentSim;
                    var av = sim?.ObjectsAvatars.Values.FirstOrDefault(a => a.ID == followId);
                    if (av != null && sim != null)
                    {
                        var p = PositionHelper.GetAvatarPosition(sim, av);
                        var d = Vector3.Distance(p, client.Self.SimPosition);
                        if (d > 3f)
                        {
                            Utils.LongToUInts(sim.Handle, out var rx, out var ry);
                            client.Self.AutoPilot(p.X + (double)rx, p.Y + (double)ry, p.Z);
                        }
                        else client.Self.AutoPilotCancel();
                    }
                }
            }
            catch { }
            try { await Task.Delay(1000, cts.Token); } catch { }
        }
    }

    // ---- socket server ------------------------------------------------
    static async Task SocketServer()
    {
        try { File.Delete(SockPath); } catch { }
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(SockPath));
        File.SetUnixFileMode(SockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(8);
        Log("ready", $"command socket {SockPath}");
        while (!cts.IsCancellationRequested)
        {
            Socket conn;
            try { conn = await listener.AcceptAsync(cts.Token); } catch { break; }
            _ = Task.Run(async () =>
            {
                using (conn)
                using (var ns = new NetworkStream(conn))
                using (var rd = new StreamReader(ns, Encoding.UTF8))
                using (var wr = new StreamWriter(ns, new UTF8Encoding(false)) { AutoFlush = true })
                {
                    try
                    {
                        var line = await rd.ReadLineAsync();
                        if (line == null) return;
                        string resp;
                        try { resp = await Exec(line.Trim()); }
                        catch (Exception ex) { resp = "error: " + ex.Message; }
                        await wr.WriteAsync(resp.EndsWith('\n') ? resp : resp + "\n");
                    }
                    catch { }
                }
            });
        }
    }


    // ---- structured (JSON-friendly) views for the MCP server ----------
    public static object StatusObj()
    {
        var sim = client?.Network?.CurrentSim;
        using var proc = Process.GetCurrentProcess();
        if (!LoggedIn) return new { logged_in = false, rss_mb = proc.WorkingSet64 / 1048576 };
        var p = client.Self.SimPosition;
        return new
        {
            logged_in = true, region = sim?.Name, pos = new[] { p.X, p.Y, p.Z },
            sitting_on = client.Self.SittingOn == 0 ? null : SeatName(client.Self.SittingOn),
            following = followId == UUID.Zero ? null : followName,
            auto_accept_lures_from = autoLure ? LureAllow : Array.Empty<string>(),
            pending_lure = pendingLure?.name, queued_events = Events.Count,
            rss_mb = proc.WorkingSet64 / 1048576, objects_known = sim?.ObjectsPrimitives.Count, avatars_known = sim?.ObjectsAvatars.Count
        };
    }

    public static async Task<object> NearbyObj(float radius, string filter, int max)
    {
        var sim = Sim; var me = client.Self.SimPosition;
        var avs = Avatars().Select(t => new
        {
            name = t.av.Name, uuid = t.av.ID.ToString(), distance = t.dist < 0 ? (float?)null : MathF.Round(t.dist, 1),
            pos = t.dist < 0 ? null : new[] { MathF.Round(t.pos.X, 1), MathF.Round(t.pos.Y, 1), MathF.Round(t.pos.Z, 1) },
            seated_on = t.av.ParentID != 0 ? SeatName(t.av.ParentID) : null
        }).ToList();
        foreach (var t in Avatars()) Remember(t.av.Name, t.av.ID);
        var roots = sim.ObjectsPrimitives.Values
            .Where(p => p != null && p.ParentID == 0 && p.PrimData.PCode == PCode.Prim)
            .Select(p => (p, d: Vector3.Distance(p.Position, me))).Where(t => t.d <= radius)
            .OrderBy(t => t.d).Take(string.IsNullOrEmpty(filter) ? 400 : 3000).ToList();
        await EnsureProperties(sim, roots.Select(t => t.p).ToList());
        var sit = Sitters(sim);
        var objs = roots.Where(t => string.IsNullOrEmpty(filter) || (t.p.Properties?.Name ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Take(max).Select(t => new
            {
                name = t.p.Properties?.Name, uuid = t.p.ID.ToString(), distance = MathF.Round(t.d, 1),
                pos = new[] { MathF.Round(t.p.Position.X, 1), MathF.Round(t.p.Position.Y, 1), MathF.Round(t.p.Position.Z, 1) },
                owner = t.p.Properties != null ? NameOf(t.p.Properties.OwnerID) : null,
                occupied_by = sit.TryGetValue(t.p.LocalID, out var l) ? l : null
            }).ToList();
        return new { region = sim.Name, me = new[] { me.X, me.Y, me.Z }, avatars = avs, objects = objs };
    }

    public static List<EventItem> DrainEvents(int max)
    {
        var list = new List<EventItem>();
        while (list.Count < max && Events.TryDequeue(out var e)) list.Add(e);
        return list;
    }

    public static Task<string> Run(string line) => Exec(line);

    // ---- profile (About text) ------------------------------------------
    // Prefer the AgentProfile capability (HTTP GET/PUT <cap>/<agent_id>, what the current SL viewer uses);
    // fall back to legacy UDP AvatarPropertiesRequest/Reply + AvatarPropertiesUpdate.
    static readonly SemaphoreSlim profileLock = new(1, 1);
    const int MaxAboutChars = 4000;

    static Uri ProfileCapUri()
    {
        var cap = client?.Network?.CurrentSim?.Caps?.CapabilityURI("AgentProfile");
        return cap == null ? null : new Uri(cap.AbsoluteUri.TrimEnd('/') + "/" + client.Self.AgentID);
    }

    static async Task<(OSDMap map, string err)> CapGetProfile()
    {
        var uri = ProfileCapUri();
        if (uri == null) return (null, "AgentProfile capability not available");
        try
        {
            using var t = new CancellationTokenSource(15000);
            var (resp, data) = await client.HttpCapsClient.GetAsync(uri, t.Token);
            if (!resp.IsSuccessStatusCode) return (null, $"AgentProfile GET HTTP {(int)resp.StatusCode}");
            if (data == null || data.Length == 0) return (null, "AgentProfile GET empty body");
            return OSDParser.Deserialize(data) is OSDMap m ? (m, null) : (null, "AgentProfile GET: not an LLSD map");
        }
        catch (Exception ex) { return (null, "AgentProfile GET failed: " + ex.GetBaseException().Message); }
    }

    static async Task<Avatar.AvatarProperties?> UdpGetProfile()
    {
        var me = client.Self.AgentID;
        var tcs = new TaskCompletionSource<Avatar.AvatarProperties>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(object s, AvatarPropertiesReplyEventArgs e) { if (e.AvatarID == me) tcs.TrySetResult(e.Properties); }
        client.Avatars.AvatarPropertiesReply += H;
        try
        {
            client.Avatars.RequestAvatarProperties(me);
            return await Task.WhenAny(tcs.Task, Task.Delay(10000)) == tcs.Task ? tcs.Task.Result : null;
        }
        finally { client.Avatars.AvatarPropertiesReply -= H; }
    }

    static object CapView(OSDMap m) => new
    {
        about_text = m["sl_about_text"].AsString(), first_life_text = m["fl_about_text"].AsString(),
        image_id = m["sl_image_id"].AsUUID().ToString(), first_life_image_id = m["fl_image_id"].AsUUID().ToString(),
        profile_url = m["home_page"].AsString(), allow_publish = m["allow_publish"].AsBoolean(), mature_profile = m["mature_profile"].AsBoolean()
    };
    static object UdpView(Avatar.AvatarProperties p) => new
    {
        about_text = p.AboutText, first_life_text = p.FirstLifeText, image_id = p.ProfileImage.ToString(), first_life_image_id = p.FirstLifeImage.ToString(),
        profile_url = p.ProfileURL, allow_publish = p.AllowPublish, mature_publish = p.MaturePublish, flags = p.Flags.ToString()
    };

    public static async Task<object> GetProfileObj()
    {
        if (!LoggedIn) return new { ok = false, result = "not logged in" };
        if (ProfileCapUri() != null)
        {
            var (m, err) = await CapGetProfile();
            if (m != null) return new { ok = true, via = "AgentProfile capability", profile = CapView(m) };
            Log("profile", "cap read failed (" + err + "); trying legacy UDP");
        }
        var p = await UdpGetProfile();
        return p is { } pp ? new { ok = true, via = "legacy UDP AvatarPropertiesRequest", profile = UdpView(pp) }
                           : new { ok = false, result = "no profile reply (cap unavailable/failed and UDP timed out)" };
    }

    // volatile keys that may legitimately differ between two reads
    static readonly HashSet<string> VolatileKeys = new() { "online", "display_name_next_update", "sl_about_text" };
    static List<string> ChangedKeys(OSDMap a, OSDMap b)
    {
        var keys = new HashSet<string>(a.Keys); keys.UnionWith(b.Keys);
        return keys.Where(k => !VolatileKeys.Contains(k))
                   .Where(k => OSDParser.SerializeJsonString(a.ContainsKey(k) ? a[k] : new OSD()) != OSDParser.SerializeJsonString(b.ContainsKey(k) ? b[k] : new OSD()))
                   .OrderBy(k => k).ToList();
    }

    public static async Task<object> SetAboutObj(string about)
    {
        if (!LoggedIn) return new { ok = false, result = "not logged in" };
        about ??= "";
        about = about.Replace("\r\n", "\n");
        if (about.Length > MaxAboutChars) return new { ok = false, result = $"about_text too long ({about.Length} > {MaxAboutChars} chars)" };
        if (!await profileLock.WaitAsync(30000)) return new { ok = false, result = "another profile update is in progress" };
        try
        {
            var backupDir = Path.GetDirectoryName(LogPath)!;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            if (ProfileCapUri() != null)
            {
                var (before, err) = await CapGetProfile();
                if (before == null) return new { ok = false, result = "refusing to write: could not read current profile first (" + err + ")" };
                var backup = Path.Combine(backupDir, $"profile-backup-{stamp}.json");
                try { File.WriteAllText(backup, OSDParser.SerializeJsonString(before, true)); } catch { backup = null; }
                // Same as the SL viewer's "save description": a partial PUT with only sl_about_text. The server
                // leaves every other field (images, first-life text, URL, publish flags) as stored. Nothing else is resent.
                var payload = new OSDMap { ["sl_about_text"] = OSD.FromString(about) };
                int status;
                try
                {
                    using var t = new CancellationTokenSource(15000);
                    var (resp, _) = await client.HttpCapsClient.PutAsync(ProfileCapUri(), OSDFormat.Xml, payload, t.Token);
                    status = (int)resp.StatusCode;
                }
                catch (Exception ex) { return new { ok = false, via = "AgentProfile capability (PUT)", result = "PUT failed: " + ex.GetBaseException().Message, backup }; }
                Log("profile", $"About text PUT via AgentProfile cap: HTTP {status} ({about.Length} chars)");
                if (status < 200 || status >= 300) return new { ok = false, via = "AgentProfile capability (PUT)", result = $"PUT returned HTTP {status}", backup };
                await Task.Delay(1500);
                var (after, err2) = await CapGetProfile();
                if (after == null) return new { ok = false, via = "AgentProfile capability (PUT)", http_status = status, result = "written, but read-back failed: " + err2, backup };
                var got = after["sl_about_text"].AsString();
                var changed = ChangedKeys(before, after);
                return new
                {
                    ok = got == about, via = "AgentProfile capability (PUT, sl_about_text only)", http_status = status,
                    readback_matches = got == about, previous_about = before["sl_about_text"].AsString(), about_text = got,
                    other_fields_changed = changed, profile = CapView(after), backup
                };
            }
            else
            {
                var cur = await UdpGetProfile();
                if (cur is not { } p) return new { ok = false, result = "refusing to write: no AvatarPropertiesReply for current profile (10 s)" };
                var backup = Path.Combine(backupDir, $"profile-backup-{stamp}.json");
                try { File.WriteAllText(backup, JsonSerializer.Serialize(UdpView(p))); } catch { backup = null; }
                var prev = p.AboutText;
                var upd = p; // copy of the current values; only AboutText changes
                upd.AboutText = about;
                upd.AllowPublish = p.AllowPublish; upd.MaturePublish = p.MaturePublish;
                client.Self.UpdateProfileUdp(upd);
                Log("profile", $"About text sent via legacy AvatarPropertiesUpdate ({about.Length} chars)");
                await Task.Delay(2000);
                var after = await UdpGetProfile();
                if (after is not { } a) return new { ok = false, via = "legacy UDP AvatarPropertiesUpdate", result = "sent, but read-back timed out", backup };
                var diffs = new List<string>();
                if (a.FirstLifeText != p.FirstLifeText) diffs.Add("first_life_text");
                if (a.ProfileImage != p.ProfileImage) diffs.Add("image_id");
                if (a.FirstLifeImage != p.FirstLifeImage) diffs.Add("first_life_image_id");
                if (a.ProfileURL != p.ProfileURL) diffs.Add("profile_url");
                if (((a.Flags ^ p.Flags) & (ProfileFlags.AllowPublish | ProfileFlags.MaturePublish)) != 0) diffs.Add("publish_flags");
                return new
                {
                    ok = a.AboutText == about, via = "legacy UDP AvatarPropertiesUpdate (all fields resent unchanged)", readback_matches = a.AboutText == about,
                    previous_about = prev, about_text = a.AboutText, other_fields_changed = diffs, profile = UdpView(a), backup
                };
            }
        }
        finally { profileLock.Release(); }
    }

    // ---- balance / prices / caps (read-only) ----------------------------
    public static async Task<int?> BalanceAsync()
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(object s, BalanceEventArgs e) => tcs.TrySetResult(e.Balance);
        client.Self.MoneyBalance += H;
        try
        {
            client.Self.RequestBalance();
            return await Task.WhenAny(tcs.Task, Task.Delay(8000)) == tcs.Task ? tcs.Task.Result : (int?)null;
        }
        finally { client.Self.MoneyBalance -= H; }
    }

    static readonly string[] InterestingCaps = { "AgentProfile", "UploadAgentProfileImage", "SetDisplayName", "GetDisplayNames", "NewFileAgentInventory", "ViewerBenefits" };
    public static object CapsObj()
    {
        var caps = client.Network.CurrentSim?.Caps;
        return InterestingCaps.ToDictionary(c => c, c => caps?.CapabilityURI(c) != null); // never print cap URLs
    }

    public static async Task<object> UploadPriceObj()
    {
        int econ = -1;
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(object s, PacketReceivedEventArgs e) { if (e.Packet is EconomyDataPacket ep) tcs.TrySetResult(ep.Info.PriceUpload); }
        client.Network.RegisterCallback(PacketType.EconomyData, H);
        try
        {
            client.Network.SendPacket(new EconomyDataRequestPacket());
            if (await Task.WhenAny(tcs.Task, Task.Delay(5000)) == tcs.Task) econ = tcs.Task.Result;
        }
        finally { client.Network.UnregisterCallback(PacketType.EconomyData, H); }
        var b = client.Self.Benefits;
        var vb = client.Self.ViewerBenefits?.AccountLevelBenefits;
        return new
        {
            economy_price_upload = econ,
            login_texture_upload_cost = b?.TextureUploadCost, login_large_texture_upload_cost = b?.LargeTextureUploadCost,
            viewer_benefits_texture_upload_cost = vb?.TextureUploadCost, viewer_benefits_large_texture_upload_cost = vb?.LargeTextureUploadCost,
            account_type = client.Self.ViewerBenefits?.AccountType,
            note = "texture_upload_cost applies up to 1024x1024; large_texture_upload_cost tiers apply above (2048x2048)"
        };
    }

    // ---- display name ---------------------------------------------------
    public static async Task<object> DisplayNameObj()
    {
        var me = client.Self.AgentID;
        var (ok, names, bad) = await client.Avatars.GetDisplayNamesAsync(new List<UUID> { me });
        var n = ok ? names?.FirstOrDefault(x => x.ID == me) : null;
        if (n == null) return new { ok = false, result = "GetDisplayNames failed" };
        return new { ok = true, display_name = n.DisplayName, username = n.UserName, legacy_name = n.LegacyFullName, is_default = n.IsDefaultDisplayName,
                     next_update = n.NextUpdate == DateTime.MinValue ? null : n.NextUpdate.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture) };
    }

    public static async Task<object> SetDisplayNameObj(string newName)
    {
        newName = (newName ?? "").Trim();
        if (newName.Length < 1 || newName.Length > 31) return new { ok = false, result = "display name must be 1-31 characters" };
        var me = client.Self.AgentID;
        var (ok, names, _) = await client.Avatars.GetDisplayNamesAsync(new List<UUID> { me });
        var cur = ok ? names?.FirstOrDefault(x => x.ID == me) : null;
        if (cur == null) return new { ok = false, result = "could not read current display name (GetDisplayNames failed)" };
        if (cur.DisplayName == newName) return new { ok = true, result = "already set", display_name = cur.DisplayName };
        var cap = client.Network.CurrentSim?.Caps?.CapabilityURI("SetDisplayName");
        if (cap == null) return new { ok = false, result = "SetDisplayName capability not available" };
        var tcs = new TaskCompletionSource<SetDisplayNameReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(object s, SetDisplayNameReplyEventArgs e) => tcs.TrySetResult(e);
        client.Self.SetDisplayNameReply += H;
        int http;
        try
        {
            var msg = new LibreMetaverse.Messages.Linden.SetDisplayNameMessage { OldDisplayName = cur.DisplayName, NewDisplayName = newName };
            using (var t = new CancellationTokenSource(15000))
            {
                var (resp, _) = await client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, msg.Serialize(), t.Token);
                http = (int)resp.StatusCode;
            }
            Log("profile", $"SetDisplayName '{cur.DisplayName}' -> '{newName}': HTTP {http}");
            var got = await Task.WhenAny(tcs.Task, Task.Delay(20000)) == tcs.Task ? tcs.Task.Result : null;
            await Task.Delay(1500);
            var after = await DisplayNameObj();
            Log("profile", $"SetDisplayNameReply: {(got == null ? "none" : $"{got.Status} {got.Reason}")}");
            return new
            {
                ok = got?.Status == 200, http_status = http, previous = cur.DisplayName, previous_next_update = cur.NextUpdate.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture),
                reply_status = got?.Status, reply_reason = got?.Reason, reply_display_name = got?.DisplayName?.DisplayName,
                reply_next_update = got?.DisplayName == null ? null : got.DisplayName.NextUpdate.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture),
                lookup_after = after
            };
        }
        catch (Exception ex) { return new { ok = false, result = "SetDisplayName failed: " + ex.GetBaseException().Message }; }
        finally { client.Self.SetDisplayNameReply -= H; }
    }

    // ---- profile picture via UploadAgentProfileImage (free cap used by the SL viewer's "Upload Photo") ----
    // Two stages, exactly like llpanelprofile.cpp post_profile_image(): POST {"profile-image-asset":"sl_image_id"} to the cap
    // -> {"uploader": url}; POST the J2C codestream (Content-Type application/jp2) to the uploader -> {"state":"complete","new_asset":uuid}.
    // No inventory item, no expected_upload_cost. One attempt only. Aborts if stage 1 mentions any price/cost/fee.
    static readonly SemaphoreSlim imageLock = new(1, 1);
    public static async Task<object> SetProfileImageObj(string path)
    {
        path = (path ?? "").Trim().Trim('"');
        if (!File.Exists(path)) return new { ok = false, result = "file not found: " + path };
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0x4F || bytes[2] != 0xFF || bytes[3] != 0x51)
            return new { ok = false, result = "not a raw JPEG2000 codestream (.j2c, magic FF4FFF51)" };
        if (bytes.Length > 4_000_000) return new { ok = false, result = "file too large" };
        var cap = client.Network.CurrentSim?.Caps?.CapabilityURI("UploadAgentProfileImage");
        if (cap == null) return new { ok = false, result = "UploadAgentProfileImage capability not offered by this region; NOT falling back to a paid upload" };
        if (ProfileCapUri() == null) return new { ok = false, result = "AgentProfile capability missing; cannot verify" };
        if (!await imageLock.WaitAsync(1000)) return new { ok = false, result = "another image upload is in progress" };
        try
        {
            var (before, err0) = await CapGetProfile();
            if (before == null) return new { ok = false, result = "cannot read profile before upload: " + err0 };
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backup = Path.Combine(Path.GetDirectoryName(LogPath)!, $"profile-backup-{stamp}.json");
            try { File.WriteAllText(backup, OSDParser.SerializeJsonString(before, true)); } catch { backup = null; }
            var balBefore = await BalanceAsync();
            // stage 1
            OSDMap r1;
            int s1;
            using (var t = new CancellationTokenSource(20000))
            {
                var (resp, data) = await client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, new OSDMap { ["profile-image-asset"] = OSD.FromString("sl_image_id") }, t.Token);
                s1 = (int)resp.StatusCode;
                r1 = data != null && data.Length > 0 ? OSDParser.Deserialize(data) as OSDMap : null;
            }
            var keys1 = r1?.Keys.ToList() ?? new List<string>();
            Log("profile", $"UploadAgentProfileImage stage 1: HTTP {s1}, keys [{string.Join(",", keys1)}]");
            if (s1 < 200 || s1 >= 300 || r1 == null || !r1.ContainsKey("uploader"))
                return new { ok = false, stage = 1, http_status = s1, response_keys = keys1, state = r1?["state"].AsString(), message = r1?["message"].AsString(), balance_before = balBefore };
            var priceKeys = keys1.Where(k => k.Contains("cost", StringComparison.OrdinalIgnoreCase) || k.Contains("price", StringComparison.OrdinalIgnoreCase) || k.Contains("fee", StringComparison.OrdinalIgnoreCase)).ToList();
            if (priceKeys.Count > 0 && priceKeys.Any(k => r1[k].AsInteger() != 0))
                return new { ok = false, stage = 1, result = "ABORTED before sending image: stage-1 response mentions a price", price_fields = priceKeys.ToDictionary(k => k, k => r1[k].AsString()) };
            var uploader = r1["uploader"].AsUri();
            // stage 2
            int s2; OSDMap r2; string raw2 = null;
            using (var t = new CancellationTokenSource(60000))
            {
                var (resp, data) = await client.HttpCapsClient.PostAsync(uploader, "application/jp2", bytes, t.Token);
                s2 = (int)resp.StatusCode;
                r2 = null;
                if (data != null && data.Length > 0) { try { r2 = OSDParser.Deserialize(data) as OSDMap; } catch { } if (r2 == null) raw2 = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 300)); }
            }
            var state = r2?["state"].AsString(); var newAsset = r2 != null ? r2["new_asset"].AsUUID() : UUID.Zero;
            Log("profile", $"UploadAgentProfileImage stage 2: HTTP {s2}, state {state}, new_asset {newAsset}, {bytes.Length} bytes");
            if (s2 < 200 || s2 >= 300 || state != "complete" || newAsset == UUID.Zero)
            {
                var bf = await BalanceAsync();
                return new { ok = false, stage = 2, http_status = s2, state, message = r2?["message"].AsString(), raw = raw2, response_keys = r2?.Keys.ToList(), balance_before = balBefore, balance_after = bf };
            }
            await Task.Delay(2000);
            var (after, err1) = await CapGetProfile();
            string putNote = null;
            if (after != null && after["sl_image_id"].AsUUID() != newAsset)
            {
                // server did not attach it itself; set only sl_image_id (partial PUT, like the viewer's "Change Photo")
                using var t = new CancellationTokenSource(15000);
                var (resp, _) = await client.HttpCapsClient.PutAsync(ProfileCapUri(), OSDFormat.Xml, new OSDMap { ["sl_image_id"] = OSD.FromUUID(newAsset) }, t.Token);
                putNote = $"server had not set sl_image_id; PUT sl_image_id -> HTTP {(int)resp.StatusCode}";
                Log("profile", putNote);
                await Task.Delay(1500);
                (after, err1) = await CapGetProfile();
            }
            var balAfter = await BalanceAsync();
            if (after == null) return new { ok = false, new_asset = newAsset.ToString(), result = "uploaded, but read-back failed: " + err1, balance_before = balBefore, balance_after = balAfter };
            var changed = ChangedKeys(before, after).Where(k => k != "sl_image_id").ToList();
            return new
            {
                ok = after["sl_image_id"].AsUUID() == newAsset, via = "UploadAgentProfileImage (free profile upload cap)", new_asset = newAsset.ToString(),
                readback_image_id = after["sl_image_id"].AsUUID().ToString(), previous_image_id = before["sl_image_id"].AsUUID().ToString(),
                about_unchanged = after["sl_about_text"].AsString() == before["sl_about_text"].AsString(), about_text = after["sl_about_text"].AsString(),
                other_fields_changed = changed, put_note = putNote, bytes = bytes.Length, balance_before = balBefore, balance_after = balAfter, backup
            };
        }
        catch (Exception ex) { return new { ok = false, result = "profile image upload failed: " + ex.GetBaseException().Message }; }
        finally { imageLock.Release(); }
    }

    // ---- clear profile picture (viewer "Remove Photo": partial PUT sl_image_id = null UUID) ----
    public static async Task<object> ClearProfileImageObj()
    {
        if (!LoggedIn) return new { ok = false, result = "not logged in" };
        if (ProfileCapUri() == null) return new { ok = false, result = "AgentProfile capability missing" };
        if (!await imageLock.WaitAsync(1000)) return new { ok = false, result = "another image operation is in progress" };
        try
        {
            var (before, err0) = await CapGetProfile();
            if (before == null) return new { ok = false, result = "refusing to write: could not read current profile first (" + err0 + ")" };
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backup = Path.Combine(Path.GetDirectoryName(LogPath)!, $"profile-backup-{stamp}.json");
            try { File.WriteAllText(backup, OSDParser.SerializeJsonString(before, true)); } catch { backup = null; }
            int status;
            using (var t = new CancellationTokenSource(15000))
            {
                var (resp, _) = await client.HttpCapsClient.PutAsync(ProfileCapUri(), OSDFormat.Xml, new OSDMap { ["sl_image_id"] = OSD.FromUUID(UUID.Zero) }, t.Token);
                status = (int)resp.StatusCode;
            }
            Log("profile", $"clear profile image: PUT sl_image_id=00000000-... via AgentProfile cap: HTTP {status}");
            if (status < 200 || status >= 300) return new { ok = false, via = "AgentProfile capability (PUT sl_image_id only)", http_status = status, backup };
            await Task.Delay(1500);
            var (after, err1) = await CapGetProfile();
            if (after == null) return new { ok = false, http_status = status, result = "written, but read-back failed: " + err1, backup };
            var changed = ChangedKeys(before, after).Where(k => k != "sl_image_id").ToList();
            return new
            {
                ok = after["sl_image_id"].AsUUID() == UUID.Zero, via = "AgentProfile capability (PUT sl_image_id only)", http_status = status,
                previous_image_id = before["sl_image_id"].AsUUID().ToString(), readback_image_id = after["sl_image_id"].AsUUID().ToString(),
                about_unchanged = after["sl_about_text"].AsString() == before["sl_about_text"].AsString(), about_text = after["sl_about_text"].AsString(),
                other_fields_changed = changed, backup
            };
        }
        catch (Exception ex) { return new { ok = false, result = "clear profile image failed: " + ex.GetBaseException().Message }; }
        finally { imageLock.Release(); }
    }

    // ---- paid texture upload (NewFileAgentInventory, the viewer's File > Upload > Image path) ----
    // Guarded: one paid upload per marker file, hard ceiling MaxPaidUploadL, price checked from ViewerBenefits
    // (2K tier) before stage 1 and against any price field in the stage-1 reply before the image is sent.
    // Never calls PayUploadFee / any L$ transfer; the server itself charges on stage-2 completion.
    const int MaxPaidUploadL = 50;
    // one marker per approved upload: paid-upload.done = 16:02 Burne-Jones profile 2K (used); this build = First Life 2K (2026-09-25 evening)
    static readonly string PaidMarker = "/workspace/secondlife/paid-upload-firstlife.done";

    static (int w, int h)? J2cDims(byte[] b)
    {
        if (b.Length < 24 || b[0] != 0xFF || b[1] != 0x4F || b[2] != 0xFF || b[3] != 0x51) return null;
        int U32(int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        int xs = U32(8), ys = U32(12), xo = U32(16), yo = U32(20);
        return (xs - xo, ys - yo);
    }

    static async Task<(int cost, object info)> TextureCostFor(int w, int h)
    {
        LibreMetaverse.Messages.Linden.ViewerBenefitsMessage vbm = null;
        try { using var t = new CancellationTokenSource(15000); vbm = await client.Self.GetViewerBenefitsAsync(t.Token); } catch { }
        var vb = vbm?.AccountLevelBenefits;
        var lb = client.Self.Benefits;
        int std = vb?.TextureUploadCost ?? lb?.TextureUploadCost ?? -1;
        var large = vb?.LargeTextureUploadCost?.ToList() ?? new List<int>();
        bool big = (long)w * h > 1024L * 1024L;
        // viewer (llagentbenefits.cpp): 2K cost = sorted large_texture_upload_cost[0]; falls back to texture_upload_cost if absent
        int cost = !big ? std : (large.Count > 0 ? large.OrderBy(x => x).First() : std);
        string source = !big ? "texture_upload_cost" : large.Count > 0 ? "server large_texture_upload_cost" : null;
        string customerType = null;
        if (big && large.Count == 0)
        {
            // No 2K tier from login/ViewerBenefits (LibreMetaverse falls back to the 1K price, which the server rejects with
            // "The server expects a different upload fee"). Use LL's published 2K schedule keyed by the profile's customer_type.
            var (pm, _) = await CapGetProfile();
            customerType = pm?["customer_type"].AsString();
            cost = customerType switch { "Base" or "Plus" => 50, "Premium" => 40, "Premium_Plus" or "Premium Plus" or "PremiumPlus" => 0, _ => -1 };
            source = $"LL 2K schedule for customer_type '{customerType}'";
        }
        return (cost, new
        {
            price_source = source, customer_type = customerType,
            width = w, height = h, is_2k = big, account_type = vbm?.AccountType,
            viewer_benefits_texture_upload_cost = vb?.TextureUploadCost, viewer_benefits_large_texture_upload_cost = vb?.LargeTextureUploadCost,
            login_texture_upload_cost = lb?.TextureUploadCost, login_large_texture_upload_cost = lb?.LargeTextureUploadCost,
            viewer_benefits_fetched = vbm != null, large_tier_from_server = large.Count > 0
        });
    }

    static async Task<UUID> TexturesFolder()
    {
        var f = client.Inventory.FindFolderForType(FolderType.Texture);
        var root = client.Inventory.Store?.RootFolder?.UUID ?? client.Network.LoginResponseData?.InventoryRoot ?? UUID.Zero;
        if (root == UUID.Zero) return UUID.Zero;
        if (f != UUID.Zero && f != root) return f;
        try
        {
            using var t = new CancellationTokenSource(20000);
            var items = await client.Inventory.FolderContentsAsync(root, client.Self.AgentID, true, false, InventorySortOrder.ByName, t.Token);
            var tf = items?.OfType<InventoryFolder>().FirstOrDefault(x => x.PreferredType == FolderType.Texture);
            if (tf != null) return tf.UUID;
        }
        catch { }
        return UUID.Zero;
    }

    static OSDMap PaidStage1Body(UUID folder, string name, int expected, string description = null) => new OSDMap
    {
        ["folder_id"] = OSD.FromUUID(folder), ["asset_type"] = OSD.FromString("texture"), ["inventory_type"] = OSD.FromString("texture"),
        ["name"] = OSD.FromString(name), ["description"] = OSD.FromString(description ?? name),
        ["next_owner_mask"] = OSD.FromInteger((int)(PermissionMask.Move | PermissionMask.Transfer | PermissionMask.Copy | PermissionMask.Modify)),
        ["group_mask"] = OSD.FromInteger(0), ["everyone_mask"] = OSD.FromInteger(0),
        ["expected_upload_cost"] = OSD.FromInteger(expected)
    };

    static Dictionary<string, string> SafeKeys(OSDMap m) => m == null ? null :
        m.Keys.ToDictionary(k => k, k => k == "uploader" ? "(url hidden)" : m[k].AsString());

    // quote: dims + benefits price + textures folder + balance; with probe=true also does stage 1 only
    // (reply keys shown, uploader URL discarded, nothing sent to it, so no asset and no charge).
    public static async Task<object> UploadQuoteObj(string path, bool probe)
    {
        path = (path ?? "").Trim().Trim('"');
        if (!File.Exists(path)) return new { ok = false, result = "file not found: " + path };
        var bytes = File.ReadAllBytes(path);
        var d = J2cDims(bytes);
        if (d == null) return new { ok = false, result = "not a raw JPEG2000 codestream (.j2c)" };
        var (cost, info) = await TextureCostFor(d.Value.w, d.Value.h);
        var folder = await TexturesFolder();
        var bal = await BalanceAsync();
        object stage1 = null;
        if (probe)
        {
            var cap = client.Network.CurrentSim?.Caps?.CapabilityURI("NewFileAgentInventory");
            if (cap == null) stage1 = "NewFileAgentInventory cap missing";
            else
            {
                using var t = new CancellationTokenSource(20000);
                var (resp, data) = await client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, PaidStage1Body(folder, "(probe - not uploaded)", cost), t.Token);
                OSDMap r = null; try { r = data != null && data.Length > 0 ? OSDParser.Deserialize(data) as OSDMap : null; } catch { }
                stage1 = new { http_status = (int)resp.StatusCode, reply = SafeKeys(r) };
                Log("upload", $"quote probe stage 1 only: HTTP {(int)resp.StatusCode}, keys [{string.Join(",", r?.Keys ?? new List<string>())}] (uploader discarded)");
            }
        }
        var bal2 = probe ? await BalanceAsync() : bal;
        return new { ok = true, file = path, bytes = bytes.Length, expected_cost = cost, price_info = info, textures_folder = folder.ToString(), balance = bal, stage1_probe = stage1, balance_after_probe = bal2, marker_exists = File.Exists(PaidMarker) };
    }

    static readonly SemaphoreSlim paidLock = new(1, 1);
    public static async Task<object> PaidTextureUploadObj(int maxL, string path, string name)
    {
        path = (path ?? "").Trim().Trim('"'); name = (name ?? "").Trim();
        if (maxL < 0 || maxL > MaxPaidUploadL) return new { ok = false, result = $"max price must be 0..{MaxPaidUploadL}" };
        if (name.Length == 0 || name.Length > 63) return new { ok = false, result = "name must be 1-63 chars" };
        if (File.Exists(PaidMarker)) return new { ok = false, result = "REFUSED: a paid upload was already done/attempted (marker " + PaidMarker + ")" };
        if (!File.Exists(path)) return new { ok = false, result = "file not found: " + path };
        var bytes = File.ReadAllBytes(path);
        var d = J2cDims(bytes);
        if (d == null) return new { ok = false, result = "not a raw JPEG2000 codestream (.j2c)" };
        var (w, h) = d.Value;
        bool Pow2(int v) => v >= 16 && v <= 2048 && (v & (v - 1)) == 0;
        if (!Pow2(w) || !Pow2(h)) return new { ok = false, result = $"dimensions {w}x{h} must be powers of two <= 2048" };
        if (bytes.Length > 8_000_000) return new { ok = false, result = "file too large" };
        var cap = client.Network.CurrentSim?.Caps?.CapabilityURI("NewFileAgentInventory");
        if (cap == null) return new { ok = false, result = "NewFileAgentInventory capability missing" };
        if (!await paidLock.WaitAsync(1000)) return new { ok = false, result = "another paid upload is in progress" };
        try
        {
            var (cost, info) = await TextureCostFor(w, h);
            if (cost < 0) return new { ok = false, result = "could not determine upload price", price_info = info };
            if (cost > maxL) return new { ok = false, result = $"STOPPED: price L${cost} exceeds max L${maxL}; nothing uploaded", price_info = info };
            var folder = await TexturesFolder();
            if (folder == UUID.Zero) return new { ok = false, result = "Textures folder not found; nothing uploaded" };
            var balBefore = await BalanceAsync();
            if (balBefore == null) return new { ok = false, result = "no balance reply; nothing uploaded" };
            if (balBefore < cost) return new { ok = false, result = $"balance L${balBefore} < price L${cost}; nothing uploaded" };
            // stage 1
            OSDMap r1; int s1;
            using (var t = new CancellationTokenSource(20000))
            {
                var (resp, data) = await client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, PaidStage1Body(folder, name, cost), t.Token);
                s1 = (int)resp.StatusCode;
                r1 = null; try { r1 = data != null && data.Length > 0 ? OSDParser.Deserialize(data) as OSDMap : null; } catch { }
            }
            Log("upload", $"paid texture stage 1: HTTP {s1}, keys [{string.Join(",", r1?.Keys ?? new List<string>())}], expected_upload_cost {cost}");
            if (s1 < 200 || s1 >= 300 || r1 == null || r1["state"].AsString() != "upload" || !r1.ContainsKey("uploader"))
                return new { ok = false, stage = 1, http_status = s1, reply = SafeKeys(r1), result = "stage 1 refused; nothing uploaded, no charge", balance_before = balBefore };
            foreach (var k in r1.Keys.Where(k => k.Contains("price", StringComparison.OrdinalIgnoreCase) || k.Contains("cost", StringComparison.OrdinalIgnoreCase) || k.Contains("fee", StringComparison.OrdinalIgnoreCase)))
                if (r1[k].AsInteger() > maxL)
                    return new { ok = false, stage = 1, result = $"STOPPED: stage-1 reply {k}={r1[k].AsInteger()} exceeds max L${maxL}; image NOT sent", reply = SafeKeys(r1), balance_before = balBefore };
            var stage1Reply = SafeKeys(r1);
            var uploader = r1["uploader"].AsUri();
            // one attempt only; marker written first so this can never be repeated (even after a crash)
            File.WriteAllText(PaidMarker, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss zzz} paid texture upload '{name}' {w}x{h} {bytes.Length} bytes, expected L${cost}\n");
            int s2; OSDMap r2 = null; string raw2 = null;
            try
            {
                using var t = new CancellationTokenSource(80000);
                var (resp, data) = await client.HttpCapsClient.PostAsync(uploader, "application/octet-stream", bytes, t.Token);
                s2 = (int)resp.StatusCode;
                if (data != null && data.Length > 0) { try { r2 = OSDParser.Deserialize(data) as OSDMap; } catch { } if (r2 == null) raw2 = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 300)); }
            }
            catch (Exception ex) { s2 = -1; raw2 = "stage 2 exception: " + ex.GetBaseException().Message; }
            var state = r2?["state"].AsString();
            var newAsset = r2 != null ? r2["new_asset"].AsUUID() : UUID.Zero;
            var newItem = r2 != null ? r2["new_inventory_item"].AsUUID() : UUID.Zero;
            var price = r2 != null && r2.ContainsKey("upload_price") ? r2["upload_price"].AsInteger() : (int?)null;
            Log("upload", $"paid texture stage 2: HTTP {s2}, state {state}, new_asset {newAsset}, item {newItem}, upload_price {price?.ToString() ?? "-"}, {bytes.Length} bytes");
            await Task.Delay(2000);
            var balAfter = await BalanceAsync();
            File.AppendAllText(PaidMarker, $"result: HTTP {s2} state {state} asset {newAsset} item {newItem} upload_price {price} balance {balBefore} -> {balAfter}\n");
            if (newItem != UUID.Zero) try { client.Inventory.RequestFetchInventory(newItem, client.Self.AgentID); } catch { }
            return new
            {
                ok = s2 >= 200 && s2 < 300 && state == "complete" && newAsset != UUID.Zero,
                http_status = s2, state, new_asset = newAsset.ToString(), new_inventory_item = newItem.ToString(), upload_price = price,
                expected_cost = cost, price_info = info, textures_folder = folder.ToString(), name, width = w, height = h, bytes = bytes.Length,
                stage1_reply = stage1Reply, reply = SafeKeys(r2), raw = raw2, balance_before = balBefore, balance_after = balAfter
            };
        }
        catch (Exception ex) { return new { ok = false, result = "paid upload failed: " + ex.GetBaseException().Message }; }
        finally { paidLock.Release(); }
    }

    // set profile picture to an existing texture asset UUID: partial PUT {"sl_image_id": uuid} (viewer "Change Photo"), read-back
    public static async Task<object> SetProfileImageIdObj(string idText)
    {
        if (!UUID.TryParse((idText ?? "").Trim(), out var id) || id == UUID.Zero) return new { ok = false, result = "usage: profile image-id <texture asset uuid>" };
        if (ProfileCapUri() == null) return new { ok = false, result = "AgentProfile capability missing" };
        if (!await imageLock.WaitAsync(1000)) return new { ok = false, result = "another image operation is in progress" };
        try
        {
            var (before, err0) = await CapGetProfile();
            if (before == null) return new { ok = false, result = "refusing to write: could not read current profile first (" + err0 + ")" };
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backup = Path.Combine(Path.GetDirectoryName(LogPath)!, $"profile-backup-{stamp}.json");
            try { File.WriteAllText(backup, OSDParser.SerializeJsonString(before, true)); } catch { backup = null; }
            int status; string body = null;
            using (var t = new CancellationTokenSource(15000))
            {
                var (resp, data) = await client.HttpCapsClient.PutAsync(ProfileCapUri(), OSDFormat.Xml, new OSDMap { ["sl_image_id"] = OSD.FromUUID(id) }, t.Token);
                status = (int)resp.StatusCode;
                if (data != null && data.Length > 0) body = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 300));
            }
            Log("profile", $"set profile image: PUT sl_image_id={id} via AgentProfile cap: HTTP {status}");
            await Task.Delay(1500);
            var (after, err1) = await CapGetProfile();
            if (after == null) return new { ok = false, http_status = status, response = body, result = "read-back failed: " + err1, backup };
            var changed = ChangedKeys(before, after).Where(k => k != "sl_image_id").ToList();
            return new
            {
                ok = status >= 200 && status < 300 && after["sl_image_id"].AsUUID() == id, via = "AgentProfile capability (PUT sl_image_id only)", http_status = status, response = body,
                previous_image_id = before["sl_image_id"].AsUUID().ToString(), readback_image_id = after["sl_image_id"].AsUUID().ToString(),
                about_unchanged = after["sl_about_text"].AsString() == before["sl_about_text"].AsString(), about_text = after["sl_about_text"].AsString(),
                other_fields_changed = changed, backup
            };
        }
        catch (Exception ex) { return new { ok = false, result = "set profile image id failed: " + ex.GetBaseException().Message }; }
        finally { imageLock.Release(); }
    }

    // ---- First Life tab (fl_about_text / fl_image_id): partial PUT of ONE field, same as the viewer ----
    static readonly HashSet<string> ReadVolatileKeys = new() { "online", "display_name_next_update" };
    static List<string> ChangedKeysExcept(OSDMap a, OSDMap b, string field)
    {
        var keys = new HashSet<string>(a.Keys); keys.UnionWith(b.Keys);
        return keys.Where(k => k != field && !ReadVolatileKeys.Contains(k))
                   .Where(k => OSDParser.SerializeJsonString(a.ContainsKey(k) ? a[k] : new OSD()) != OSDParser.SerializeJsonString(b.ContainsKey(k) ? b[k] : new OSD()))
                   .OrderBy(k => k).ToList();
    }

    public static async Task<object> SetFirstLifeAboutObj(string text)
    {
        if (!LoggedIn) return new { ok = false, result = "not logged in" };
        text ??= "";
        text = text.Replace("\r\n", "\n");
        if (text.Length > MaxAboutChars) return new { ok = false, result = $"fl_about_text too long ({text.Length} > {MaxAboutChars} chars)" };
        if (ProfileCapUri() == null) return new { ok = false, result = "AgentProfile capability missing (no legacy fallback for First Life)" };
        if (!await profileLock.WaitAsync(30000)) return new { ok = false, result = "another profile update is in progress" };
        try
        {
            var (before, err) = await CapGetProfile();
            if (before == null) return new { ok = false, result = "refusing to write: could not read current profile first (" + err + ")" };
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backup = Path.Combine(Path.GetDirectoryName(LogPath)!, $"profile-backup-{stamp}.json");
            try { File.WriteAllText(backup, OSDParser.SerializeJsonString(before, true)); } catch { backup = null; }
            var payload = new OSDMap { ["fl_about_text"] = OSD.FromString(text) };
            int status;
            try
            {
                using var t = new CancellationTokenSource(15000);
                var (resp, _) = await client.HttpCapsClient.PutAsync(ProfileCapUri(), OSDFormat.Xml, payload, t.Token);
                status = (int)resp.StatusCode;
            }
            catch (Exception ex) { return new { ok = false, via = "AgentProfile capability (PUT)", result = "PUT failed: " + ex.GetBaseException().Message, backup }; }
            Log("profile", $"First Life text PUT via AgentProfile cap: HTTP {status} ({text.Length} chars)");
            if (status < 200 || status >= 300) return new { ok = false, via = "AgentProfile capability (PUT)", result = $"PUT returned HTTP {status}", backup };
            await Task.Delay(1500);
            var (after, err2) = await CapGetProfile();
            if (after == null) return new { ok = false, via = "AgentProfile capability (PUT)", http_status = status, result = "written, but read-back failed: " + err2, backup };
            var got = after["fl_about_text"].AsString();
            var changed = ChangedKeysExcept(before, after, "fl_about_text");
            return new
            {
                ok = got == text && changed.Count == 0, via = "AgentProfile capability (PUT, fl_about_text only)", http_status = status,
                readback_matches = got == text, previous_fl_about = before["fl_about_text"].AsString(), fl_about_text = got,
                other_fields_changed = changed,
                sl_about_unchanged = after["sl_about_text"].AsString() == before["sl_about_text"].AsString(),
                sl_image_unchanged = after["sl_image_id"].AsUUID() == before["sl_image_id"].AsUUID(),
                profile = CapView(after), backup
            };
        }
        catch (Exception ex) { return new { ok = false, result = "set first life text failed: " + ex.GetBaseException().Message }; }
        finally { profileLock.Release(); }
    }

    public static async Task<object> SetFirstLifeImageIdObj(string idText)
    {
        if (!UUID.TryParse((idText ?? "").Trim(), out var id) || id == UUID.Zero) return new { ok = false, result = "usage: profile fl-image-id <texture asset uuid>" };
        if (!LoggedIn) return new { ok = false, result = "not logged in" };
        if (ProfileCapUri() == null) return new { ok = false, result = "AgentProfile capability missing" };
        if (!await imageLock.WaitAsync(1000)) return new { ok = false, result = "another image operation is in progress" };
        try
        {
            var (before, err0) = await CapGetProfile();
            if (before == null) return new { ok = false, result = "refusing to write: could not read current profile first (" + err0 + ")" };
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backup = Path.Combine(Path.GetDirectoryName(LogPath)!, $"profile-backup-{stamp}.json");
            try { File.WriteAllText(backup, OSDParser.SerializeJsonString(before, true)); } catch { backup = null; }
            int status; string body = null;
            using (var t = new CancellationTokenSource(15000))
            {
                var (resp, data) = await client.HttpCapsClient.PutAsync(ProfileCapUri(), OSDFormat.Xml, new OSDMap { ["fl_image_id"] = OSD.FromUUID(id) }, t.Token);
                status = (int)resp.StatusCode;
                if (data != null && data.Length > 0) body = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 300));
            }
            Log("profile", $"set first life image: PUT fl_image_id={id} via AgentProfile cap: HTTP {status}");
            await Task.Delay(1500);
            var (after, err1) = await CapGetProfile();
            if (after == null) return new { ok = false, http_status = status, response = body, result = "read-back failed: " + err1, backup };
            var changed = ChangedKeysExcept(before, after, "fl_image_id");
            return new
            {
                ok = status >= 200 && status < 300 && after["fl_image_id"].AsUUID() == id && changed.Count == 0, via = "AgentProfile capability (PUT fl_image_id only)", http_status = status, response = body,
                previous_fl_image_id = before["fl_image_id"].AsUUID().ToString(), readback_fl_image_id = after["fl_image_id"].AsUUID().ToString(),
                sl_image_id = after["sl_image_id"].AsUUID().ToString(),
                sl_about_unchanged = after["sl_about_text"].AsString() == before["sl_about_text"].AsString(),
                fl_about_unchanged = after["fl_about_text"].AsString() == before["fl_about_text"].AsString(),
                other_fields_changed = changed, backup
            };
        }
        catch (Exception ex) { return new { ok = false, result = "set first life image id failed: " + ex.GetBaseException().Message }; }
        finally { imageLock.Release(); }
    }

    // ---- helpers ------------------------------------------------------
    static string Fmt(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "<{0:F1}, {1:F1}, {2:F1}>", v.X, v.Y, v.Z);
    static bool F(string s, out float f) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f);

    static Simulator Sim => client.Network.CurrentSim ?? throw new InvalidOperationException("not connected to a region");

    // dist = -1 when the avatar sits on an object we have not received yet (position unknown)
    static List<(Avatar av, Vector3 pos, float dist)> Avatars()
    {
        var sim = Sim; var me = client.Self.SimPosition;
        return sim.ObjectsAvatars.Values.Where(a => a != null && a.ID != client.Self.AgentID)
            .Select(a =>
            {
                if (a.ParentID != 0 && !sim.ObjectsPrimitives.ContainsKey(a.ParentID)) return (a, Vector3.Zero, -1f);
                var p = PositionHelper.GetAvatarPosition(sim, a); return (a, p, Vector3.Distance(p, me));
            })
            .OrderBy(t => t.Item3 < 0 ? float.MaxValue : t.Item3).ToList();
    }

    static async Task EnsureProperties(Simulator sim, List<Primitive> prims)
    {
        var need = prims.Where(p => p.Properties == null).ToList();
        if (need.Count == 0) return;
        var waiting = new HashSet<UUID>(need.Select(p => p.ID));
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(object s, ObjectPropertiesEventArgs e)
        {
            lock (waiting) { waiting.Remove(e.Properties.ObjectID); if (waiting.Count == 0) tcs.TrySetResult(true); }
        }
        client.Objects.ObjectProperties += H;
        try
        {
            foreach (var chunk in need.Chunk(50))
                client.Objects.SelectObjects(sim, chunk.Select(p => p.LocalID).ToArray(), true);
            await Task.WhenAny(tcs.Task, Task.Delay(Math.Min(15000, 2000 + 20 * need.Count)));
        }
        finally
        {
            client.Objects.ObjectProperties -= H;
            try { foreach (var chunk in need.Chunk(50)) client.Objects.DeselectObjects(sim, chunk.Select(p => p.LocalID).ToArray()); } catch { }
        }
        var owners = need.Where(p => p.Properties != null).Select(p => p.Properties!.OwnerID).Distinct()
            .Where(o => o != UUID.Zero && !idToName.ContainsKey(o)).ToList();
        if (owners.Count > 0) { client.Avatars.RequestAvatarNames(owners); await Task.Delay(700); }
    }

    static Dictionary<uint, List<string>> Sitters(Simulator sim)
    {
        // map root-prim localID -> names of avatars seated on it (or on one of its child prims)
        var res = new Dictionary<uint, List<string>>();
        foreach (var a in sim.ObjectsAvatars.Values)
        {
            if (a == null || a.ParentID == 0) continue;
            uint root = a.ParentID;
            if (sim.ObjectsPrimitives.TryGetValue(root, out var p) && p.ParentID != 0 && sim.ObjectsPrimitives.ContainsKey(p.ParentID)) root = p.ParentID;
            if (!res.TryGetValue(root, out var l)) res[root] = l = new();
            l.Add(a.ID == client.Self.AgentID ? "ME" : a.Name);
        }
        return res;
    }

    static async Task<string> Objects(float radius, string filter, int max = 60)
    {
        var sim = Sim; var me = client.Self.SimPosition;
        var roots = sim.ObjectsPrimitives.Values
            .Where(p => p != null && p.ParentID == 0 && p.PrimData.PCode == PCode.Prim)
            .Select(p => (p, pos: p.Position, d: Vector3.Distance(p.Position, me)))
            .Where(t => t.d <= radius).OrderBy(t => t.d).Take(string.IsNullOrEmpty(filter) ? 400 : 3000).ToList();
        await EnsureProperties(sim, roots.Select(t => t.p).ToList());
        var sit = Sitters(sim);
        var sb = new StringBuilder();
        int n = 0;
        foreach (var (p, pos, d) in roots)
        {
            var name = p.Properties?.Name ?? "(name unknown)";
            if (!string.IsNullOrEmpty(filter) && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (++n > max) { sb.AppendLine($"... (more; narrow with a radius or filter)"); break; }
            var owner = p.Properties != null ? NameOf(p.Properties.OwnerID) : "?";
            var occ = sit.TryGetValue(p.LocalID, out var l) ? $"  OCCUPIED by {string.Join(", ", l)}" : "";
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,6:F1}m  {1}  {2}  owner={3}  at {4}{5}", d, p.ID, name, owner, Fmt(pos), occ));
        }
        if (n == 0) sb.AppendLine("(no objects matched)");
        return sb.ToString();
    }

    static string AvatarList()
    {
        var sb = new StringBuilder();
        var list = Avatars();
        foreach (var (a, p, d) in list)
        {
            Remember(a.Name, a.ID);
            var seat = a.ParentID != 0 ? $"  seated on {SeatName(a.ParentID)}" : "";
            sb.AppendLine(d < 0 ? $"     ?m  {a.ID}  {a.Name}  (seated on an object not loaded yet){seat}"
                              : string.Format(CultureInfo.InvariantCulture, "{0,6:F1}m  {1}  {2}  at {3}{4}", d, a.ID, a.Name, Fmt(p), seat));
        }
        if (list.Count == 0) sb.AppendLine("(no avatars in view)");
        return sb.ToString();
    }

    static string SeatName(uint localId)
    {
        var sim = Sim;
        if (sim.ObjectsPrimitives.TryGetValue(localId, out var p))
        {
            var root = p;
            if (p.ParentID != 0 && sim.ObjectsPrimitives.TryGetValue(p.ParentID, out var r)) root = r;
            return $"{root.Properties?.Name ?? "object"} {root.ID}";
        }
        return $"localid {localId}";
    }

    static async Task<UUID> ResolveAvatar(string who)
    {
        who = who.Trim().Trim('"');
        if (UUID.TryParse(who, out var id)) return id;
        var norm = who.Contains('.') && !who.Contains(' ') ? who.Replace('.', ' ') : who;
        if (!norm.Contains(' ')) norm += " Resident";
        lock (nameToId) if (nameToId.TryGetValue(norm, out var c)) return c;
        try
        {
            var av = Sim.ObjectsAvatars.Values.FirstOrDefault(a => string.Equals(a.Name, norm, StringComparison.OrdinalIgnoreCase));
            if (av != null) { Remember(av.Name, av.ID); return av.ID; }
        }
        catch { }
        var tcs = new TaskCompletionSource<UUID>(TaskCreationOptions.RunContinuationsAsynchronously);
        UUID q = UUID.Zero;
        void H(object s, DirPeopleReplyEventArgs e)
        {
            if (e.QueryID != q) return;
            foreach (var m in e.MatchedPeople)
            {
                var full = $"{m.FirstName} {m.LastName}";
                if (string.Equals(full, norm, StringComparison.OrdinalIgnoreCase)) { Remember(full, m.AgentID); tcs.TrySetResult(m.AgentID); return; }
            }
        }
        client.Directory.DirPeopleReply += H;
        try
        {
            q = client.Directory.StartPeopleSearch(norm, 0);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(8000));
            return done == tcs.Task ? tcs.Task.Result : UUID.Zero;
        }
        finally { client.Directory.DirPeopleReply -= H; }
    }

    // split "im" arguments into (target, text)
    static (string target, string text) SplitTarget(string rest)
    {
        rest = rest.Trim();
        if (rest.StartsWith('"'))
        {
            var end = rest.IndexOf('"', 1);
            if (end > 0) return (rest[1..end], rest[(end + 1)..].Trim());
        }
        var toks = rest.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (toks.Length == 0) return ("", "");
        if (UUID.TryParse(toks[0], out _) || toks[0].Contains('.')) return (toks[0], rest[toks[0].Length..].Trim());
        if (toks.Length >= 3) return ($"{toks[0]} {toks[1]}", toks[2]);
        return (toks[0], toks.Length > 1 ? toks[1] : "");
    }

    const string Help = @"commands (one per line):
  help | status | where
  say <text> | shout <text> | whisper <text> | chan <n> <text>
  im <First Last|username|uuid|""Name""> <text>
  nearby [radius=20]          avatars + objects (uuid, distance, owner, occupancy)
  avatars | objects [radius=20] [name filter] | find <name filter> (64 m)
  objinfo <uuid>
  sit <object uuid> | stand     (sit guard re-checks pose/hover ~6 s after each sit)
  height | diag               sit-height diagnostics: sim size, hover, animations, seat offset
  hover [get] | hover set <m> | hover repin   hover height (AgentPreferences); set also pins it in hover.txt
  rebake | anim [list|stop <uuid>|start <uuid>] | sitguard [on|off]
  worn [scripts]              attachments incl. HUDs: item id, scripted, animations each plays (scripts = list contents)
  worn all                    READ-ONLY: body parts + clothing (wearables/COF), attachments + HUDs (points, COF links), My Outfits (Outfit.cs)
  detach <item> | attach <item> [point]   reversible; logged; attach-block.txt items are off while seated, re-worn on stand
  walk_path x,y,z;x,y,z;... | goto_avatar <name> | sit_near <avatar> | walk_status | walk_stop   walking navigation (never teleports)
  map [radius] [x y] | terrain <x> <y>   planning data: objects with size/rotation, ground height
  route list | route show <name> | route status | route steer [smooth|legacy]   named routes (textclient/routes/*.json) + places of this region's path graph
  route walk <name> [reverse] [allow_zendo] [allow_outside]   follow a route (joins at the nearest point; carrot steering 2.5 m ahead)
  wander start|stop|pause|hold|resume|status|seats|selftest|resumetest   autonomous loop deerpark<->landing with random sits, greetings, chat pause (Wander.cs); resumes after restart/reboot unless stale after a deliberate stop
  quiet status | quiet selftest | quiet override <min 1-30>|off   session detector (>= 3 seated in zendo / Deer Park -> quiet: no greetings, keep 15 m away, nearby say refused) (Quiet.cs)
  goto_place <place> [nosit] [allow_zendo] [allow_outside]    shortest way over the path graph (Naberrie: zendo, landing, waterfall, poolrock, deerpark); poolrock ends with sit_home
  route record <avatar> <name> | route record stop | route stop   record an avatar's walk as a route / stop walking+recording
     (bounds: The Buddha Center parcel in Naberrie, never the zendo unless allowed; stuck -> max 1 m sidestep, no flying, stop+log; avatars on the path -> pause 10 s, 1 m sidestep, else wait/stop)
  overhead [tag] | snapshot [tag]   map-tile overhead picture (position, avatars, seats, path, route) -> /workspace/secondlife/images/overhead-<time>[-tag].png
  mute <uuid|name> | unmute <uuid|name> | mutelist   server mute list; muted avatars' chat/IM/offers are dropped locally
  animwatch on|off|<minutes> | posekeeper on|off   log own animation start/stop with source; re-assert seat pose
  moveto <x> <y> <z>  (goto is an alias)  | walk <meters> | turn <degrees> | face <x y z | avatar name> | stop
  teleport [force] <region name> <x> <y> <z> | home [force]  (refused outside Naberrie while the robe is worn)
  follow <First Last> | follow off
  accept | decline            pending teleport offer (allow-list only)
  dialog <button label>       answer the last script dialog (e.g. AVsitter pose menu)
  touch <object uuid>         touch an object (seat/HUD) so it opens its own menu (NOT the AO HUD: a touch toggles it off)
  pose [change|selftest]      random solo pose (no male/couples) from the current seat's own menu (AVsitter dialog)
  watchdog [selftest]         freeze / heartbeat (60 s) / stale-connection (45 s) watchdog -> clean logout + exit 75 -> supervisor relogin
  offlineim [status|selftest] stored (offline) IMs: fetched at every login, logged [offline, sent ...], sent to the webhook; no auto-actions
  ao [status|selftest]        AO guard: no walking unless the AO override stand/walk is playing (restore = detach+re-attach HUD)
  autolure on|off             auto-accept offers from allow-listed avatars (now: {0})
  profile get | profile set <text>   read / set ONLY the profile About text (read-back verified)
  profile image <file.j2c>           set profile picture via the free UploadAgentProfileImage cap (no paid fallback)
  profile image clear                remove the profile picture (AgentProfile PUT sl_image_id = null UUID)
  profile image-id <uuid>            set profile picture to an existing texture asset (PUT sl_image_id only, read-back)
  profile fl-set <text>              set ONLY the First Life text (PUT fl_about_text; literal \n = line break; backup + read-back)
  profile fl-image-id <uuid>         set the First Life picture to an existing texture asset (PUT fl_image_id only, backup + read-back)
  upload quote <file.j2c> [probe]    price for this texture (ViewerBenefits 2K tier), Textures folder, balance; probe = stage 1 only, no charge
  upload texture <maxL$> <file.j2c> <name>   ONE paid texture upload (NewFileAgentInventory), max L$50, one-shot marker
  displayname get | displayname set <name>
  group list | group info <group uuid>   current groups / group profile (name, open enrollment, fee, members)
  group join <group uuid>     joins ONLY if open enrollment and fee L$0 (profile checked first); reports JoinGroupReply + balance
  pick list | pick info <pick id> | pick delete <pick id>   own profile picks
  pick lookup <region> <x> <y> <z>   dry run: parcel id, parcel name, snapshot id a pick there would use
  pick create <region> <x> <y> <z> | <name> | <description>   new pick (literal \n = line break), read back
  balance | prices | caps            read-only: L$ balance, upload prices, which caps exist (no URLs)
  webhook_test | webhook status      probe the chat webhook (HTTP status) / show config (never the key)
  webhook debounce [<quiet s> [<max s> [<detect s>]]] | webhook debounce detect <s> | webhook debounce selftest   per-conversation debounce: single line after 4 s detect window; burst (2nd line inside it) after 20 s quiet, cap 60 s; urgent = immediate
  restart status | restart test [fast] | restart cancel   region-restart evacuation: state / simulate a warning / abort
                              (warning -> stand, go home [fallbacks, else logout], poll every 60 s, return >= 3 min after the restart, re-sit; 45 min limit)
  sit_home                    Naberrie seat rule: her pillow 10d8a656 if both rock pillows are free, else a quiet free seat in The Buddha Center parcel (not the zendo), else stand
  offers [all|selftest] | offers accept <n> [confirm] | offers decline <n>   pending inventory offers + friendship requests (never auto-accepted; non-allow-listed sender needs 'confirm' = David's OK)
  friend list | friend accept|decline <name> [confirm] | friend add <name> [confirm]   friendships (allow-list: David Nightingale, Sophie-Jeanne)
  landmark create <name> | landmark list | landmark raw <name> | landmark tp <name|item uuid> [pos] [force]   landmarks (pos = teleport to the exact stored position instead of the landmark request; raw = stored asset text)
  sethome                     set home to the current spot; prints the server's reply (e.g. refused off your own/group land)
  parcel [x y]                teleport routing, landing point, owner/group and whether Galatay may ignore the landing point
  worn links <attachment|ao>  every prim of a worn attachment/HUD: link no., local id, name, description, faces, touch flag
  touch-attachment <attachment|ao> <link no.|prim name|local:<id>> [face] [st=u,v]   press one HUD button / prim face (quote names with spaces)
  shape get [filter] | shape set <slider|param id> <0-100>   worn shape sliders; set ONLY on 'Galatea Petite shape - Jani short neck' (backup in shape-backups/, upload + rebake)
  texture save <uuid>         download a texture and save it as PNG under /workspace/secondlife/textures/ (needs the sl-texture-vision add-on)
  faces <object name|uuid> [face=<n>] [r=<m>]   faces of a nearby object/linkset with texture UUIDs; saves the non-blank ones as PNG
  vendor look <name filter> [radius]   nearby objects matching name/hover text (default 20 m): face PNGs + index.json in textures/scan-*/
  inv find <text>[|text2]     READ-ONLY recursive inventory search (path, type, item id, desc, last attach point)
  inv ls <folder uuid>        READ-ONLY direct contents of one folder
  inv read <notecard item>    READ-ONLY print the text of one of her notecards
  wear add|remove <item> [pt] ADD an object/clothing layer (never replace) + COF link / take it off + remove only its COF link(s); body parts refused
  rez <item> | take <object>  rez her own Object item 1.5 m in front of her / take her own object back into Objects
  offer allow <object name> [min] | offer status | offer off   accept task-inventory offers ONLY from her own object with that name (default 5 min, one offer)
  outfit plan|create <name> [extra ids]   dry run / create an Outfit folder under My Outfits with links to the original items (COF minus LSL Bridge)
  invitem <item uuid>         check that an inventory item exists (FetchItem; never attaches)
  logout                      log out cleanly and exit";

    static async Task<string> Exec(string line)
    {
        if (line.Length == 0) return "";
        var sp = line.IndexOf(' ');
        var cmd = (sp < 0 ? line : line[..sp]).ToLowerInvariant();
        var rest = sp < 0 ? "" : line[(sp + 1)..].Trim();
        var a = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (cmd != "status" && cmd != "where" && cmd != "help" && line != "anim list") Log("cmd", line);
        if (cmd == "login") return await LoginAsync();
        if (!LoggedIn && cmd != "help" && cmd != "restart") return "not logged in" + (cmd is "status" or "where" ? "" : " (use login)");
        if (cmd is "say" or "shout" or "whisper" || (cmd == "chan" && a.Length > 0 && a[0] == "0")) { var qg = QuietChatGuard(); if (qg != null) { Log("quiet", $"manual {cmd} {qg}"); return qg; } }
        if (cmd is "say" or "shout" or "whisper" or "chan" or "im") { var rg = RateGuard(); if (rg != null) return rg; WanderOwnChat(); }
        switch (cmd)
        {
            case "help": return string.Format(Help, autoLure ? "on" : "off");
            case "status":
            case "where":
            {
                var sim = client.Network.CurrentSim;
                using var proc = Process.GetCurrentProcess();
                return $"connected={client.Network.Connected} region={sim?.Name} pos={Fmt(client.Self.SimPosition)} " +
                       $"sitting_on={(client.Self.SittingOn == 0 ? "-" : SeatName(client.Self.SittingOn))} follow={(followId == UUID.Zero ? "-" : followName)} " +
                       $"autolure={(autoLure ? "on" : "off")} pending_lure={(pendingLure?.name ?? "-")} uptime={(DateTime.Now - started):hh\\:mm\\:ss} " +
                       $"rss_mb={proc.WorkingSet64 / 1048576} objects={sim?.ObjectsPrimitives.Count} avatars={sim?.ObjectsAvatars.Count} quiet={QuietFlag()} ao={AoFlag()}";
            }
            case "say": case "shout": case "whisper":
            {
                if (rest.Length == 0) return "usage: say <text>";
                var t = cmd == "say" ? ChatType.Normal : cmd == "shout" ? ChatType.Shout : ChatType.Whisper;
                client.Self.Chat(rest, 0, t);
                Log("me-chat", $"({cmd}) {rest}");
                return "ok";
            }
            case "chan":
            {
                if (a.Length < 2 || !int.TryParse(a[0], out var ch)) return "usage: chan <n> <text>";
                var text = rest[a[0].Length..].Trim();
                client.Self.Chat(text, ch, ChatType.Normal);
                Log("me-chat", $"(channel {ch}) {text}");
                return "ok";
            }
            case "im":
            {
                var (target, text) = SplitTarget(rest);
                if (target.Length == 0 || text.Length == 0) return "usage: im <name|uuid> <text>";
                var id = await ResolveAvatar(target);
                if (id == UUID.Zero) return $"could not resolve avatar '{target}'";
                client.Self.InstantMessage(id, text);
                Log("me-im", $"to {NameOf(id)} ({id}): {text}");
                return $"sent to {NameOf(id)} ({id})";
            }
            case "nearby":
            {
                float r = 20; if (a.Length > 0 && !F(a[0], out r)) return "usage: nearby [radius]";
                return "AVATARS\n" + AvatarList() + $"OBJECTS within {r} m\n" + await Objects(r, "");
            }
            case "avatars": return AvatarList();
            case "objects":
            {
                float r = 20; string filt = "";
                if (a.Length > 0 && F(a[0], out var rr)) { r = rr; filt = string.Join(' ', a.Skip(1)); } else filt = rest;
                return await Objects(r, filt);
            }
            case "find": return rest.Length == 0 ? "usage: find <name filter>" : await Objects(64, rest);
            case "objinfo":
            {
                if (!UUID.TryParse(rest, out var id)) return "usage: objinfo <uuid>";
                var sim = Sim;
                var p = sim.ObjectsPrimitives.Values.FirstOrDefault(x => x.ID == id);
                if (p == null) return "object not in view";
                await EnsureProperties(sim, new() { p });
                var pos = PositionHelper.GetPrimPosition(sim, p);
                var c = p.Textures?.DefaultTexture?.RGBA;
                var sit = Sitters(sim);
                return $"name={p.Properties?.Name} desc={p.Properties?.Description} owner={(p.Properties != null ? NameOf(p.Properties.OwnerID) : "?")} " +
                       $"pos={Fmt(pos)} dist={Vector3.Distance(pos, client.Self.SimPosition):F1}m localid={p.LocalID} parent={p.ParentID} " +
                       $"color0={(c.HasValue ? $"r{c.Value.R:F2} g{c.Value.G:F2} b{c.Value.B:F2} a{c.Value.A:F2}" : "?")} " +
                       $"occupied={(sit.TryGetValue(p.LocalID, out var l) ? string.Join(",", l) : "no")} " +
                       // 2026-09-26 11:30: what a click does (teleporters/doors): hover text, click action, touch/sit menu text, scripted
                       $"click={p.ClickAction} touchname='{p.Properties?.TouchName}' sitname='{p.Properties?.SitName}' " +
                       $"scripted={((p.Flags & PrimFlags.Scripted) != 0 ? "yes" : "no")} touch={((p.Flags & PrimFlags.Touch) != 0 ? "yes" : "no")} " +
                       $"text='{(p.Text ?? "").Replace("\n", " / ")}'";
            }
            case "sit":
            {
                if (!UUID.TryParse(rest, out var id)) return "usage: sit <object uuid>";
                followId = UUID.Zero; client.Self.AutoPilotCancel();
                await DetachSeatOffBeforeSit(); // AO off before sitting (attach-block.txt)
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void H(object s, AvatarSitResponseEventArgs e) => tcs.TrySetResult(true);
                client.Self.AvatarSitResponse += H;
                try
                {
                    client.Self.RequestSit(id, Vector3.Zero);
                    var got = await Task.WhenAny(tcs.Task, Task.Delay(6000)) == tcs.Task;
                    client.Self.Sit();
                    await Task.Delay(2500);
                    var on = client.Self.SittingOn;
                    if (got) ScheduleSitGuard(id);
                    return got
                        ? (on != 0 ? $"seated (on {SeatName(on)})" : "sit response received and AgentSit sent; not confirmed yet (check 'status')")
                        : "no sit response from simulator within 6 s (object too far, no room, or not sittable); AgentSit sent anyway";
                }
                finally { client.Self.AvatarSitResponse -= H; }
            }
            case "height": case "diag": return await HeightDiag();
            case "hover": return await HoverCmd(a);
            case "rebake": { try { await client.Appearance.RequestSetAppearance(true); return "appearance update (server bake) requested"; } catch (Exception ex) { return "rebake failed: " + ex.GetBaseException().Message; } }
            case "anim": return AnimCmd(a);
            case "mute": case "unmute": case "mutelist": return await MuteCmds(cmd, rest);
            case "walk_path": case "goto_avatar": case "sit_near": case "walk_status": case "walk_stop": case "map": case "terrain":
                if (cmd is "walk_path" or "goto_avatar" or "sit_near" && WanderBlocksManualWalk) return "wander is running: 'wander pause' (or 'wander stop') first";
                return await NavCmds(cmd, rest, a);
            case "wander": return await WanderCmds(a);
            case "quiet": return QuietCmds(a);
            case "pose": return await PoseCmd(a);
            case "watchdog": return a.Length > 0 && a[0] == "selftest" ? WatchdogSelfTest() : WatchdogStatus();
            case "ao": return a.Length > 0 && a[0] == "selftest" ? AoSelfTest() : AoStatus();
            case "offlineim": return a.Length > 0 && a[0] == "selftest" ? OfflineImSelfTest() : OfflineImStatus();
            case "route": case "routes": case "goto_place": case "overhead": case "snapshot": return await RouteCmds(cmd, rest, a);
            case "offers": return await OffersCmd(a);
            case "friend": case "friends": return await FriendCmd(a);
            case "landmark": case "landmarks": case "lm": return await LandmarkCmd(a, rest);
            case "sethome": return await SetHomeCmd();
            case "parcel": return await ParcelCmd(a);
            case "texture" when a.Length > 0 && a[0] == "save": case "faces": case "vendor" when a.Length > 0 && a[0] == "look": return await TextureCmds(cmd, a, rest);
            case "worn" when a.Length >= 2 && a[0] == "links": return await WornLinks(rest.Substring(rest.IndexOf("links") + 5).Trim().Trim('"'));
            case "touch-attachment": case "touchatt": return await TouchAttachment(rest);
            case "shape": return await ShapeCmd(a);
            case "worn": case "detach": case "attach": case "animwatch": case "posekeeper": return await AttachCmds(cmd, a);
            case "inv" when a.Length >= 2 && a[0] == "ls": return await WearOpsCmd("invls", a[1..]);
            case "inv" when a.Length == 2 && a[0] == "read":
            {   // 2026-09-27 (David's gift): READ-ONLY - print the text of one of her own notecards (never modifies anything)
                if (!UUID.TryParse(a[1], out var nid)) return "usage: inv read <notecard item uuid>";
                InventoryItem nit;
                try { using var t = new CancellationTokenSource(15000); nit = await client.Inventory.FetchItemAsync(nid, client.Self.AgentID, t.Token); }
                catch (Exception ex) { return "fetch failed: " + ex.GetBaseException().Message; }
                if (nit == null) return "item not found";
                if (nit.AssetType != AssetType.Notecard) return $"'{nit.Name}' is a {nit.AssetType}, not a notecard";
                LibreMetaverse.Assets.Asset asset;
                try { using var t2 = new CancellationTokenSource(30000); asset = await client.Assets.RequestInventoryAssetAsync(nit, true, UUID.Random(), t2.Token); }
                catch (Exception ex) { return "download failed: " + ex.GetBaseException().Message; }
                if (asset is not LibreMetaverse.Assets.AssetNotecard nc) return $"download returned {(asset == null ? "nothing" : asset.GetType().Name)}";
                try { nc.Decode(); } catch (Exception ex) { return "decode failed: " + ex.GetBaseException().Message; }
                var body = nc.BodyText ?? "";
                Log("inv", $"read notecard '{nit.Name}' {nid}: {body.Length} chars, {nc.EmbeddedItems?.Count ?? 0} embedded item(s)");
                return $"notecard '{nit.Name}' ({body.Length} chars{(nc.EmbeddedItems?.Count > 0 ? ", embedded: " + string.Join(", ", nc.EmbeddedItems.Select(e => e.Name)) : "")}):\n" + (body.Length > 6000 ? body[..6000] + "\n(... truncated)" : body);
            }
            case "wear": case "rez": case "take": case "offer": return await WearOpsCmd(cmd, a);
            case "inv": return a.Length >= 2 && a[0] == "find" ? await InvFind(rest.Substring(rest.IndexOf("find") + 4).Trim()) : "usage: inv find <text>[|text2...]";
            case "outfit": return await OutfitCmd(a);
            case "sitguard":
                if (a.Length == 1 && (a[0] == "on" || a[0] == "off")) sitGuard = a[0] == "on";
                return $"sit guard {(sitGuard ? "on" : "off")}; last check: {lastHeightCheck}";
            case "stand":
                Interlocked.Increment(ref sitGen);
                client.Self.Stand();
                await Task.Delay(800);
                return client.Self.SittingOn == 0 ? "standing" : "stand sent (still reported seated)";
            case "moveto": case "goto":
            {
                if (client.Self.SittingOn == 0 && !AoStateNow().active) { AoLog($"moveto REFUSED: {AoStateNow().why}"); return "refused: AO not active (" + AoStateNow().why + "); not walking"; }
                if (a.Length != 3 || !F(a[0], out var x) || !F(a[1], out var y) || !F(a[2], out var z)) return "usage: moveto <x> <y> <z>  (region-local)";
                followId = UUID.Zero;
                Utils.LongToUInts(Sim.Handle, out var rx, out var ry);
                client.Self.AutoPilot(x + (double)rx, y + (double)ry, z);
                return $"autopilot to {Fmt(new Vector3(x, y, z))} (from {Fmt(client.Self.SimPosition)})";
            }
            case "walk":
            {
                if (a.Length < 1 || !F(a[0], out var m)) return "usage: walk <meters>";
                if (client.Self.SittingOn != 0 || !AoStateNow().active) { AoLog($"walk REFUSED: {AoStateNow().why}"); return "refused: AO not active or seated (" + AoStateNow().why + "); not walking"; }
                var d0 = Vector3.UnitX * client.Self.SimRotation; var dir = new Vector3(d0.X, d0.Y, 0f);
                if (dir.Length() < 0.01f) dir = Vector3.UnitX; dir = Vector3.Normalize(dir);
                var tgt = client.Self.SimPosition + dir * m;
                Utils.LongToUInts(Sim.Handle, out var rx, out var ry);
                client.Self.AutoPilot(tgt.X + (double)rx, tgt.Y + (double)ry, tgt.Z);
                return $"walking {m} m to {Fmt(tgt)}";
            }
            case "turn":
            {
                if (a.Length < 1 || !F(a[0], out var deg)) return "usage: turn <degrees>  (positive = left/counter-clockwise)";
                var d0 = Vector3.UnitX * client.Self.SimRotation; var dir = new Vector3(d0.X, d0.Y, 0f);
                if (dir.Length() < 0.01f) dir = Vector3.UnitX; dir = Vector3.Normalize(dir);
                var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, deg * MathF.PI / 180f);
                var nd = dir * rot;
                client.Self.Movement.TurnToward(client.Self.SimPosition + nd * 5f);
                return $"turned {deg} deg";
            }
            case "face":
            {
                Vector3 tgt;
                if (a.Length == 3 && F(a[0], out var x) && F(a[1], out var y) && F(a[2], out var z)) tgt = new Vector3(x, y, z);
                else
                {
                    var av = Avatars().FirstOrDefault(t => t.av.Name.Equals(rest, StringComparison.OrdinalIgnoreCase));
                    if (av.av == null) return "usage: face <x y z | avatar name in view>";
                    tgt = av.pos;
                }
                client.Self.Movement.TurnToward(tgt);
                return $"facing {Fmt(tgt)}";
            }
            case "stop":
                if (WanderOn) StopWander("stop command");
                followId = UUID.Zero; client.Self.AutoPilotCancel();
                return "stopped";
            case "teleport": case "tp":
            {
                bool tpForce = a.Length > 0 && a[0].Equals("force", StringComparison.OrdinalIgnoreCase); if (tpForce) a = a[1..];
                if (a.Length < 4 || !F(a[^3], out var x) || !F(a[^2], out var y) || !F(a[^1], out var z)) return "usage: teleport [force] <region name> <x> <y> <z>";
                var region = string.Join(' ', a[..^3]);
                {   // 2026-09-27 (David): the robe is worn only at the Buddha Center
                    bool robeOn = RobeMaybeWorn(out var robeHow);
                    var block = RobeTpBlock(region, robeOn, tpForce);
                    if (block != null) { Log("robe", $"WARNING teleport to {region} {x},{y},{z} {block} ({robeHow})"); return block; }
                    if (robeOn && tpForce && !region.Equals(HomeSeatRegion, StringComparison.OrdinalIgnoreCase)) Log("robe", $"WARNING forced teleport to {region} while the robe is worn ({robeHow})");
                }
                followId = UUID.Zero;
                var ok = await client.Self.TeleportAsync(region, new Vector3(x, y, z));
                return ok ? $"teleported to {client.Network.CurrentSim?.Name} {Fmt(client.Self.SimPosition)}" : $"teleport failed: {client.Self.TeleportMessage}";
            }
            case "home":
            {
                {   // home is Firestorm Orientation (outside the Buddha Center): same robe rule as teleport
                    bool robeOn = RobeMaybeWorn(out var robeHow);
                    var block = RobeTpBlock("home (Firestorm Orientation)", robeOn, rest.Equals("force", StringComparison.OrdinalIgnoreCase));
                    if (block != null) { Log("robe", $"WARNING go home {block} ({robeHow})"); return block; }
                }
                var ok = await client.Self.GoHomeAsync();
                return ok ? $"home: {client.Network.CurrentSim?.Name}" : $"go home failed: {client.Self.TeleportMessage}";
            }
            case "follow":
            {
                if (rest.Length == 0 || rest.Equals("off", StringComparison.OrdinalIgnoreCase))
                { followId = UUID.Zero; client.Self.AutoPilotCancel(); return "follow off"; }
                var av = Avatars().FirstOrDefault(t => t.av.Name.Equals(rest, StringComparison.OrdinalIgnoreCase) ||
                                                        t.av.Name.Equals(rest + " Resident", StringComparison.OrdinalIgnoreCase));
                if (av.av == null) return $"'{rest}' is not in view (must be in the same region and within draw distance)";
                if (client.Self.SittingOn != 0) client.Self.Stand();
                followId = av.av.ID; followName = av.av.Name;
                return $"following {followName} ({av.dist:F1} m away)";
            }
            case "accept":
            {
                if (pendingLure is not { } pl) return "no pending teleport offer";
                bool robeAcc = RobeMaybeWorn(out var robeHowA);
                if (robeAcc) Log("robe", $"WARNING accepting teleport from {pl.name} while the VIOLETTE robe is worn ({robeHowA}); the robe should stay at the Buddha Center");
                client.Self.TeleportLureRespond(pl.from, pl.session, true); pendingLure = null;
                return $"accepted teleport from {pl.name}" + (robeAcc ? " (WARNING: the robe is worn; the robe stays at the Buddha Center - change to 'Original' if she leaves it)" : "");
            }
            case "decline":
            {
                if (pendingLure is not { } pl) return "no pending teleport offer";
                client.Self.TeleportLureRespond(pl.from, pl.session, false); pendingLure = null;
                return $"declined teleport from {pl.name}";
            }
            case "touch":
            {   // touch an object (e.g. a HUD or seat to open its own menu); logged
                if (a.Length != 1 || !UUID.TryParse(a[0], out var tid)) return "usage: touch <object uuid>";
                var sim = Sim; var tp = sim.ObjectsPrimitives.Values.FirstOrDefault(x => x != null && x.ID == tid);
                if (tp == null) return "object not found in this region's object list";
                client.Self.Touch(tp.LocalID); Log("touch", $"touched {tid} (local {tp.LocalID})");
                return $"touched {tid}; any menu shows up as a [dialog] log line ('dialog <button>' answers it)";
            }
            case "dialog":
            {
                var d = lastDialog;
                if (d == null) return "no script dialog received";
                var idx = d.ButtonLabels.FindIndex(b => b.Trim().Equals(rest, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) return $"no button '{rest}'; buttons: {string.Join(" | ", d.ButtonLabels)}";
                client.Self.ReplyToScriptDialog(d.Channel, idx, d.ButtonLabels[idx], d.ObjectID);
                return $"pressed '{d.ButtonLabels[idx]}' on '{d.ObjectName}'";
            }
            case "autolure":
                if (rest == "on") autoLure = true; else if (rest == "off") autoLure = false; else return "usage: autolure on|off";
                return "autolure " + rest;
            case "profile":
            {
                var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "get";
                if (sub == "get") return JsonSerializer.Serialize(await GetProfileObj());
                if (sub == "image-id") return JsonSerializer.Serialize(await SetProfileImageIdObj(a.Length > 1 ? a[1] : ""));
                if (sub == "fl-image-id") return JsonSerializer.Serialize(await SetFirstLifeImageIdObj(a.Length > 1 ? a[1] : ""));
                if (sub == "fl-set")
                {
                    var text = rest.Length > 6 ? rest[6..].Trim() : "";
                    if (text.Length == 0) return "usage: profile fl-set <first life text>";
                    text = text.Replace("\\n", "\n");
                    return JsonSerializer.Serialize(await SetFirstLifeAboutObj(text));
                }
                if (sub == "image")
                {
                    var path = rest.Length > 5 ? rest[5..].Trim() : "";
                    if (path.Length == 0) return "usage: profile image <path to .j2c codestream> | profile image clear";
                    if (path.Equals("clear", StringComparison.OrdinalIgnoreCase) || path.Equals("none", StringComparison.OrdinalIgnoreCase))
                        return JsonSerializer.Serialize(await ClearProfileImageObj());
                    return JsonSerializer.Serialize(await SetProfileImageObj(path));
                }
                if (sub == "set")
                {
                    var text = rest.Length > 3 ? rest[3..].Trim() : "";
                    if (text.Length == 0) return "usage: profile set <about text>";
                    // socket is one line per command: allow literal \n (backslash-n) as a line break
                    text = text.Replace("\\n", "\n");
                    return JsonSerializer.Serialize(await SetAboutObj(text));
                }
                return "usage: profile get | profile set <about text> | profile fl-set <first life text> | profile fl-image-id <uuid>";
            }
            case "restart": case "sit_home": case "invitem": return await RestartCmds(cmd, a);
            case "group": case "groups": return await GroupCmds(a);
            case "pick": case "picks": return await PickCmds(rest, a);
            case "webhook_test": return await GalatayMcp.Webhook.Test();
            case "displayname":
            {
                var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "get";
                if (sub == "get") return JsonSerializer.Serialize(await DisplayNameObj());
                if (sub == "set") { var n = rest.Length > 3 ? rest[3..].Trim() : ""; return n.Length == 0 ? "usage: displayname set <name>" : JsonSerializer.Serialize(await SetDisplayNameObj(n)); }
                return "usage: displayname get | displayname set <name>";
            }
            case "balance": { var b = await BalanceAsync(); return b.HasValue ? $"L$ {b.Value}" : "no balance reply"; }
            case "prices": return JsonSerializer.Serialize(await UploadPriceObj());
            case "caps": return JsonSerializer.Serialize(CapsObj());
            case "upload":
            {
                var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "";
                if (sub == "quote" && a.Length >= 2) return JsonSerializer.Serialize(await UploadQuoteObj(a[1], a.Length > 2 && a[2].Equals("probe", StringComparison.OrdinalIgnoreCase)));
                if (sub == "texture" && a.Length >= 4 && int.TryParse(a[1], out var maxL))
                {
                    var name = string.Join(' ', a.Skip(3));
                    return JsonSerializer.Serialize(await PaidTextureUploadObj(maxL, a[2], name));
                }
                return "usage: upload quote <file.j2c> [probe] | upload texture <maxL$> <file.j2c> <name>";
            }
            case "webhook":
                if (a.Length > 0 && a[0].Equals("test", StringComparison.OrdinalIgnoreCase)) return await GalatayMcp.Webhook.Test();
                if (a.Length > 0 && a[0].Equals("debounce", StringComparison.OrdinalIgnoreCase)) return a.Length > 1 && a[1] == "selftest" ? await GalatayMcp.Webhook.DebounceSelfTest() : GalatayMcp.Webhook.DebounceCmd(a[1..]);
                return GalatayMcp.Webhook.ConfigSummary();
            case "logout": case "quit": case "exit":
                _ = Task.Run(async () => { await Task.Delay(200); await Shutdown("logout command"); });
                return "logging out";
            default:
                return $"unknown command '{cmd}'. Try 'help'.";
        }
    }
}
