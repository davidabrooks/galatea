// Leash.cs (2026-10-19, David): the N° 9 collar's leash and its follow mode.
// What the docs say (README_Collar "THE LEASH", NX Remote README "LEASH TOGGLE", Peanut leash manual, the predecessor):
//   - leash = a particle chain from the collar to the holder (or to an anchor object); Grab/Yank/Unleash/Pass/Anchor;
//     Shorter/Longer set how far she may move from the holder (Peanut: max 20 m); the COLLAR pulls her itself.
//   - Strict: she can't unleash herself or teleport (Peanut: or fly) away. Anchored leashes are always strict.
//   - Follow: no chain, "cannot be strict". The NX Remote's Leash button toggles leash/unleash, and on items without a leash
//     it toggles follow/unfollow. Peanut's text reads "the wearer follows", but David (19:19) says on his button HE follows
//     HER: so in follow mode she does nothing special (no hold, no walking to him; she wanders and walks as usual at
//     walking pace so he can keep up). It is only detected, reported (rlv_follow_start/stop), logged and shown.
//   - The notecards do not name the RLV commands, the pull method or the length in metres: those are logged live here.
// Signals used: (1) a worn attachment's particle system aimed at an avatar/object = leashed to it (reliable for the
// chain; an invisible Magic Leash may still use a transparent target particle); (2) the collar's own owner-say text about
// leash/follow (wording unverified: every such line is logged to rlv-log.md for the live test); While LEASHED
// (never in follow mode): soft hold (no wander, autofollow snoozed), she gently walks toward the
// holder only when he is past a comfortable distance (a nicety) (follow engine; the collar's own pull is never fought). Strict blocks her own
// teleports, flying, lures from anyone else and 'leash release'. MAYDAY / rlv clear always end it locally.
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal sealed record LeashState(string mode, UUID holder, string holderName, bool anchor, float length, string how, DateTime since);
    internal sealed record LeashSaved(string mode, string holder, string holderName, bool anchor, float length, string how, string region);
    static volatile LeashState leash;   // null = free
    internal const float ShoelaceM = 1f;   // the collar's shortest preset 'Shoelace' (README; David calls it shoestring). Metres undocumented: assumed ~1 m
    static float leashLength = ShoelaceM; // read from the collar's text when it says it, else Shoelace (David always uses the shortest)
    static int leashMissScans;
    static string leashLastNote;
    static int leashWalkTick;
    internal static volatile bool leashWanderOff = true;   // 'leash wanderoff on|off' (persisted, default ON): now and then she "forgets"
    static DateTime leashNextWanderOff = DateTime.MaxValue;
    static readonly string[] LeashOopsLines = { "oops… forgot 😳", "oh! right… the leash 😳", "hehe… forgot I'm on a leash 🙈", "oops 😅" };
    // pure (tested): next "forgot I'm leashed" moment, 10-20 min after `from`
    internal static DateTime LeashNextForget(DateTime from, double r01) => from.AddMinutes(10 + 10 * Math.Clamp(r01, 0, 1));
    // pure (tested): may she try now? never seated, placed/held, or under @unsit; never anchored-less-than-known holder
    internal static bool LeashMayForget(bool on, bool seated, bool held, bool unsit, bool holderKnown, DateTime now, DateTime due) =>
        on && !seated && !held && !unsit && holderKnown && now >= due;
    // pure (tested): where to stroll: a couple of metres past the leash from the holder, toward `toward` (something interesting) or straight on
    internal static Vector3 LeashForgetTarget(Vector3 holder, Vector3 me, Vector3? toward, float length)
    {
        var d = (toward ?? me) - holder; var dir = new Vector3(d.X, d.Y, 0);
        if (dir.LengthSquared() < 0.01f) dir = new Vector3(1, 0, 0);
        dir = Vector3.Normalize(dir);
        var t = holder + dir * (length + 1.5f); return new Vector3(t.X, t.Y, me.Z);
    }
    // pure (tested): "length 4 m" / "Length: 2.5m" in the collar's text
    internal static float? LeashLengthFromText(string msg)
    {
        var m = Regex.Match(msg ?? "", @"length\D{0,20}?(?<v>\d+(\.\d+)?)\s*(m\b|meters?|metres?)", RegexOptions.IgnoreCase);
        if (!m.Success && Regex.IsMatch(msg ?? "", @"\bshoe(lace|string)\b", RegexOptions.IgnoreCase)) return ShoelaceM;
        return m.Success && float.TryParse(m.Groups["v"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 && v <= 20 ? v : null;
    }
    static int leashForgetBusy;
    static void LeashMaybeForget(LeashState l, Vector3 holderPos)
    {
        bool held = rlvHoldSeat != null;
        if (leashNextWanderOff == DateTime.MaxValue) leashNextWanderOff = LeashNextForget(DateTime.Now, Random.Shared.NextDouble());
        if (!LeashMayForget(leashWanderOff, client.Self.SittingOn != 0, held, rlv.Has("unsit"), true, DateTime.Now, leashNextWanderOff)) return;
        if (Interlocked.Exchange(ref leashForgetBusy, 1) == 1) return;
        leashNextWanderOff = LeashNextForget(DateTime.Now, Random.Shared.NextDouble());
        _ = Task.Run(async () =>
        {
            try
            {
                var me = client.Self.SimPosition;
                // something nearby and interesting: a named, unattached object 3-10 m from me
                var sim = client.Network.CurrentSim;
                var near = sim.ObjectsPrimitives.Values.Where(p => p != null && p.ParentID == 0 && !string.IsNullOrEmpty(p.Properties?.Name) && p.Properties.Name != "Object"
                                                                 && Vector3.Distance(p.Position, me) is > 3f and < 10f).ToList();
                var pick = near.Count > 0 ? near[Random.Shared.Next(near.Count)] : null;
                var tgt = LeashForgetTarget(holderPos, me, pick?.Position, leashLength);
                RlvLog($"leash: forgot I'm leashed: strolling toward {(pick != null ? $"'{pick.Properties.Name}'" : "nothing in particular")} ({Fmt(tgt)}, {leashLength + 1.5:F1} m from {l.holderName}); the collar will tug me back");
                if (followId == l.holder) { followId = UUID.Zero; followDistThis = null; }
                AutoPilotTo(tgt);
                var start = client.Self.SimPosition;
                await Task.Delay(4000);
                try { client.Self.AutoPilotCancel(); } catch { }   // stop trying: never fight the pull
                RlvLog($"leash: stopped trying after {Vector3.Distance(start, client.Self.SimPosition):F1} m (tugged back)");
                if (!rlv.Has("sendchat") && QuietChatGuard() == null)
                {
                    var line = LeashOopsLines[Random.Shared.Next(LeashOopsLines.Length)];
                    var r = await Exec("say " + line); RlvLog($"leash: said '{line}' ({r})");
                }
            }
            catch (Exception ex) { RlvLog("leash: wander-off error " + ex.GetBaseException().Message); }
            finally { Interlocked.Exchange(ref leashForgetBusy, 0); }
        });
    }
    internal static volatile bool leashWalk;   // 'leash walk on|off' (persisted, default OFF): the collar's pull leads her, she just chats
    // pure (tested): walk toward him only when he is past a comfortable distance (under the leash length)
    internal static bool LeashShouldWalk(float dist, float length) => dist > LeashWalkDist(length) + 1f;

    // pure (tested): how close she walks to the holder: inside the leash, never so close she bumps him
    internal static float LeashWalkDist(float length) => Math.Clamp(length * 0.7f, 1f, 8f);
    // pure (tested): strict = an anchor, or the collar's movement restrictions are on while leashed (follow can't be strict)
    internal static bool LeashIsStrict(string mode, bool anchor, Func<string, bool> has) =>
        mode == "leash" && (anchor || new[] { "tploc", "tplm", "tplure", "fly", "sittp", "tplocal" }.Any(has));
    // pure (tested): her own commands refused under a strict leash (null = allowed). MAYDAY is chat and never refused.
    internal static string LeashBlocks(string cmd, string rest, bool strict, string holderName, string lureFrom) => !strict ? null : cmd switch
    {
        "teleport" or "tp" or "home" => "refused (strict leash: I can't teleport away; say MAYDAY to end it)",
        "fly" => "refused (strict leash: no flying away)",
        "accept" when lureFrom != null && !string.Equals(lureFrom, holderName, StringComparison.OrdinalIgnoreCase) => $"refused (strict leash: only {holderName} can take me somewhere)",
        "leash" when rest.Trim().Equals("release", StringComparison.OrdinalIgnoreCase) => "refused (strict leash: I can't unleash myself; say MAYDAY)",
        _ => null
    };
    // pure (tested): the collar's own text about leash/follow. (kind, name) kind = leash|unleash|follow|unfollow; null = not about it
    internal static (string kind, string name)? LeashFromText(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg) || msg.StartsWith("@")) return null;
        var m = msg.Trim();
        Match x;
        if (Regex.IsMatch(m, @"\b(unleash(ed)?|released (your|the) leash|leash (was |has been )?(removed|released|dropped)|let go of (your|the) leash)\b", RegexOptions.IgnoreCase)) return ("unleash", null);
        if (Regex.IsMatch(m, @"\b(unfollow(ed)?|stop(ped)? following|no longer following|not following)\b", RegexOptions.IgnoreCase)) return ("unfollow", null);
        if ((x = Regex.Match(m, @"^(?<n>.+?) (grabbed|grabs|has grabbed|took|takes|is holding|holds|passed you|yanked) (your|the) leash", RegexOptions.IgnoreCase)).Success) return ("leash", x.Groups["n"].Value.Trim());
        if ((x = Regex.Match(m, @"leash(ed)? (to|by) (?<n>[^.!,]+)", RegexOptions.IgnoreCase)).Success) return ("leash", x.Groups["n"].Value.Trim());
        if ((x = Regex.Match(m, @"^(?<n>.+?) (is )?(now )?(following|follows) you", RegexOptions.IgnoreCase)).Success) return ("follow", x.Groups["n"].Value.Trim());
        if ((x = Regex.Match(m, @"\b(now )?follow(ing|s)? (?<n>[^.!,]+)", RegexOptions.IgnoreCase)).Success && !Regex.IsMatch(m, @"\bcan (now )?follow\b", RegexOptions.IgnoreCase)) return ("follow", x.Groups["n"].Value.Trim());
        return null;
    }

    static bool LeashStrictNow() { var l = leash; return l != null && LeashIsStrict(l.mode, l.anchor, b => rlv.Has(b)); }
    static string LeashGuard(string cmd, string rest) { var l = leash; return l == null ? null : LeashBlocks(cmd, rest, LeashStrictNow(), l.holderName, pendingLure?.name); }

    static void LeashStart(string mode, UUID holder, string name, bool anchor, string how)
    {
        var cur = leash;
        if (cur != null && cur.mode == mode && cur.holder == holder) return;
        if (cur != null) LeashEnd($"replaced by {mode} to {name}", quiet: true);
        leash = new LeashState(mode, holder, name, anchor, leashLength, how, DateTime.Now); leashMissScans = 0; leashNextWanderOff = DateTime.MaxValue;
        if (mode == "leash") { try { SetPauseNow("rlv", $"leash held by {name}"); } catch { } afSnoozeUntil = DateTime.MaxValue; }
        bool strict = LeashStrictNow();
        var text = mode == "leash"
            ? $"leashed {(anchor ? "(anchored) to" : "by")} {name} [{how}]; strict {(strict ? "ON (no self-unleash/teleport)" : "off")}; I walk after them past {LeashWalkStart(leashLength):F1} m, stop at {LeashWalkStop(leashLength):F1} m"
            : $"{name} is following me on the collar's follow (no chain) [{how}]; I carry on as usual at walking pace";
        RlvLog("leash: " + text);
        try { Notify(mode == "leash" ? "rlv_leashed" : "rlv_follow_start", name, holder, text, null); } catch { }
        RlvSaveState();
    }
    static void LeashEnd(string why, bool quiet = false)
    {
        var l = leash; if (l == null) return;
        leash = null; leashNextWanderOff = DateTime.MaxValue;
        if (l.mode == "leash" && followId == l.holder) { followId = UUID.Zero; followDistThis = null; try { client.Self.AutoPilotCancel(); } catch { } }
        if (l.mode == "leash" && rlvHoldSeat == null) { afSnoozeUntil = DateTime.Now; try { if (wanderPause == "rlv") wanderPause = null; } catch { } }
        var text = $"{(l.mode == "leash" ? "unleashed" : "follow stopped")} ({why}); was {l.mode} to {l.holderName} for {(DateTime.Now - l.since):hh\\:mm\\:ss}";
        RlvLog("leash: " + text);
        if (!quiet) try { Notify(l.mode == "leash" ? "rlv_unleashed" : "rlv_follow_stop", l.holderName, l.holder, text, null); } catch { }
        RlvSaveState();
    }

    // owner-say text from her own collar (non-@)
    static void LeashHook()
    {
        client.Self.ChatFromSimulator += (s, e) =>
        {
            try
            {
                if (e.Type != ChatType.OwnerSay || string.IsNullOrEmpty(e.Message) || e.Message[0] == '@' || e.OwnerID != client.Self.AgentID || e.SourceType != ChatSourceType.Object) return;
                if (!Regex.IsMatch(e.Message, "leash|follow|strict|length|anchor|yank|beckon", RegexOptions.IgnoreCase)) return;
                RlvLog($"leash: collar said ('{e.FromName}'): {e.Message}");
                if (LeashLengthFromText(e.Message) is float len && len != leashLength) { leashLength = len; RlvLog($"leash: length {len:F1} m read from the collar"); RlvSaveState(); }
                var t = LeashFromText(e.Message); if (t == null) return;
                switch (t.Value.kind)
                {
                    case "unleash": if (leash?.mode == "leash") LeashEnd("collar said so"); break;
                    case "unfollow": if (leash?.mode == "follow") LeashEnd("collar said so"); break;
                    default:
                    {
                        var av = Avatars().FirstOrDefault(a => a.av.Name.StartsWith(t.Value.name, StringComparison.OrdinalIgnoreCase) || (a.av.Name.Replace(" Resident", "")).Equals(t.Value.name, StringComparison.OrdinalIgnoreCase));
                        if (t.Value.kind == "leash" && leash?.mode == "leash") break;   // the particle scan already has it
                        LeashStart(t.Value.kind, av.av?.ID ?? UUID.Zero, av.av?.Name ?? t.Value.name, false, "collar text");
                        break;
                    }
                }
            }
            catch { }
        };
    }

    // the particle chain: any of her worn prims aiming particles at someone/something else
    static (UUID target, Primitive emitter)? LeashParticleTarget()
    {
        var sim = client?.Network?.CurrentSim; if (sim == null) return null;
        var me = client.Self.AgentID;
        foreach (var p in sim.ObjectsPrimitives.Values)
        {
            if (p == null) continue;
            var ps = p.ParticleSys;
            if (ps.CRC == 0 && ps.Target == UUID.Zero) continue;
            if (ps.Target == UUID.Zero || ps.Target == me || ps.Target == p.ID) continue;
            var root = RlvOwnAttachmentRoot(p.ID); if (root == null) continue;
            var rn = root.Properties?.Name; if (rn != null && rn.IndexOf("collar", StringComparison.OrdinalIgnoreCase) < 0 && rn.IndexOf("No 9", StringComparison.OrdinalIgnoreCase) < 0) continue;   // only the collar's chain
            return (ps.Target, p);
        }
        return null;
    }

    // 2026-10-21 live test: the click produced no collar text and no own-chain hit. The chain may come from HIS side (his
    // NX Remote / HUD / attachment) aimed at me or at one of my attachments. Holder = the avatar that emitter is worn by.
    static (UUID target, Primitive emitter)? LeashForeignTarget()
    {
        var sim = client?.Network?.CurrentSim; if (sim == null) return null;
        var me = client.Self.AgentID; var myLocal = client.Self.LocalID;
        var mine = WornPrims().Select(p => p.ID).ToHashSet();
        foreach (var p in sim.ObjectsPrimitives.Values)
        {
            if (p == null || p.ParticleSys.Target == UUID.Zero) continue;
            var t = p.ParticleSys.Target;
            if (t != me && !mine.Contains(t) && !(sim.ObjectsPrimitives.Values.FirstOrDefault(x => x?.ID == t) is { } tp && RlvOwnAttachmentRoot(tp.ID) != null)) continue;
            if (RlvOwnAttachmentRoot(p.ID) != null) continue;   // mine: handled by the own-chain scan
            // walk up to the avatar wearing it
            Primitive q = p; uint parent = p.ParentID;
            for (int k = 0; k < 4 && parent != 0; k++)
            {
                if (sim.ObjectsAvatars.Values.FirstOrDefault(a => a?.LocalID == parent) is { } av) return (av.ID, p);
                q = sim.ObjectsPrimitives.Values.FirstOrDefault(x => x?.LocalID == parent); parent = q?.ParentID ?? 0;
            }
            return (p.OwnerID != UUID.Zero ? p.OwnerID : p.ID, p);   // rezzed object (post) or unknown wearer
        }
        return null;
    }
    static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, uint> leashDiagSeen = new();
    // diagnostics: every particle system that involves me (emitted by my attachments, or aimed at me / my attachments)
    static void LeashDiagHook()
    {
        client.Objects.ObjectUpdate += (s, e) =>
        {
            try
            {
                var p = e.Prim; if (p == null) return;
                var ps = p.ParticleSys; var me = client.Self.AgentID;
                bool mineEmit = p.ParentID == client.Self.LocalID || RlvOwnAttachmentRoot(p.ID) != null;
                bool atMe = ps.Target != UUID.Zero && (ps.Target == me || RlvOwnAttachmentRoot(ps.Target) != null);
                if (!mineEmit && !atMe) return;
                var key = ps.CRC ^ (uint)ps.Target.GetHashCode();
                if (leashDiagSeen.TryGetValue(p.LocalID, out var old) && old == key) return;
                leashDiagSeen[p.LocalID] = key;
                if (ps.CRC == 0 && ps.Target == UUID.Zero && old == 0) return;
                if (mineEmit && ps.Target == UUID.Zero && old != 0) RlvLog($"leash-diag: particles CLEARED on MY prim {p.ID} (unleashed, or invisible leash)");
                RlvLog($"leash-diag: particles on {(mineEmit ? "MY" : "other")} prim {p.ID} (local {p.LocalID}, parent {p.ParentID}, owner {p.OwnerID}): crc {ps.CRC} target {ps.Target} flags {ps.PartDataFlags} pattern {ps.Pattern} texture {ps.Texture}");
            }
            catch { }
        };
    }

    static void LeashLoopRun() => _ = Task.Run(async () =>
    {
        while (true)
        {
            await Task.Delay(2000);
            try { if (LoggedIn) LeashTick(); } catch { }
        }
    });
    // pure (tested): chain target -> holder: an object worn by an avatar (its root's parent is the avatar) resolves to that avatar
    internal static UUID LeashResolveHolder(UUID target, Func<UUID, (uint local, uint parent)?> prim, Func<uint, UUID?> avatarByLocal, Func<uint, uint> parentOf)
    {
        if (prim(target) is not { } p) return target;
        uint parent = p.parent;
        for (int i = 0; i < 4 && parent != 0; i++)
        {
            if (avatarByLocal(parent) is UUID av) return av;
            parent = parentOf(parent);
        }
        return target;
    }
    static void LeashTick()
    {
        var sim = client.Network.CurrentSim;
        var pt = LeashParticleTarget() ?? LeashForeignTarget();
        var l = leash;
        if (pt is { } t)
        {
            leashMissScans = 0;
            // the chain often targets the leash HANDLE he wears (an attachment), not his avatar: resolve it to the wearer
            var tid = LeashResolveHolder(t.target, id => sim.ObjectsPrimitives.Values.FirstOrDefault(x => x?.ID == id) is { } pr ? (pr.LocalID, pr.ParentID) : null,
                lid => sim.ObjectsAvatars.TryGetValue(lid, out var a) ? a.ID : (lid != 0 && sim.ObjectsPrimitives.TryGetValue(lid, out var pp) ? null : (UUID?)null),
                lid => sim.ObjectsPrimitives.TryGetValue(lid, out var pp) ? pp.ParentID : 0u);
            if (tid != t.target) t = (tid, t.emitter);
            if (l == null || l.mode != "leash" || l.holder != t.target)
            {
                var av = Avatars().FirstOrDefault(a => a.av.ID == t.target);
                string name; bool anchor = av.av == null;
                if (!anchor) name = av.av.Name;
                else name = sim.ObjectsPrimitives.Values.FirstOrDefault(x => x?.ID == t.target)?.Properties?.Name ?? $"object {t.target}";
                LeashStart("leash", t.target, name, anchor, "particle chain");
                l = leash;
            }
            else if (l.how == "relog") leash = l = l with { how = "particle chain" };
        }
        else if (l?.mode == "leash" && l.how == "particle chain" && ++leashMissScans >= 3 && LeashChainLossEnds(DateTime.Now, lastPullAt)) { LeashEnd("the chain is gone"); return; }
        l = leash; if (l == null) return;
        if (l.mode == "follow") { try { if (client.Self.Movement.AlwaysRun && !leashDriving) client.Self.Movement.AlwaysRun = false; } catch { } return; }   // he follows her: walking pace only
        // keep the soft hold and walk after the holder when out of range
        if (wanderPause != "rlv") try { SetPauseNow("rlv", $"{l.mode} held by {l.holderName}"); } catch { }
        afSnoozeUntil = DateTime.MaxValue;
        if (l.anchor || l.holder == UUID.Zero) return;   // anchored: the collar holds her at the post
        var h = Avatars().FirstOrDefault(a => a.av.ID == l.holder);
        if (h.av != null) LeashMaybeForget(l, h.pos);
        string note;
        if (h.av == null) note = $"{l.holderName} is not in this region (teleported away?): staying put{(LeashStrictNow() ? " (strict: no teleport except his own offer/force-TP)" : "")}";
        else if (h.av.ParentID != 0) note = $"{l.holderName} is sitting: I stay near";
        else note = null;
        if (note != leashLastNote) { leashLastNote = note; if (note != null) RlvLog("leash: " + note); }
        // keeping up on my own legs: LeashWalk.cs (4 Hz walk/run toward him past 0.8x the leash, walk anim during the pull)
    }

    static string LeashCmd(string rest)
    {
        var a = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
        switch (sub)
        {
            case "status":
            {
                var l = leash;
                if (l == null) return $"not leashed, not following (leash walk {(leashWalk ? "on" : "off")}; walk distance when leashed: {LeashWalkDist(leashLength):F1} m)";
                var h = l.holder == UUID.Zero ? default : Avatars().FirstOrDefault(x => x.av.ID == l.holder);
                if (l.mode == "follow") return $"{l.holderName} is following me (collar follow, no chain) since {l.since:HH:mm:ss} via {l.how}; no hold, I move as usual at walking pace; {(h.av != null ? $"{h.dist:F1} m away" : "not in view")}";
                return $"{l.mode} {(l.anchor ? "anchored to" : "held by")} {l.holderName} ({l.holder}) since {l.since:HH:mm:ss} via {l.how}; " +
                       $"strict {(LeashStrictNow() ? "ON" : "off")}; {(h.av != null ? $"{h.dist:F1} m away" : "holder not in view")}; leash walk {(leashWalk ? $"on ({LeashWalkDist(leashLength):F1} m)" : "off (the collar's pull leads me)")}; length {leashLength:F1} m; wanderoff {(leashWanderOff ? $"on (next ~{leashNextWanderOff:HH:mm})" : "off")}; " +
                       $"soft hold on (wander paused, autofollow snoozed); MAYDAY ends it";
            }
            case "length":
                if (a.Length == 2 && F(a[1], out var m) && m > 0 && m <= 20) { leashLength = m; RlvLog($"leash: length set to {m:F1} m (local)"); RlvSaveState(); return $"leash length {m:F1} m: I keep within {LeashWalkDist(m):F1} m"; }
                return "usage: leash length <metres 1-20>";
            case "walk":
                if (a.Length == 2 && a[1].ToLowerInvariant() is "on" or "off") { leashWalk = a[1].ToLowerInvariant() == "on"; RlvLog($"leash: walk-after-holder {(leashWalk ? "ON" : "OFF")}"); RlvSaveState(); if (!leashWalk && leash is { mode: "leash" } lw && followId == lw.holder) { followId = UUID.Zero; followDistThis = null; try { client.Self.AutoPilotCancel(); } catch { } } }
                return $"leash walk {(leashWalk ? "on: when leashed I walk toward the holder if he is more than ~" + (LeashWalkDist(leashLength) + 1).ToString("F1") + " m away" : "off: when leashed the collar's pull leads me (default)")}";
            case "wanderoff":
                if (a.Length == 2 && a[1].ToLowerInvariant() is "on" or "off") { leashWanderOff = a[1].ToLowerInvariant() == "on"; RlvLog($"leash: forget-and-wander-off {(leashWanderOff ? "ON" : "OFF")}"); RlvSaveState(); }
                return $"leash wanderoff {(leashWanderOff ? $"on: every 10-20 min leashed I forget and stroll ~1.5 m past the leash, then say oops{(leash != null && leashNextWanderOff != DateTime.MaxValue ? $" (next ~{leashNextWanderOff:HH:mm})" : "")}" : "off")}";
            case "release":
                if (leash == null) return "not leashed";
                LeashEnd("released by my own command (the collar may still hold the chain)"); return "released locally";
            default: return "usage: leash status | leash walk on|off | leash wanderoff on|off | leash length <m> | leash release";
        }
    }

    static LeashSaved LeashExport() { var l = leash; return l == null ? null : new LeashSaved(l.mode, l.holder.ToString(), l.holderName, l.anchor, leashLength, l.how, client?.Network?.CurrentSim?.Name); }
    static void LeashRestore(LeashSaved s)
    {
        if (s == null) return;
        leashLength = s.length > 0 ? s.length : leashLength;
        UUID.TryParse(s.holder, out var h);
        RlvLog($"relog: restoring {s.mode} to {s.holderName} (the collar re-casts the chain itself; the scan takes over when it does)");
        leash = new LeashState(s.mode, h, s.holderName, s.anchor, leashLength, s.how == "particle chain" ? "relog" : s.how, DateTime.Now);
        leashMissScans = 0;
        if (s.mode == "leash") { try { SetPauseNow("rlv", $"relog: leash to {s.holderName}"); } catch { } afSnoozeUntil = DateTime.MaxValue; }
        _ = Task.Run(async () =>
        {   // the NX Remote README: after a relog "you may need to press the button twice". If no chain comes back in 2 min, end it.
            await Task.Delay(120000);
            var l = leash;
            if (l != null && l.how == "relog" && s.mode == "leash" && LeashParticleTarget() == null) LeashEnd("no chain came back within 2 min of the relog");
        });
    }
}
