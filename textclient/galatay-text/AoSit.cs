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
    // while a pose enters / leaves, the AO's animations are transitional: nothing is learned as "stand" from them
    static DateTime aoSitTransitionUntil = DateTime.MinValue;
    
    static void AoClassEnsureLoaded()
    {
        lock (aoClassLock)
        {
            if (aoClassLoaded) return; aoClassLoaded = true;
            try
            {
                if (!File.Exists(AoClassFile)) return;
                var d = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(AoClassFile)) ?? new();
                foreach (var s in d.GetValueOrDefault("sit") ?? new()) if (UUID.TryParse(s, out var u)) aoSitIds.Add(u);
                foreach (var s in d.GetValueOrDefault("stand") ?? new()) if (UUID.TryParse(s, out var u)) aoStandIds.Add(u);
                foreach (var u in aoStandIds) aoSitIds.Remove(u);
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
    internal static string AoRealState(bool aoSitPlaying, IReadOnlyCollection<string> playingSitIds, bool simSittingOnObject, bool simSaysGround)
    {
        string real = simSittingOnObject ? "sitting on an object" : aoSitPlaying ? "AO sit playing (" + (playingSitIds.Count == 0 ? "built-in ground sit" : string.Join(",", playingSitIds)) + ")" : "standing (no AO sit playing)";
        string sim = simSittingOnObject ? "on an object" : simSaysGround ? "ground sit" : "stand";
        bool mismatch = !simSittingOnObject && aoSitPlaying != simSaysGround;
        return $"real state: {real}; sim says {sim}{(mismatch ? " (MISMATCH: the AO is the authority)" : "")}";
    }

    // learned sit animations only (the raw animations of a pose include AO stand animations that restart during it)
    static bool AoSitReallyOn() => AoSitPlaying(SelfAnims(), client.Self.SittingOn != 0, AoSitIdsNow());

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
        if (BuiltinAnims.ContainsKey(id) || !IsAoHudSource(src)) return;
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
        if (!AoSitReallyOn()) return "AO force sit already off (no AO sit animation playing)";
        bool mayToggle = idlePosed?.kind == "groundsit" || SelfAnims().Any(a => a == Animations.SIT_GROUND || a == Animations.SIT_GROUND_staticRAINED);
        int stands = 0, toggles = 0; string trail = "";
        while (AoStandNext(AoSitReallyOn(), stands + toggles, mayToggle) is var step && step is AoStandStep.Stand or AoStandStep.Toggle)
        {
            if (step == AoStandStep.Stand) { stands++; try { client.Self.Stand(); } catch { } trail += " stand"; await WaitFor(() => !AoSitReallyOn(), 1500); }
            else { toggles++; await AoGroundSitToggle($"off: {why} (AO sit still playing, toggle {toggles})"); trail += " toggle"; await WaitFor(() => !AoSitReallyOn(), 2500); }
        }
        bool still = AoSitReallyOn(); aoSitTransitionUntil = DateTime.Now.AddSeconds(20);
        var msg = still ? $"STILL ground sitting: the AO sit animation keeps playing after{trail} ({stands} stands, {toggles} toggles)" : $"standing, AO sit animation stopped after{trail}";
        Log("idlepose", $"verified stand ({why}): {msg}");
        return msg;
    }
}
