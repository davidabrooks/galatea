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
    // 2026-10-27 09:34 (login, force sit held ON by the AO): 985d3be4 (a VAMOSITG ground-sit animation, it also replaced 7c8e8863 in the 11:21 sit) was learned as a STAND on an
    // earlier login, because the AO starts the held sit right after login while she is "plainly standing"; from then on the status said "standing (only classified AO stands)"
    // on a sit and the login hover check retried against a sitting render. Seeded as a sit: the load re-asserts the seed over the learned file.
    internal static readonly UUID[] AoSitSeed = { UUID.Parse("2941ee1d-94c1-a56f-0d90-fe0e2e0918dc"), UUID.Parse("985d3be4-2b64-1123-c503-8dfa2eb083c6") };
    // 2026-10-26 13:20 (live test after #316): the N° 9 collar (worn at the chin) starts these four animations right after every login (RLV ready) and again at
    // random; they were "learned" as AO sit animations because they appeared while a ground-sit entry was running, so from then on a plain STAND counted as "AO sit
    // playing", the hover check ran as `sit VAMOSITG12` on a standing avatar and posted a -0.229 fix. They come from the collar attachment (anim-events.log: "from att @Chin"),
    // not from the AO, and are never sit animations. Seeded here; any animation seen starting from one of our own non-AO attachments joins them (persisted as "other").
    internal static readonly UUID[] AoNotSitSeed = {
        UUID.Parse("947dd903-cd66-158b-13ae-79a72ee7c947"), UUID.Parse("dccb493a-93f0-0903-041e-6a7469846d11"),
        UUID.Parse("c9574fd9-e482-d213-ac65-9d5c9b4e33cb"), UUID.Parse("201d84aa-63e9-5cb0-ac55-6c6c4ddd334e") };
    static readonly HashSet<UUID> aoOtherIds = new();
    // while a pose enters / leaves, the AO's animations are transitional: nothing is learned as "stand" from them
    static DateTime aoSitTransitionUntil = DateTime.MinValue;
    
    static void AoClassEnsureLoaded()
    {
        lock (aoClassLock)
        {
            if (aoClassLoaded) return; aoClassLoaded = true;
            foreach (var u in AoSitSeed) aoSitIds.Add(u);
            foreach (var u in AoNotSitSeed) aoOtherIds.Add(u);
            try
            {
                if (!File.Exists(AoClassFile)) return;
                var d = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(AoClassFile)) ?? new();
                foreach (var s in d.GetValueOrDefault("sit") ?? new()) if (UUID.TryParse(s, out var u)) aoSitIds.Add(u);
                foreach (var s in d.GetValueOrDefault("stand") ?? new()) if (UUID.TryParse(s, out var u)) aoStandIds.Add(u);
                foreach (var s in d.GetValueOrDefault("other") ?? new()) if (UUID.TryParse(s, out var u)) aoOtherIds.Add(u);
                foreach (var u in aoStandIds) aoSitIds.Remove(u);
                foreach (var u in AoSitSeed) { aoStandIds.Remove(u); aoSitIds.Add(u); }
                aoSitIds.ExceptWith(aoOtherIds);   // a collar animation learned as a sit by an older build is a sit no more
            }
            catch { }
        }
    }
    static void AoClassSave()
    {
        try
        {
            Dictionary<string, List<string>> d;
            lock (aoClassLock) d = new() { ["sit"] = aoSitIds.Select(x => x.ToString()).OrderBy(x => x).ToList(), ["stand"] = aoStandIds.Select(x => x.ToString()).OrderBy(x => x).ToList(), ["other"] = aoOtherIds.Select(x => x.ToString()).OrderBy(x => x).ToList() };
            Directory.CreateDirectory(Path.GetDirectoryName(AoClassFile)!);
            File.WriteAllText(AoClassFile, JsonSerializer.Serialize(d));
        }
        catch { }
    }
    static HashSet<UUID> AoSitIdsNow() { AoClassEnsureLoaded(); lock (aoClassLock) return new(aoSitIds); }

    // pure (tested): the AO-added animations of a pose entry/rotation that are sit animations: not already playing before, not a known stand animation
    internal static List<UUID> AoSitLearnable(IEnumerable<string> added, IEnumerable<UUID> before, IEnumerable<UUID> standIds, IEnumerable<UUID> otherIds = null)
    {
        var b = new HashSet<UUID>(before ?? Enumerable.Empty<UUID>()); var st = new HashSet<UUID>(standIds ?? Enumerable.Empty<UUID>());
        st.UnionWith(otherIds ?? Enumerable.Empty<UUID>());   // other = animations of our own non-AO attachments (the collar): never a sit
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
    // pure (tested): may the verified stand press the AO force-sit toggle? 2026-10-30 14:56:41 logout: the AO held its force sit (VAMOSITG25, 990f4821, a LEARNED
    // AO sit) since 14:45 under the collar poses; idlePosed was the collar and no built-in ground sit played, so the stand made 6 stands and 0 toggles, logged
    // "STILL ground sitting" and logged out sitting; the AO kept force sit ON and the 14:57:28 login came back sitting. A learned AO sit playing IS the AO's force
    // sit being on, so the toggle turns it off (never pressed when nothing plays: AoStandNext returns Done first).
    internal static bool AoStandMayToggle(string poseKind, bool builtinGroundSitPlaying, bool learnedAoSitPlaying)
        => poseKind == "groundsit" || builtinGroundSitPlaying || learnedAoSitPlaying;
    // pure (tested): login re-checks after the +20 s reset: the AO starts its held sit late (14:57:51 "already off", 990f4821 playing by 14:58:19)
    internal static readonly int[] LoginAoRecheckDelaysMs = { 15000, 30000 };
    internal static bool LoginAoRecheckNeeded(bool loggedIn, bool aoSitPlaying, bool sittingOnObject, bool posed) => loggedIn && aoSitPlaying && !sittingOnObject && !posed;
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
    // ---- login: the AO came back holding its force sit (2026-10-27 09:34) ----
    internal const double StuckSitWindowS = 300, StuckSitWaitMs = 11000;
    // pure (tested): may the login hover check release a held AO sit itself? Only when the render says she sits ("pose not rendered: ... sitting, not standing") while the
    // client believes she stands, in a login / stand request shortly after login, and never a second press in the same login (the toggle turns force sit ON when it is off)
    internal static bool StuckSitReleaseOk(string kind, string mismatchWhy, string requestWhy, double sinceLoginS, int pressesThisLogin)
        => kind == "stand" && mismatchWhy != null && mismatchWhy.Contains("sitting, not standing") && requestWhy is "login" or "stand"
           && sinceLoginS >= 0 && sinceLoginS <= StuckSitWindowS && pressesThisLogin == 0;
    // pure (tested): what a release taught us: exactly ONE non-built-in AO animation was playing before the press, it is gone after it and a classified AO stand
    // plays now -> it was the held sit (learn it; the caller also drops it from the stands). With several candidates nothing is learned (a rotating stand must never become a sit)
    internal static List<UUID> StuckSitLearn(IReadOnlyCollection<UUID> aoBefore, IEnumerable<UUID> playingAfter, IEnumerable<UUID> standIds)
        => aoBefore != null && aoBefore.Count == 1 ? AoLearnAfterToggle(aoBefore, playingAfter, standIds) : new();
    // pure (tested): the log / result line of the release
    internal static string StuckSitReleaseText(int pressesThisLogin, bool stillSitting) =>
        pressesThisLogin == 0 ? "no release pressed yet"
        : stillSitting ? "force sit pressed ONCE this login and she still sits: giving up (never pressed again, no stop_animations)"
        : "force sit pressed ONCE this login: the held AO sit is released";
    static int stuckSitPresses; static DateTime stuckSitLoginAt = DateTime.Now;
    // login (LoginJoinStart): a new login, a fresh single press
    static void StuckSitNewLogin() { stuckSitPresses = 0; stuckSitLoginAt = DateTime.Now; }
    // the ONE release: press the AO force-sit toggle once, wait ~11 s, re-read the animation list, learn the held sit. Never stop_animations, never a second press.
    static async Task<string> StuckSitRelease(string reason)
    {
        stuckSitPresses++;
        aoSitTransitionUntil = DateTime.Now.AddSeconds(60);   // nothing started during the release is learned as a stand
        Dictionary<UUID, (int seq, UUID src)> cur; lock (animLock) cur = new(ownAnims);
        var before = SelfAnims().Where(u => !BuiltinAnims.ContainsKey(u) && cur.TryGetValue(u, out var e) && IsAoHudSource(e.src)).Distinct().ToList();
        Log("idlepose", $"stuck AO sit at login ({reason}): pressing force sit ONCE (AO animations playing: {(before.Count == 0 ? "none" : string.Join(",", before.Select(x => x.ToString()[..8])))})");
        var r = await AoGroundSitToggle("login release: " + reason);   // waits ~11 s and re-reads the animations itself
        var after = SelfAnims().ToList();
        var learn = StuckSitLearn(before, after, AoStandIdsNow());
        if (learn.Count > 0) { AoClassEnsureLoaded(); lock (aoClassLock) foreach (var u in learn) { aoSitIds.Add(u); aoStandIds.Remove(u); } AoClassSave(); }
        bool still = AoSitReallyOn();
        aoSitTransitionUntil = DateTime.Now.AddSeconds(20);
        var msg = $"{StuckSitReleaseText(stuckSitPresses, still)} ({r}){(learn.Count > 0 ? "; learned as AO sit: " + string.Join(",", learn.Select(x => x.ToString()[..8])) : "")}";
        Log("idlepose", "stuck AO sit release: " + msg);
        return msg;
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
        Dictionary<UUID, (int seq, UUID src)> cur; lock (animLock) cur = new(ownAnims);
        foreach (var u in (added ?? Enumerable.Empty<string>()).Select(a => UUID.TryParse(a, out var x) ? x : UUID.Zero).Where(u => u != UUID.Zero && cur.TryGetValue(u, out var e) && IsOwnNonAoAttachment(e.src)))
            changed |= AoNoteOther(u);   // started by the collar / another attachment of ours: not the AO's
        lock (aoClassLock) foreach (var u in AoSitLearnable(added, before, aoStandIds, aoOtherIds)) changed |= aoSitIds.Add(u);
        if (changed) { AoClassSave(); Log("idlepose", "learned AO sit animations: " + string.Join(",", AoSitIdsNow().Select(x => x.ToString()[..8]))); }
    }
    // OnAvatarAnimation: an AO animation that starts in plain standing is a stand animation
    // an animation that one of our own non-AO attachments plays (the collar): never a sit; returns whether anything changed (saved by the caller)
    static bool AoNoteOther(UUID id)
    {
        AoClassEnsureLoaded(); bool changed;
        lock (aoClassLock) { changed = aoOtherIds.Add(id); changed |= aoSitIds.Remove(id); }
        if (changed) { Log("idlepose", $"animation {id.ToString()[..8]} is played by one of my own non-AO attachments: never an AO sit"); AoClassSave(); }
        return changed;
    }
    static void AoNoteStarted(UUID id, UUID src, IEnumerable<UUID> playingNow)
    {
        if (!BuiltinAnims.ContainsKey(id) && IsOwnNonAoAttachment(src)) { AoNoteOther(id); return; }
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
        if (!AoSitReallyOn() && unk0.Count == 0)
        {
            // 2026-10-28: "already off" came from the animation list alone while the render showed her sitting on the ground. The render is evidence too: ask it (one scene export,
            // not at logout) and when it says sitting, that is NOT "already off": say MISMATCH, stand once through the sim, never press the AO toggle on a guess (it turns force sit ON when off)
            if (why != "logout" && hoverSvc.RenderEvidenceNow().call == null) try { await Task.WhenAny(hoverSvc.PoseProbe(), Task.Delay(20000)); } catch { }
            if (hoverSvc.RenderEvidenceNow() is { call: PoseCall.Sitting } rr)
            {
                try { client.Self.Stand(); } catch { }
                var mm = $"NOT VERIFIED standing: MISMATCH: render says sitting ({rr.detail}), the animation list shows no AO sit; sim stand sent once, force-sit button NOT pressed (a press on a guess can turn force sit ON); run `idle_pose verify` and look at her";
                Log("idlepose", $"verified stand ({why}): {mm}"); return mm;
            }
            return "AO force sit already off (no AO sit animation playing, only classified AO stands" + (hoverSvc.RenderEvidenceNow().call == PoseCall.Standing ? "; the render shows her standing)" : "; no fresh render)");
        }
        bool believed = AoBelievedSitting(idlePosed?.kind, aoSitSeenAt, DateTime.Now);
        bool mayToggle = AoStandMayToggle(idlePosed?.kind, SelfAnims().Any(a => a == Animations.SIT_GROUND || a == Animations.SIT_GROUND_staticRAINED), AoSitReallyOn());
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
