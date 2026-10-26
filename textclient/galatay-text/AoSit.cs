// AoSit.cs (2026-10-26 09:03, David "stand up babe"): the AO attachment is the authority on sit versus stand, not the sim's state and
// not our own flag. The AO plays its own ground-sit animation (VAMOSITG*, from @HUDCenter) while the sim already reports STAND, so
// "AO force sit already off" was believed (only the built-in SIT_GROUND anims were checked) and she stayed sat down.
// The AO's sit animations are learned (their ids, persisted in run/ao-anim-classes.json): at every pose entry/rotation the AO-added
// animations that are not known AO stand animations are sit animations; an AO animation that starts during normal standing (no pose,
// no transition window) is a stand animation and is never a sit one. "AO force sit on" = a built-in ground sit or any learned AO sit
// animation is playing while not on an object. Stand = verify against that, toggle the AO off only while it is really on, bounded retries.
using System.Collections.Concurrent;
using System.Text.Json;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static readonly string AoClassFile = Env("GT_AO_CLASS_FILE", "/home/box/viewers/textclient/run/ao-anim-classes.json");
    static readonly object aoClassLock = new();
    static readonly HashSet<UUID> aoSitIds = new(), aoStandIds = new();
    static bool aoClassLoaded;
    // 2026-10-26 11:28: after `idle_pose stop` from a ground sit the AO kept playing 2941ee1d (a kneel / sit-back-on-heels) while the status said
    // "standing (no AO sit playing)": the id was in neither list. It is a SIT. Seeded here so it is a sit from the first login, whatever the
    // learned file says; it can never be learned as a stand.
    internal static readonly UUID[] AoSitSeed = { UUID.Parse("2941ee1d-94c1-a56f-0d90-fe0e2e0918dc") };
    // while a pose enters / leaves, the AO's animations are transitional: nothing is learned as "stand" from them
    static DateTime aoSitTransitionUntil = DateTime.MinValue;
    
    static void AoClassEnsureLoaded()
    {
        lock (aoClassLock)
        {
            if (aoClassLoaded) return; aoClassLoaded = true;
            foreach (var u in AoSitSeed) aoSitIds.Add(u);
            try
            {
                if (!File.Exists(AoClassFile)) return;
                var d = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(AoClassFile)) ?? new();
                foreach (var s in d.GetValueOrDefault("sit") ?? new()) if (UUID.TryParse(s, out var u)) aoSitIds.Add(u);
                foreach (var s in d.GetValueOrDefault("stand") ?? new()) if (UUID.TryParse(s, out var u)) aoStandIds.Add(u);
                foreach (var u in aoStandIds) aoSitIds.Remove(u);
                foreach (var u in AoSitSeed) { aoStandIds.Remove(u); aoSitIds.Add(u); }
            }
            catch { }
        }
    }
    static void AoClassSave()
    {
        try
        {
            Dictionary<string, List<string>> d;
            lock (aoClassLock) d = new() { ["sit"] = aoSitIds.Select(x => x.ToString()).OrderBy(x => x).ToList(), ["stand"] = aoStandIds.Select(x => x.ToString()).OrderBy(x => x).ToList() };
            Directory.CreateDirectory(Path.GetDirectoryName(AoClassFile)!);
            File.WriteAllText(AoClassFile, JsonSerializer.Serialize(d));
        }
        catch { }
    }
    static HashSet<UUID> AoSitIdsNow() { AoClassEnsureLoaded(); lock (aoClassLock) return new(aoSitIds); }

    // pure (tested): the AO-added animations of a pose entry/rotation that are sit animations: not already playing before, not a known stand animation
    internal static List<UUID> AoSitLearnable(IEnumerable<string> added, IEnumerable<UUID> before, IEnumerable<UUID> standIds)
    {
        var b = new HashSet<UUID>(before ?? Enumerable.Empty<UUID>()); var st = new HashSet<UUID>(standIds ?? Enumerable.Empty<UUID>());
        return (added ?? Enumerable.Empty<string>()).Select(s => UUID.TryParse(s, out var u) ? u : UUID.Zero)
            .Where(u => u != UUID.Zero && !b.Contains(u) && !st.Contains(u) && !BuiltinAnims.ContainsKey(u)).Distinct().ToList();
    }
    // pure (tested): may an AO animation that just started be recorded as a STAND animation? only in plain standing: no pose, no
    // ground sit playing, outside a pose transition window, not on an object
    internal static bool AoStandLearnOk(bool posed, bool builtinGroundSitPlaying, bool sittingOnObject, DateTime now, DateTime transitionUntil)
        => !posed && !builtinGroundSitPlaying && !sittingOnObject && now >= transitionUntil;
    // pure (tested): is an AO sit playing? built-in ground sit, or any known AO sit animation (learned + the ones of the current pose)
    internal static bool AoSitPlaying(IEnumerable<UUID> playing, bool sittingOnObject, IEnumerable<UUID> sitIds)
    {
        if (sittingOnObject) return false;
        var ids = new HashSet<UUID>(sitIds ?? Enumerable.Empty<UUID>()); ids.Add(Animations.SIT_GROUND); ids.Add(Animations.SIT_GROUND_staticRAINED);
        return (playing ?? Enumerable.Empty<UUID>()).Any(ids.Contains);
    }
    // pure (tested): AO-sourced animations that are in NEITHER list. Never treated as stands: status says "unclassified, may be a sit", the stand
    // verifier acts on them, the hover check does not run (2026-10-26 2941ee1d)
    internal static List<UUID> AoUnclassified(IEnumerable<UUID> playing, Func<UUID, bool> fromAo, IEnumerable<UUID> standIds, IEnumerable<UUID> sitIds)
    {
        var st = new HashSet<UUID>(standIds ?? Enumerable.Empty<UUID>()); var si = new HashSet<UUID>(sitIds ?? Enumerable.Empty<UUID>());
        return (playing ?? Enumerable.Empty<UUID>()).Where(u => !BuiltinAnims.ContainsKey(u) && fromAo(u) && !st.Contains(u) && !si.Contains(u)).Distinct().ToList();
    }
    // pure (tested): the ONE AO force-sit toggle for an unclassified animation that survived the stands: only when she is believed to have been
    // ground sitting (the toggle turns force sit ON when it is off) and never a second time
    internal static bool AoUnknownToggleOk(bool unclassifiedRemains, bool believedSitting, int togglesDone) => unclassifiedRemains && believedSitting && togglesDone == 0;
    // pure (tested): believed ground sitting = a ground-sit pose of ours, or an AO sit seen playing in the last 10 minutes
    internal static bool AoBelievedSitting(string poseKind, DateTime sitSeenAt, DateTime now) => poseKind == "groundsit" || now - sitSeenAt < TimeSpan.FromMinutes(10);
    // pure (tested): after the one toggle, the unclassified animations that stopped while a CLASSIFIED stand now plays were the sit: learn them
    internal static List<UUID> AoLearnAfterToggle(IEnumerable<UUID> unclassifiedBefore, IEnumerable<UUID> playingAfter, IEnumerable<UUID> standIds)
    {
        var after = new HashSet<UUID>(playingAfter ?? Enumerable.Empty<UUID>()); var st = new HashSet<UUID>(standIds ?? Enumerable.Empty<UUID>());
        if (!after.Any(st.Contains)) return new();
        return (unclassifiedBefore ?? Enumerable.Empty<UUID>()).Where(u => !after.Contains(u)).Distinct().ToList();
    }
    internal enum AoStandStep { Done, Stand, Toggle, GiveUp }
    // pure (tested): the next move of the verified stand, from how many moves are already made (stands + toggles). Pattern: stand,
    // stand, toggle, stand, toggle, stand, then give up. The AO toggle is touched only while an AO sit really plays AND we believe
    // she is ground sitting (touching it while off turns force sit ON), never twice in a row, at most AoStandMaxToggles times.
    internal static AoStandStep AoStandNext(bool aoSitPlaying, int moves, bool mayToggle)
    {
        if (!aoSitPlaying) return AoStandStep.Done;
        if (moves >= 6) return AoStandStep.GiveUp;
        bool toggleTurn = moves is 2 or 4;
        return toggleTurn && mayToggle ? AoStandStep.Toggle : AoStandStep.Stand;
    }
    // pure (tested): the real sit/stand state for `idlepose status`; the sim's own opinion is shown beside it
    internal static string AoRealState(bool aoSitPlaying, IReadOnlyCollection<string> playingSitIds, bool simSittingOnObject, bool simSaysGround, IReadOnlyCollection<string> unclassifiedIds = null)
    {
        bool unk = !simSittingOnObject && !aoSitPlaying && unclassifiedIds is { Count: > 0 };
        string real = simSittingOnObject ? "sitting on an object" : aoSitPlaying ? "AO sit playing (" + (playingSitIds.Count == 0 ? "built-in ground sit" : string.Join(",", playingSitIds)) + ")"
            : unk ? "UNKNOWN: AO animation unclassified (" + string.Join(",", unclassifiedIds) + "), may be a sit; not treated as standing" : "standing (no AO sit playing, only classified AO stands)";
        string sim = simSittingOnObject ? "on an object" : simSaysGround ? "ground sit" : "stand";
        bool mismatch = !simSittingOnObject && aoSitPlaying != simSaysGround;
        return $"real state: {real}; sim says {sim}{(mismatch ? " (MISMATCH: the AO is the authority)" : "")}";
    }

    // learned sit animations only (the raw animations of a pose include AO stand animations that restart during it)
    static DateTime aoSitSeenAt = DateTime.MinValue;
    static bool AoSitReallyOn() { var r = AoSitPlaying(SelfAnims(), client.Self.SittingOn != 0, AoSitIdsNow()); if (r) aoSitSeenAt = DateTime.Now; return r; }
    // AO animations playing now that are in neither list (ownAnims holds each animation's source object)
    static List<UUID> AoUnclassifiedNow(IEnumerable<UUID> playing = null)
    {
        AoClassEnsureLoaded(); HashSet<UUID> st, si; lock (aoClassLock) { st = new(aoStandIds); si = new(aoSitIds); }
        Dictionary<UUID, (int seq, UUID src)> cur; lock (animLock) cur = new(ownAnims);
        return AoUnclassified(playing ?? SelfAnims(), u => cur.TryGetValue(u, out var e) && IsAoHudSource(e.src), st, si);
    }

    // entry / rotation: the animations the AO added are its sit animations (minus known stand ones)
    static void AoLearnSits(IEnumerable<string> added, IEnumerable<UUID> before)
    {
        AoClassEnsureLoaded(); bool changed = false;
        lock (aoClassLock) foreach (var u in AoSitLearnable(added, before, aoStandIds)) changed |= aoSitIds.Add(u);
        if (changed) { AoClassSave(); Log("idlepose", "learned AO sit animations: " + string.Join(",", AoSitIdsNow().Select(x => x.ToString()[..8]))); }
    }
    // OnAvatarAnimation: an AO animation that starts in plain standing is a stand animation
    static void AoNoteStarted(UUID id, UUID src, IEnumerable<UUID> playingNow)
    {
        if (BuiltinAnims.ContainsKey(id) || !IsAoHudSource(src) || AoSitSeed.Contains(id)) return;
        bool ground = playingNow.Any(a => a == Animations.SIT_GROUND || a == Animations.SIT_GROUND_staticRAINED);
        if (!AoStandLearnOk(idlePosed != null, ground, client.Self.SittingOn != 0, DateTime.Now, aoSitTransitionUntil)) return;
        AoClassEnsureLoaded(); bool changed;
        lock (aoClassLock) { changed = aoStandIds.Add(id); aoSitIds.Remove(id); }
        if (changed) AoClassSave();
    }

    static async Task<bool> WaitFor(Func<bool> cond, int ms)
    {
        for (int t = 0; t < ms; t += 250) { if (cond()) return true; await Task.Delay(250); }
        return cond();
    }
    // turn the AO ground sit off, judged from what really plays: stand, then (only while an AO sit still plays and she is believed to be
    // ground sitting) the AO toggle, re-check after each move, bounded; logs the outcome
    static async Task<string> AoGroundSitEnsureOff(string why)
    {
        aoSitTransitionUntil = DateTime.Now.AddSeconds(60);
        var unk0 = AoUnclassifiedNow();
        if (!AoSitReallyOn() && unk0.Count == 0) return "AO force sit already off (no AO sit animation playing, only classified AO stands)";
        bool believed = AoBelievedSitting(idlePosed?.kind, aoSitSeenAt, DateTime.Now);
        bool mayToggle = idlePosed?.kind == "groundsit" || SelfAnims().Any(a => a == Animations.SIT_GROUND || a == Animations.SIT_GROUND_staticRAINED);
        int stands = 0, toggles = 0; string trail = "";
        while (AoStandNext(AoSitReallyOn(), stands + toggles, mayToggle) is var step && step is AoStandStep.Stand or AoStandStep.Toggle)
        {
            if (step == AoStandStep.Stand) { stands++; try { client.Self.Stand(); } catch { } trail += " stand"; await WaitFor(() => !AoSitReallyOn(), 1500); }
            else { toggles++; await AoGroundSitToggle($"off: {why} (AO sit still playing, toggle {toggles})"); trail += " toggle"; await WaitFor(() => !AoSitReallyOn(), 2500); }
        }
        // 2026-10-26: verify with anim_list that only CLASSIFIED AO stands play. An unclassified AO animation is not a stand: stand twice, then (only
        // if she is believed to have been sitting) press the AO force-sit toggle ONCE and re-verify; learn what stopped as a sit. Never a second toggle.
        var unk = AoSitReallyOn() ? new List<UUID>() : AoUnclassifiedNow();
        string unkNote = "";
        if (unk.Count > 0)
        {
            for (int i = 0; i < 2 && unk.Count > 0; i++) { stands++; try { client.Self.Stand(); } catch { } trail += " stand"; await WaitFor(() => AoUnclassifiedNow().Count == 0, 1500); unk = AoUnclassifiedNow(); }
            if (unk.Count > 0 && AoUnknownToggleOk(true, believed, toggles))
            {
                var before = unk; toggles++; await AoGroundSitToggle($"off: {why} (unclassified AO animation {string.Join(",", before.Select(x => x.ToString()[..8]))} still playing, one toggle)"); trail += " toggle(unclassified)";
                await WaitFor(() => AoUnclassifiedNow().Count == 0 && !AoSitReallyOn(), 2500);
                var learn = AoLearnAfterToggle(before, SelfAnims(), AoStandIdsNow());
                if (learn.Count > 0) { AoClassEnsureLoaded(); lock (aoClassLock) foreach (var u in learn) { aoSitIds.Add(u); aoStandIds.Remove(u); } AoClassSave(); unkNote = " learned as AO sit: " + string.Join(",", learn.Select(x => x.ToString()[..8])) + ";"; }
                unk = AoSitReallyOn() ? new List<UUID>() : AoUnclassifiedNow();
            }
            else if (unk.Count > 0) unkNote = " no toggle (not believed sitting);";
        }
        bool still = AoSitReallyOn(); aoSitTransitionUntil = DateTime.Now.AddSeconds(20);
        var msg = still ? $"STILL ground sitting: the AO sit animation keeps playing after{trail} ({stands} stands, {toggles} toggles)"
            : unk.Count > 0 ? $"NOT VERIFIED standing: AO animation unclassified ({string.Join(",", unk.Select(x => x.ToString()[..8]))}) still plays after{trail};{unkNote} may be a sit, not treated as a stand ({stands} stands, {toggles} toggles)"
            : $"standing, AO sit animation stopped after{trail};{unkNote} only classified AO stands play";
        Log("idlepose", $"verified stand ({why}): {msg}");
        return msg;
    }
    static HashSet<UUID> AoStandIdsNow() { AoClassEnsureLoaded(); lock (aoClassLock) return new(aoStandIds); }
}
