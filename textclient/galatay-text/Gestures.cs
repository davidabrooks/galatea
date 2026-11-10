// Gestures.cs (2026-11-09, David t3205u: "Now you can build gesture support").
// `gesture list | show <g> | play <g> | stop`, MCP tool `gesture`. A gesture is played the way the SL viewer's LLGestureMgr plays it,
// by our own step player, not LibreMetaverse's AgentManager.PlayGestureAsync. In LMV 3.3.0 that method:
//   - ignores "wait until animations are done" (the step is a TODO), so such a gesture ends at once;
//   - sends chat steps straight to Self.Chat, past our RLV @sendchat, quiet-mode and rate guards and the chat log;
//   - treats ANY non-zero animation flag as "stop" (the format has only bit 0x01 = stop);
//   - never stops what it started, logs nothing, reports nothing (a failed download or decode just returns), and puts a
//     30 s limit on the asset download and the whole gesture together.
// LMV's AssetGesture parser has the same flag reading and keeps '\r' in chat text, so the asset is parsed here too (GestureParse).
// LMV's asset download (ViewerAsset cap + its disk cache) is used as is.
// Viewer semantics kept: an animation step sends a start (a restart if it already plays); "wait for animations" waits until every
// animation this gesture asked for has played and stopped (max 30 s); at the end the gesture waits for its animations (max 30 s),
// then stops any that still play, so she is back to her normal AO stand. Chat steps say the text in nearby chat ("/5 text" = channel 5),
// like a viewer does. Chat triggers: a word of her own nearby chat that is an ACTIVE gesture's trigger plays it; the word is replaced
// by the gesture's replace text (or dropped), and nothing is said if nothing is left (LLGestureMgr::triggerAndReviseString).
// AO / idle: animations a gesture started are never learned as AO stands or sits, never seat-off animations, and do not count as a
// posture change for the hover check; while a gesture plays the idle timer keeps restarting (no idle sit or kneel on top of it), and an
// idle pose is cleared (she stands) before a gesture plays.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal enum GStepKind { Anim, Sound, Chat, Wait }
    internal sealed record GStep(GStepKind Kind, string Name = "", UUID Id = default, bool Stop = false, string Text = "", float Seconds = 0, bool WaitTime = false, bool WaitAnims = false);
    internal sealed record GestureDef(string Trigger, string ReplaceWith, List<GStep> Steps);

    // ---- pure: the asset text (LLMultiGesture::serialize, version 2) ----
    internal static (GestureDef def, string error) GestureParse(byte[] data) => GestureParse(Encoding.UTF8.GetString(data ?? Array.Empty<byte>()));
    internal static (GestureDef def, string error) GestureParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text?.Replace("\0", ""))) return (null, "empty gesture asset");
        var lines = text.Replace("\0", "").Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);   // the final newline (and a NUL) end the asset
        int i = 0;
        string Next(string what) => i < lines.Count ? lines[i++] : throw new FormatException($"the asset ends before the {what} (line {i + 1})");
        long Num(string what) { var s = Next(what).Trim(); return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new FormatException($"line {i}: {what} '{s}' is not a number"); }
        UUID Id(string what) { var s = Next(what).Trim(); return UUID.TryParse(s, out var u) ? u : throw new FormatException($"line {i}: {what} '{s}' is not a uuid"); }
        try
        {
            var ver = Num("version"); if (ver != 2) return (null, $"gesture asset version {ver} (only version 2 is known)");
            Num("key"); Num("key mask");
            var trigger = Next("trigger").Trim(); var replace = Next("replace text").Trim();
            var n = Num("step count"); if (n < 0 || n > 1000) return (null, $"bad step count {n}");
            var steps = new List<GStep>();
            for (int k = 0; k < n; k++)
            {
                var t = Num("step type");
                switch (t)
                {
                    case 0: { var name = Next("animation name"); var id = Id("animation id"); var fl = Num("animation flags"); steps.Add(new(GStepKind.Anim, name, id, Stop: (fl & 1) != 0)); break; }
                    case 1: { var name = Next("sound name"); var id = Id("sound id"); Num("sound flags"); steps.Add(new(GStepKind.Sound, name, id)); break; }
                    case 2: { var txt = Next("chat text"); Num("chat flags"); steps.Add(new(GStepKind.Chat, Text: txt)); break; }
                    case 3:
                    {
                        var s = Next("wait seconds").Trim();
                        if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)) throw new FormatException($"line {i}: wait seconds '{s}' is not a number");
                        var fl = Num("wait flags"); steps.Add(new(GStepKind.Wait, Seconds: sec, WaitTime: (fl & 1) != 0, WaitAnims: (fl & 2) != 0)); break;
                    }
                    case 4: k = (int)n; break;   // EOF
                    default: return (null, $"unknown step type {t} at step {k + 1}");
                }
            }
            return (new GestureDef(trigger, replace, steps), null);
        }
        catch (FormatException ex) { return (null, ex.Message); }
    }

    // pure: "/5 hello" -> channel 5 "hello"; anything else is nearby chat (channel 0)
    internal static (int channel, string text) GestureChatChannel(string t)
    {
        var m = Regex.Match(t ?? "", @"^/(-?\d+)\s*(.*)$", RegexOptions.Singleline);
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? (c, m.Groups[2].Value) : (0, t ?? "");
    }

    internal static string GestureStepText(GStep s) => s.Kind switch
    {
        GStepKind.Anim => $"{(s.Stop ? "stop" : "start")} animation '{s.Name}' {s.Id}",
        GStepKind.Sound => $"sound '{s.Name}' {s.Id}",
        GStepKind.Chat => $"chat '{s.Text}'",
        _ => "wait " + (s.WaitTime ? $"{s.Seconds.ToString("0.##", CultureInfo.InvariantCulture)} s" : s.WaitAnims ? "until its animations are done" : "(nothing: no wait flag)"),
    };

    // pure: which gesture a name / trigger means. Tiers: active trigger, any trigger, exact name (with or without a leading '/'), then a
    // unique part of a name or trigger. Several exact matches in a tier: the active one first, then the first listed.
    internal static (int idx, string error) GestureResolve(string query, IReadOnlyList<(string name, string trigger, bool active)> items)
    {
        var q = (query ?? "").Trim(); if (q.Length == 0) return (-1, "usage: gesture play <name or trigger>");
        static bool Eq(string a, string b) => !string.IsNullOrEmpty(a) && string.Equals(a.Trim(), b, StringComparison.OrdinalIgnoreCase);
        var tiers = new Func<(string name, string trigger, bool active), bool>[]
        {
            x => x.active && Eq(x.trigger, q),
            x => Eq(x.trigger, q),
            x => Eq(x.name, q) || Eq(x.name?.TrimStart('/'), q.TrimStart('/')),
        };
        foreach (var tier in tiers)
        {
            var hits = Enumerable.Range(0, items.Count).Where(k => tier(items[k])).ToList();
            if (hits.Count > 0) return (hits.OrderByDescending(k => items[k].active).First(), null);
        }
        var part = Enumerable.Range(0, items.Count).Where(k => (items[k].name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) || (items[k].trigger ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (part.Count == 1) return (part[0], null);
        if (part.Count > 1) return (-1, $"could not pick: ambiguous gesture '{q}': {string.Join(", ", part.Take(8).Select(k => $"'{items[k].name}'"))}{(part.Count > 8 ? $" (+{part.Count - 8} more)" : "")}; nothing played");
        return (-1, $"unknown gesture '{q}' (see `gesture list`); nothing played");
    }

    // pure: her own chat line with an active gesture's trigger word (LLGestureMgr::triggerAndReviseString): only the first trigger word
    // plays; it is replaced by the replace text (her capitalization kept when they are equal) or dropped. hit = index into active, -1 = none.
    internal static (string text, int hit) GestureTriggerRewrite(string text, IReadOnlyList<(string trigger, string replace)> active)
    {
        var toks = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries); var outp = new List<string>(); int hit = -1;
        foreach (var tok in toks)
        {
            if (hit < 0)
            {
                int k = -1; for (int j = 0; j < active.Count; j++) if (!string.IsNullOrWhiteSpace(active[j].trigger) && string.Equals(tok, active[j].trigger.Trim(), StringComparison.OrdinalIgnoreCase)) { k = j; break; }
                if (k >= 0)
                {
                    hit = k; var r = active[k].replace?.Trim() ?? "";
                    if (r.Length > 0) outp.Add(string.Equals(tok, r, StringComparison.OrdinalIgnoreCase) ? tok : r);
                    continue;
                }
            }
            outp.Add(tok);
        }
        return hit < 0 ? (text, -1) : (string.Join(' ', outp), hit);
    }

    // ---- the step player (pure over IGestureIo; tests drive it with a fake clock) ----
    internal interface IGestureIo
    {
        DateTime Now { get; }
        Task Delay(int ms, CancellationToken ct);
        bool AnimPlaying(UUID id);
        bool AnimSeenSince(UUID id, DateTime t);   // a start of this animation was seen at or after t
        void AnimStart(UUID id);
        void AnimStop(UUID id);
        string Sound(UUID id);                       // null = played, else why it was skipped
        string Chat(int channel, string text);       // null = said, else why it was skipped
        void Log(string msg);
    }
    internal sealed record GestureLimits(double MaxSec = 120, double MaxWaitSec = 60, double WaitAnimSec = 30, double EndWaitSec = 30, double NotSeenSec = 3, int PollMs = 100);
    internal sealed record GestureRunResult(bool Ok, string Error, int StepsRun, int Chats, int Skipped, List<UUID> Started, List<UUID> StoppedAtEnd, double Seconds);

    internal static async Task<GestureRunResult> GestureRun(string label, GestureDef g, IGestureIo io, GestureLimits lim, CancellationToken ct)
    {
        var t0 = io.Now; var requested = new Dictionary<UUID, DateTime>(); var started = new List<UUID>(); var stoppedAtEnd = new List<UUID>();
        int chats = 0, skipped = 0, run = 0; string err = null;
        bool Finished(UUID id, DateTime at) => !io.AnimPlaying(id) && (io.AnimSeenSince(id, at) || (io.Now - at).TotalSeconds >= lim.NotSeenSec);
        bool AllDone() => requested.All(kv => Finished(kv.Key, kv.Value));
        double Left() => lim.MaxSec - (io.Now - t0).TotalSeconds;
        io.Log($"'{label}': start, {g.Steps.Count} step(s), trigger '{g.Trigger}'");
        try
        {
            for (int k = 0; k < g.Steps.Count; k++)
            {
                ct.ThrowIfCancellationRequested();
                if (Left() <= 0) { err = $"timeout: the gesture ran {lim.MaxSec:0} s, stopped before step {k + 1}"; break; }
                var s = g.Steps[k]; var n = $"step {k + 1}/{g.Steps.Count}";
                switch (s.Kind)
                {
                    case GStepKind.Anim when s.Stop:
                        io.AnimStop(s.Id); requested.Remove(s.Id); io.Log($"{n} anim stop '{s.Name}' {s.Id}"); break;
                    case GStepKind.Anim:
                    {
                        bool was = io.AnimPlaying(s.Id); if (was) io.AnimStop(s.Id);
                        io.AnimStart(s.Id); requested[s.Id] = io.Now; if (!started.Contains(s.Id)) started.Add(s.Id);
                        io.Log($"{n} anim start '{s.Name}' {s.Id}{(was ? " (it was playing: restarted)" : "")}"); break;
                    }
                    case GStepKind.Sound:
                    {
                        var why = io.Sound(s.Id);
                        if (why == null) io.Log($"{n} sound '{s.Name}' {s.Id}"); else { skipped++; io.Log($"{n} sound '{s.Name}' skipped: {why}"); }
                        break;
                    }
                    case GStepKind.Chat:
                    {
                        var (ch, txt) = GestureChatChannel(s.Text);
                        if (txt.Trim().Length == 0) { skipped++; io.Log($"{n} chat skipped: empty text"); break; }
                        var why = io.Chat(ch, txt);
                        if (why == null) { chats++; io.Log($"{n} chat{(ch != 0 ? $" on channel {ch}" : "")}: {txt}"); } else { skipped++; io.Log($"{n} chat '{txt}' skipped: {why}"); }
                        break;
                    }
                    case GStepKind.Wait when s.WaitTime:
                    {
                        var sec = Math.Min(Math.Max(0, s.Seconds), Math.Min(lim.MaxWaitSec, Math.Max(0, Left())));
                        io.Log($"{n} wait {sec.ToString("0.##", CultureInfo.InvariantCulture)} s{(sec < s.Seconds ? $" (asked {s.Seconds.ToString("0.##", CultureInfo.InvariantCulture)} s, capped)" : "")}");
                        await io.Delay((int)(sec * 1000), ct); break;
                    }
                    case GStepKind.Wait when s.WaitAnims:
                    {
                        var w0 = io.Now;
                        while (!AllDone() && (io.Now - w0).TotalSeconds < lim.WaitAnimSec && Left() > 0) await io.Delay(lim.PollMs, ct);
                        io.Log($"{n} wait for animations: {(AllDone() ? "done" : $"waited too long ({(io.Now - w0).TotalSeconds:0.#} s), continuing")}"); break;
                    }
                    default: io.Log($"{n} wait step without a wait flag: nothing to wait for"); break;
                }
                run++;
            }
            if (err == null)
            {
                var e0 = io.Now;
                while (!AllDone() && (io.Now - e0).TotalSeconds < lim.EndWaitSec && Left() > 0) await io.Delay(lim.PollMs, ct);
                if (!AllDone() && Left() <= 0) err = $"timeout: the gesture ran {lim.MaxSec:0} s";
            }
        }
        catch (OperationCanceledException) { err = "cancelled"; }
        foreach (var id in requested.Keys) if (io.AnimPlaying(id)) { io.AnimStop(id); stoppedAtEnd.Add(id); }
        if (stoppedAtEnd.Count > 0) io.Log($"'{label}': stopped {stoppedAtEnd.Count} of its animation(s) still playing at the end: {string.Join(", ", stoppedAtEnd.Select(x => x.ToString()[..8]))}");
        var secs = (io.Now - t0).TotalSeconds;
        io.Log(err == null ? $"'{label}': done in {secs:0.0} s ({run} step(s), {chats} chat, {skipped} skipped)" : $"'{label}': ERROR {err} after {secs:0.0} s ({run} step(s) run)");
        return new(err == null, err, run, chats, skipped, started, stoppedAtEnd, secs);
    }

    // ---- live state ----
    static readonly ConcurrentDictionary<UUID, GestureDef> gestureDefs = new();        // gesture asset id -> decoded
    static readonly ConcurrentDictionary<UUID, DateTime> gestureAnimUntil = new();     // animation started by a gesture -> owned until (grace after the end)
    static readonly ConcurrentDictionary<UUID, DateTime> animStartSeenAt = new();      // animation id -> its last start seen in AvatarAnimation
    static readonly object gestureLock = new();
    static CancellationTokenSource gestureCts; static Task<string> gestureTask; static volatile string gesturePlaying;
    internal static bool GestureAnimOwned(UUID id, DateTime now, IReadOnlyDictionary<UUID, DateTime> owned) => owned.TryGetValue(id, out var until) && now < until;
    static bool GestureAnimOwnedNow(UUID id) => GestureAnimOwned(id, DateTime.Now, gestureAnimUntil);
    static bool GesturePlayingNow() => gesturePlaying != null;
    // what she plays minus the animations a gesture owns (posture / AO checks)
    internal static List<UUID> WithoutGestureAnims(IEnumerable<UUID> playing, DateTime now, IReadOnlyDictionary<UUID, DateTime> owned) => playing.Where(a => !GestureAnimOwned(a, now, owned)).ToList();
    static void GestureNoteAnimStarted(UUID id) => animStartSeenAt[id] = DateTime.Now;

    sealed class LiveGestureIo : IGestureIo
    {
        readonly string label; public LiveGestureIo(string label) { this.label = label; }
        public DateTime Now => DateTime.Now;
        public Task Delay(int ms, CancellationToken ct) => Task.Delay(ms, ct);
        public bool AnimPlaying(UUID id) => client.Self.SignaledAnimations.ContainsKey(id);
        public bool AnimSeenSince(UUID id, DateTime t) => animStartSeenAt.TryGetValue(id, out var at) && at >= t;
        public void AnimStart(UUID id) { gestureAnimUntil[id] = DateTime.MaxValue; client.Self.AnimationStart(id, true); }
        public void AnimStop(UUID id) => client.Self.AnimationStop(id, true);
        public string Sound(UUID id)
        {
            if (QuietOn && DateTime.Now >= qOverrideUntil) return "quiet mode ON";
            client.Sound.PlaySound(id); return null;
        }
        public string Chat(int channel, string text)
        {
            if (channel == 0 && QuietChatGuard() is string q) return q;
            if (channel == 0 && RlvGuard("say", text) is string rb) { RlvLog($"gesture '{label}' chat '{text}' {rb}"); return rb; }
            if (RateGuard() is string rg) return rg;
            client.Self.Chat(text, channel, ChatType.Normal);
            if (channel == 0) NoteSpokeToNearby(ChatType.Normal);
            Program.Log("me-chat", $"(gesture '{label}'{(channel != 0 ? $", channel {channel}" : "")}) {text}");
            return null;
        }
        public void Log(string msg) => Program.Log("gesture", msg);
    }

    sealed record GestureItem(UUID Item, UUID Asset, string Name, string Path, bool Active);

    // the gestures she owns: the Gestures folder tree, the cached inventory index, and every active gesture (whatever folder it is in)
    static async Task<List<GestureItem>> GestureItems(CancellationToken ct)
    {
        var found = new Dictionary<UUID, GestureItem>();
        var active = client.Self.ActiveGestures;
        void Add(InventoryItem it, string path)
        {
            if (it == null || it.IsLink() || it.InventoryType != InventoryType.Gesture) return;
            found[it.UUID] = new(it.UUID, it.AssetUUID, it.Name, path, active.ContainsKey(it.UUID));
        }
        var root = client.Inventory.Store?.RootFolder;
        if (root != null)
        {
            var kids = await ReadFolderRO(root.UUID, ct);
            var gf = kids.OfType<InventoryFolder>().FirstOrDefault(f => f.PreferredType == FolderType.Gesture);
            var q = new Queue<(UUID id, string path)>(); if (gf != null) q.Enqueue((gf.UUID, "/" + gf.Name));
            for (int n = 0; q.Count > 0 && n < 200; n++)
            {
                var (fid, path) = q.Dequeue();
                foreach (var k in await ReadFolderRO(fid, ct, 10000))
                    if (k is InventoryFolder f) q.Enqueue((f.UUID, path + "/" + f.Name)); else if (k is InventoryItem it) Add(it, path);
            }
        }
        List<(string path, InventoryBase node)> snap; lock (invIndex) snap = invIndex.ToList();
        foreach (var (path, node) in snap) if (node is InventoryItem it && !found.ContainsKey(it.UUID)) Add(it, path);
        foreach (var kv in active)
            if (!found.ContainsKey(kv.Key))
            {
                InventoryItem it = null; try { it = await FetchItemRO(kv.Key, ct); } catch { }
                found[kv.Key] = new(kv.Key, kv.Value != UUID.Zero ? kv.Value : it?.AssetUUID ?? UUID.Zero, it?.Name ?? $"(active gesture {kv.Key.ToString()[..8]})", "", true);
            }
        return found.Values.OrderByDescending(x => x.Active).ThenBy(x => x.Path).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static async Task<(GestureDef def, string error)> GestureLoad(UUID asset, CancellationToken ct)
    {
        if (asset == UUID.Zero) return (null, "the item has no asset id (no permission to read it?)");
        if (gestureDefs.TryGetValue(asset, out var d)) return (d, null);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(15000);
        LibreMetaverse.Assets.Asset a;
        try { a = await client.Assets.RequestAssetAsync(asset, AssetType.Gesture, true, cts.Token); }
        catch (OperationCanceledException) { return (null, "the gesture asset download timed out (15 s)"); }
        catch (Exception ex) { return (null, "the gesture asset download threw " + ex.GetBaseException().Message); }
        if (a?.AssetData == null || a.AssetData.Length == 0) return (null, "the gesture asset download returned nothing");
        var (def, err) = GestureParse(a.AssetData);
        if (def != null) gestureDefs[asset] = def;
        return (def, err);
    }
    // load the not yet decoded ones, 4 at a time, within a time budget
    static async Task<int> GestureLoadAll(IEnumerable<GestureItem> items, TimeSpan budget, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(budget);
        int n = 0; using var gate = new SemaphoreSlim(4);
        var tasks = items.Where(x => x.Asset != UUID.Zero && !gestureDefs.ContainsKey(x.Asset)).Select(async x =>
        {
            try { await gate.WaitAsync(cts.Token); } catch { return; }
            try { if ((await GestureLoad(x.Asset, cts.Token)).def != null) Interlocked.Increment(ref n); } catch { } finally { gate.Release(); }
        });
        try { await Task.WhenAll(tasks); } catch { }
        return n;
    }
    static string GestureTrig(GestureItem x) => gestureDefs.TryGetValue(x.Asset, out var d) ? d.Trigger : null;
    static List<(string trigger, string replace)> GestureActiveTriggers(out List<UUID> assets)
    {
        var l = new List<(string, string)>(); assets = new();
        foreach (var asset in client.Self.ActiveGestures.Values)
            if (gestureDefs.TryGetValue(asset, out var d) && !string.IsNullOrWhiteSpace(d.Trigger)) { l.Add((d.Trigger, d.ReplaceWith)); assets.Add(asset); }
        return l;
    }
    // after login: decode the active gestures so chat triggers work without waiting on a download
    static void GestureWarmAfterLogin() => _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(60000); if (!LoggedIn) return;
            var act = client.Self.ActiveGestures.Select(kv => new GestureItem(kv.Key, kv.Value, "", "", true)).ToList();
            var n = await GestureLoadAll(act, TimeSpan.FromSeconds(60), CancellationToken.None);
            Log("gesture", $"login: {n} of {act.Count} active gesture(s) decoded for chat triggers");
        }
        catch (Exception ex) { Log("gesture", "login warm-up: " + ex.GetBaseException().Message); }
    });

    static async Task<string> GestureCmd(string[] a, string rest)
    {
        var sub = a.Length == 0 ? "list" : a[0].ToLowerInvariant();
        var arg = a.Length <= 1 ? "" : rest[(rest.IndexOf(a[0], StringComparison.Ordinal) + a[0].Length)..].Trim();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        switch (sub)
        {
            case "list":
            {
                var items = await GestureItems(cts.Token);
                var loaded = await GestureLoadAll(items, TimeSpan.FromSeconds(20), cts.Token);
                var sb = new StringBuilder($"{items.Count} gesture(s), {items.Count(x => x.Active)} active (* = active; trigger in []):\n");
                foreach (var x in items)
                {
                    var t = GestureTrig(x);
                    sb.AppendLine($"  {(x.Active ? "*" : " ")} '{x.Name}' [{(t == null ? "?" : t.Length == 0 ? "-" : t)}]{(gestureDefs.TryGetValue(x.Asset, out var d) ? $" {d.Steps.Count} step(s)" : "")} {x.Path} item {x.Item}");
                }
                if (items.Any(x => GestureTrig(x) == null)) sb.AppendLine($"  (? = not decoded yet; {loaded} decoded now)");
                return sb.ToString().TrimEnd();
            }
            case "show":
            {
                var (it, err) = await GestureFind(arg, cts.Token); if (it == null) return err;
                var (def, e2) = await GestureLoad(it.Asset, cts.Token); if (def == null) return $"error: gesture '{it.Name}': {e2}";
                var sb = new StringBuilder($"'{it.Name}'{(it.Active ? " (active)" : "")} trigger '{def.Trigger}' replace '{def.ReplaceWith}' item {it.Item} asset {it.Asset}, {def.Steps.Count} step(s):\n");
                for (int k = 0; k < def.Steps.Count; k++) sb.AppendLine($"  {k + 1}. {GestureStepText(def.Steps[k])}");
                return sb.ToString().TrimEnd();
            }
            case "play":
            {
                var (it, err) = await GestureFind(arg, cts.Token); if (it == null) return err;
                var (def, e2) = await GestureLoad(it.Asset, cts.Token); if (def == null) return $"error: gesture '{it.Name}': {e2}";
                var task = GesturePlayStart(it.Name, def, "command");
                if (await Task.WhenAny(task, Task.Delay(40000)) == task) return await task;
                return $"playing '{it.Name}' (still running after 40 s; the result is logged as [gesture])";
            }
            case "stop":
            {
                var p = gesturePlaying; CancellationTokenSource c; lock (gestureLock) c = gestureCts;
                if (p == null || c == null) return "no gesture is playing";
                try { c.Cancel(); } catch { }
                var t = gestureTask; if (t != null) await Task.WhenAny(t, Task.Delay(5000));
                return $"stopped '{p}'";
            }
            default: return "usage: gesture list | gesture show <name|trigger> | gesture play <name|trigger> | gesture stop";
        }
    }

    static async Task<(GestureItem item, string error)> GestureFind(string query, CancellationToken ct)
    {
        if (query.Trim().Length == 0) return (null, "usage: gesture play|show <name or trigger>");
        var items = await GestureItems(ct);
        if (items.Count == 0) return (null, "error: no gestures found in her inventory");
        (int idx, string err) Try() => GestureResolve(query, items.Select(x => (x.Name, GestureTrig(x), x.Active)).ToList());
        var r = Try();
        if (r.idx < 0 && items.Any(x => GestureTrig(x) == null)) { await GestureLoadAll(items, TimeSpan.FromSeconds(20), ct); r = Try(); }   // a trigger may be in an asset not decoded yet
        return r.idx < 0 ? (null, r.err) : (items[r.idx], null);
    }

    // one gesture at a time: a new one stops the one playing (like the viewer replaying a gesture)
    static Task<string> GesturePlayStart(string name, GestureDef def, string why)
    {
        CancellationTokenSource prev; Task<string> prevTask; var mine = new CancellationTokenSource();
        lock (gestureLock) { prev = gestureCts; prevTask = gestureTask; gestureCts = mine; }
        var t = Task.Run(async () =>
        {
            if (prev != null) { try { prev.Cancel(); } catch { } if (prevTask != null) await Task.WhenAny(prevTask, Task.Delay(5000)); }
            return await GesturePlay(name, def, why, mine);
        });
        lock (gestureLock) if (gestureCts == mine) gestureTask = t;
        return t;
    }

    static async Task<string> GesturePlay(string name, GestureDef def, string why, CancellationTokenSource cts)
    {
        var lim = new GestureLimits();
        cts.CancelAfter(TimeSpan.FromSeconds(lim.MaxSec + 10));
        string pre = "";
        if (idlePosed != null) { var r = await IdlePoseExit("gesture " + name); pre = $"stood up from the idle pose first ({r}); "; }
        bool standingBefore = client.Self.SittingOn == 0 && !AoSitReallyOn();
        gesturePlaying = name; IdleNoteActivity();
        GestureRunResult res;
        Log("gesture", $"'{name}' ({why}): play");
        try { res = await GestureRun(name, def, new LiveGestureIo(name), lim, cts.Token); }
        finally
        {
            gesturePlaying = null; IdleNoteActivity();
            foreach (var kv in gestureAnimUntil) if (kv.Value == DateTime.MaxValue) gestureAnimUntil[kv.Key] = DateTime.Now.AddSeconds(10);   // grace: a late stop packet is still hers
            lock (gestureLock) if (gestureCts == cts) { gestureCts = null; gestureTask = null; }
        }
        string after;
        if (client.Self.SittingOn != 0) after = "seated (as before)";
        else if (standingBefore && AoSitReallyOn()) { after = "an AO sit plays after the gesture: " + await AoGroundSitEnsureOff("after gesture " + name); }
        else after = "standing";
        Log("gesture", $"'{name}': after: {after}");
        var head = res.Ok ? $"played '{name}'" : $"error: gesture '{name}': {res.Error}";
        return $"{pre}{head}: {res.StepsRun}/{def.Steps.Count} step(s) in {res.Seconds:0.0} s, {res.Chats} chat line(s), {res.Skipped} skipped" +
               $"{(res.StoppedAtEnd.Count > 0 ? $", stopped {res.StoppedAtEnd.Count} animation(s) still playing" : "")}; after: {after}";
    }

    // `say`: a trigger word of an active gesture plays it (only gestures already decoded; never waits on a download). Returns the text
    // to say (maybe empty) and a note, or null when nothing matched.
    static (string text, string note)? GestureChatTrigger(string text)
    {
        if (Env("GT_GESTURE_CHAT_TRIGGERS", "on") == "off" || IsSafeword(text)) return null;
        var act = GestureActiveTriggers(out var assets); if (act.Count == 0) return null;
        var (rev, hit) = GestureTriggerRewrite(text, act); if (hit < 0) return null;
        var def = gestureDefs[assets[hit]];
        _ = GesturePlayStart(act[hit].trigger, def, "chat trigger").ContinueWith(t => Log("gesture", $"chat trigger '{act[hit].trigger}': {(t.IsFaulted ? "error " + t.Exception?.GetBaseException().Message : t.Result)}"), TaskScheduler.Default);
        return (rev, $"gesture '{act[hit].trigger}' played");
    }
}
