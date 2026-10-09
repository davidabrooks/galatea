// FastOutfit.cs (2026-10-09, David: "it didn't seem any faster" - FastChat acked his "change your outfit" in ~1 s, but the
// change itself waited ~60 s for the "Second Life chat" routine to start up). Now, for David only, when the fast-chat model
// marks his line as an outfit request (JSON "outfit": "random" | "top_color" | "<saved outfit name>"), the client does it:
//   1. FastOutfitStart (right after the API answer, ~1 s after his line): guards (FastOutfitBlock: not seated / no tub or
//      toilet re-dress pending / no other outfit change running / on the beach only the Bikini or a top colour), then in the
//      background: validate the request against the real My Outfits list (ResolveFastOutfit: exact name, else fuzzy word
//      match; random = the daily-outfit pool minus what she wears), wear it with the usual rules (WearOutfitWithHuds: HUD
//      colours randomized on a real change, shorts matched to the top per _clothing-huds.json, single-colour tops no HUD
//      press; the Bikini via BikiniOn; top_color = the outfit's clothing HUDs pressed again).
//   2. FastOutfitBegin: the short ack goes out (typing pace as usual) WITHOUT claiming; the webhook keeps holding the line.
//   3. Done: a short follow-up written by the model ("All changed~ what do you think?"), typed at its pace, sent claiming the
//      line (say/im --re), so the routine never redoes it. Failed / not understood / blocked: the hold ends and the line goes
//      to the routine with fast_ack, exactly as before.
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal sealed record FastOutfitRun(bool Ok, string Kind, string Name, string Why, long Ms);
    static readonly List<string> fastOutfitNames = new();   // My Outfits names seen last (for the model's state line)
    static string fastOutfitLast = "-";
    static DateTime fastOutfitNamesAt = DateTime.MinValue;
    // the names for the model's state line; an empty / old list is refreshed in the background (ready for the next line)
    static List<string> FastOutfitNamesSnapshot()
    {
        List<string> snap; lock (fastOutfitNames) snap = fastOutfitNames.ToList();
        if (LoggedIn && DateTime.UtcNow - fastOutfitNamesAt > TimeSpan.FromMinutes(snap.Count == 0 ? 2 : 30))
        {
            fastOutfitNamesAt = DateTime.UtcNow;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(30000);
                    var names = (await ListOutfitFolders(cts.Token)).Select(f => f.Name).ToList();
                    if (names.Count > 0) lock (fastOutfitNames) { fastOutfitNames.Clear(); fastOutfitNames.AddRange(names); }
                }
                catch { }
            });
        }
        return snap;
    }

    static readonly HashSet<string> OutfitStopWords = new(StringComparer.Ordinal)
        { "your", "you", "the", "my", "a", "an", "outfit", "outfits", "one", "with", "and", "in", "into", "on", "of", "that", "those", "this", "clothes", "some", "pair", "put", "wear", "change", "to", "set", "please", "babe" };

    // pure: words of an outfit name / request, lower case; t-shirt / tee / t shirt -> tshirt; apostrophes dropped (ARTi'S -> artis)
    internal static List<string> OutfitWords(string s)
    {
        s = (s ?? "").ToLowerInvariant().Replace("'", "").Replace("\u2019", "");
        s = Regex.Replace(s, @"[^a-z0-9]+", " ");
        s = Regex.Replace(s, @"\bt shirt\b|\btee shirt\b|\btees?\b|\btshirts\b", "tshirt");
        s = Regex.Replace(s, @"\btube top\b", "tube top").Replace("tubetop", "tube top");
        return s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !OutfitStopWords.Contains(w)).ToList();
    }

    // pure: the model's outfit request -> (kind, outfit name) or (null, why). kind: "random" | "named" | "top_color".
    // named: an exact (case/punctuation-insensitive) name wins; else every request word must be in the name; several such
    // names (e.g. "shorts") -> a random one of them that she is not wearing (at most 4, more = too vague -> routine).
    internal static (string kind, string name, string why) ResolveFastOutfit(string req, IReadOnlyList<string> names, string current, IReadOnlyCollection<string> allow, Random rnd)
    {
        if (string.IsNullOrWhiteSpace(req)) return (null, null, "no outfit request");
        if (names == null || names.Count == 0) return (null, null, "My Outfits list unavailable");
        var r = req.Trim();
        var key = r.ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        if (key is "random" or "something_else" or "any" or "different")
        {
            var pool = DailyOutfitPool(DailyOutfitCandidates(names, allow?.ToList()), current);
            pool = pool.Where(p => current == null || !p.Equals(current.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            return pool.Count == 0 ? (null, null, "no other outfit to pick") : ("random", pool[rnd.Next(pool.Count)], null);
        }
        if (key is "top_color" or "top_colour" or "color" or "colour")
            return string.IsNullOrWhiteSpace(current) ? (null, null, "current outfit unknown") : ("top_color", current.Trim(), null);
        var want = OutfitWords(r);
        if (want.Count == 0) return (null, null, $"'{r}' names no outfit");
        var exact = names.Where(n => OutfitWords(n).SequenceEqual(want)).ToList();
        if (exact.Count == 1) return ("named", exact[0], null);
        var hits = names.Where(n => { var w = OutfitWords(n); return want.All(w.Contains); }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (hits.Count == 0) return (null, null, $"no saved outfit matches '{r}'");
        if (hits.Count > 4) return (null, null, $"'{r}' matches {hits.Count} outfits (too vague)");
        var notWorn = hits.Where(h => current == null || !h.Equals(current.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (notWorn.Count == 0) return (null, null, $"already wearing '{hits[0]}'");
        return ("named", notWorn[rnd.Next(notWorn.Count)], null);
    }

    // pure: anything that keeps the client from changing by itself (null = go ahead). The routine handles the blocked cases.
    internal static string FastOutfitBlock(bool loggedIn, bool seated, bool outfitBusy, bool redressPending, bool beachMode, string req)
    {
        if (!loggedIn) return "not logged in";
        if (seated) return "seated (furniture / toilet / tub rules)";
        if (redressPending) return "a tub / toilet re-dress is pending";
        if (outfitBusy) return "another outfit change is running";
        var k = (req ?? "").Trim().ToLowerInvariant().Replace(' ', '_');
        if (beachMode && !(k is "top_color" or "top_colour" or "color" or "colour") && !BikiniNameRx.IsMatch((req ?? "").Trim()))
            return "on the beach in the bikini (beach rules)";
        return null;
    }

    // ~1 s after his line: guards now, the change in the background. null = not done here (the routine does it).
    static Task<FastOutfitRun> FastOutfitStart(string req, string text, long msgId)
    {
        try
        {
            var block = FastOutfitBlock(LoggedIn, client.Self.SittingOn != 0, beachOutfitBusy,
                                        pendingDressOutfit != null || pendingToilet != null, beachMode, req);
            if (block != null) { fastOutfitLast = $"{DateTime.Now:HH:mm:ss} '{Short(text, 40)}' ({req}): left to the routine ({block})"; Log("fastoutfit", fastOutfitLast); return null; }
            beachOutfitBusy = true;   // the zone tick / wander bikini change wait for this one
            fastPending[msgId] = DateTime.UtcNow.AddSeconds(180);   // the webhook holds the line until the change is over
            var sw = Stopwatch.StartNew();
            Log("fastoutfit", $"'{Short(text, 60)}' -> outfit '{req}': starting");
            return Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(60000);
                    var folders = await ListOutfitFolders(cts.Token);
                    var names = folders.Select(f => f.Name).ToList();
                    lock (fastOutfitNames) { fastOutfitNames.Clear(); fastOutfitNames.AddRange(names); }
                    var (kind, name, why) = ResolveFastOutfit(req, names, lastNamedOutfit, DailyOutfitAllow(), Random.Shared);
                    if (kind == null) return new FastOutfitRun(false, null, null, why, sw.ElapsedMilliseconds);
                    string rep; bool ok;
                    if (kind == "top_color")
                    {
                        var f = await FindOutfitFolder(name, cts.Token);
                        if (f == null) return new FastOutfitRun(false, kind, name, "outfit folder not found", sw.ElapsedMilliseconds);
                        if (BikiniNameRx.IsMatch(name)) { rep = await BikiniHudRandomize(cts.Token); ok = rep.Contains("': attached"); }
                        else { rep = await OutfitClothingHuds(f, cts.Token, keepColor: false); ok = rep.Contains("': attached"); }
                    }
                    else if (BikiniNameRx.IsMatch(name)) { rep = await BikiniOn(); ok = rep.Contains("wearing outfit") || rep.Contains("worn"); }
                    else { rep = await WearOutfitWithHuds(name, KeepHudColorFor(name, lastNamedOutfit, true)); ok = rep.StartsWith("wearing outfit"); }
                    Log("fastoutfit", $"{kind} '{name}': {(ok ? "done" : "FAILED")} in {sw.ElapsedMilliseconds / 1000.0:0.0} s: " + rep.Replace("\n", " | ")[..Math.Min(400, rep.Length)]);
                    return new FastOutfitRun(ok, kind, name, ok ? null : "change failed: " + Short(rep.Replace("\n", " "), 120), sw.ElapsedMilliseconds);
                }
                catch (Exception ex) { return new FastOutfitRun(false, null, null, "error " + ex.GetBaseException().Message, sw.ElapsedMilliseconds); }
                finally { beachOutfitBusy = false; }
            });
        }
        catch (Exception ex) { Log("fastoutfit", "start error: " + ex.GetBaseException().Message); return null; }
    }

    // after the typing pause: send the ack (unclaimed), then finish in the background. null = not ours (normal ack path).
    static string FastOutfitBegin(FastCfg cfg, FastResult r, UUID from, string name, string text, long msgId, bool im, UUID imSession, Task<FastOutfitRun> run)
    {
        if (run == null) return null;
        bool ackSent = FastSend(from, name, r.Reply, msgId, im, imSession, claim: false);
        _ = Task.Run(async () =>
        {
            FastOutfitRun res;
            try { res = await run; } catch (Exception ex) { res = new FastOutfitRun(false, null, null, ex.GetType().Name, 0); }
            try
            {
                if (!res.Ok)
                {
                    // the routine takes over as before (it sees what I already said)
                    if (ackSent) fastAcks[msgId] = r.Reply;
                    fastOutfitLast = $"{DateTime.Now:HH:mm:ss} '{Short(text, 40)}' ({r.Outfit}): not done ({res.Why}); line handed to the routine";
                    Log("fastoutfit", fastOutfitLast);
                    return;
                }
                instantAnswered[msgId] = "(outfit changed: " + res.Name + ")";   // claimed even if the follow-up fails
                var sw = Stopwatch.StartNew();
                var (txt, pace) = await FastOutfitFollowUp(cfg, from, name, text, res, im);
                int wait = FastTypingDelayMs(cfg, txt, pace, sw.ElapsedMilliseconds, Random.Shared.NextDouble() * 2 - 1);
                if (wait > 0) { TypingStart(im); await Task.Delay(wait); }
                bool sent = FastSend(from, name, txt, msgId, im, imSession, claim: true);
                if (sent) instantAnswered[msgId] = txt;
                fastOutfitLast = $"{DateTime.Now:HH:mm:ss} '{Short(text, 40)}' -> {res.Kind} '{res.Name}' done in {res.Ms / 1000.0:0.0} s; follow-up '{txt}' {(sent ? "sent" : "NOT sent")} (claimed)";
                Log("fastoutfit", fastOutfitLast);
            }
            catch (Exception ex) { Log("fastoutfit", "finish error: " + ex.GetBaseException().Message); }
            finally { fastPending.TryRemove(msgId, out _); }
        });
        return $"ack{(ackSent ? "" : " (not sent)")} + outfit change by the client ({r.Outfit})";
    }

    // pure: the follow-up request (persona + state + this conversation's history + what just happened)
    internal static string BuildFastOutfitFollowUp(FastCfg cfg, string state, IReadOnlyList<FastLine> hist, string scope, string from, string text, FastOutfitRun res, bool im)
    {
        var mine = hist.Where(h => h.Scope == scope).ToList();
        if (mine.Count > cfg.HistoryLines) mine = mine.Skip(mine.Count - cfg.HistoryLines).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(im ? $"Private IM conversation with {from}. Earlier lines:" : "Nearby (public) chat. Earlier lines:");
        if (mine.Count == 0) sb.AppendLine("(none)");
        foreach (var h in mine) sb.AppendLine($"{(h.Mine ? "Galatea (you)" : h.Speaker)}: {h.Text}");
        var what = res.Kind == "top_color" ? $"you just changed the colour of your top (outfit '{res.Name}')" : $"you just finished changing into your outfit '{res.Name}'";
        sb.AppendLine().Append($"[{from} had asked: \"{text}\". You already acknowledged it; {what}. Write ONE short follow-up line telling him you're done, e.g. asking what he thinks. action=false, outfit=null.]");
        return new JsonObject
        {
            ["model"] = cfg.Model, ["max_tokens"] = cfg.MaxTokens, ["temperature"] = cfg.Temperature, ["stream"] = false,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = cfg.Persona },
                new JsonObject { ["role"] = "system", ["content"] = "Current state: " + (state ?? "unknown") },
                new JsonObject { ["role"] = "user", ["content"] = sb.ToString() },
            },
        }.ToJsonString();
    }

    static readonly string[] FastOutfitDoneLines = { "All changed~ what do you think?", "There, all done! Like it, babe?", "Done! How do I look?" };
    static async Task<(string text, string pace)> FastOutfitFollowUp(FastCfg cfg, UUID from, string name, string text, FastOutfitRun res, bool im)
    {
        try
        {
            var scope = im ? "im:" + from : "nearby";
            var body = BuildFastOutfitFollowUp(cfg, FastState(), FastHistSnapshot(), scope, name, text, res, im);
            var (r, ms) = await FastCall(cfg, body, FastKey());
            FastUsage(cfg, name, im, r, ms, "outfit follow-up");
            if (r.Error == null && !string.IsNullOrWhiteSpace(r.Reply) && !r.Action) return (r.Reply, r.Pace);
        }
        catch { }
        return (FastOutfitDoneLines[Random.Shared.Next(FastOutfitDoneLines.Length)], "quick");
    }
}
