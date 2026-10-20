// RlvPlace.cs (2026-10-19, David): when RLV sits her, stands her or teleports her, the chat webhook gets an 'rlv_placed'
// event (object, where she is, whether @unsit is on). After an RLV sit she is HELD there: wander pauses ('rlv'), autofollow
// is snoozed, and any automatic stand-up (wander, follow, pose fixes, height guard, ...) is undone by sitting her back on
// the same seat, until she is released: the 'stand' command (David asked), @unsit=force, MAYDAY or rlv_clear.
// ponytail: re-sit after the fact instead of blocking each of the 17 Stand() call sites; ceiling = a 2 s visible hop.
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    // pure (tested): may she stand by her own choice? Only explicit RLV restrictions lock standing (the collar's Force Sit
    // "Strict" sends @unsit=n). The soft hold after a placement never blocks a deliberate stand. (TPE has nothing to do with it:
    // N° 9 README, Force Sit section.)
    internal static string SelfStandBlock(bool unsitRestricted) =>
        unsitRestricted ? "refused (RLV @unsit is on, e.g. the collar's Strict force-sit; say MAYDAY or use 'rlv clear' to release)" : null;

    static volatile string rlvHoldSeat;   // object uuid as string, null = not held
    static string rlvHoldName;

    // pure (tested): should the hold guard re-sit her now?
    internal static bool RlvHoldResit(string holdSeat, uint sittingOn, bool releasing) => holdSeat != null && sittingOn == 0 && !releasing;
    // pure (tested): stand requests that release the hold (not the automatic ones)
    internal static bool RlvReleasesHold(string why) => why is "stand command" or "rlv unsit" or "safeword" or "rlv clear" or "source gone";

    static volatile bool rlvReleasing;
    static void RlvPlaced(string how, string objName, UUID obj)
    {
        string where = "";
        try { where = $"{client.Network.CurrentSim?.Name} {Fmt(client.Self.SimPosition)}"; } catch { }
        bool unsitOn = rlv.Has("unsit");
        if (how == "sit") { rlvHoldSeat = obj.ToString(); rlvHoldName = objName; RlvSaveState(); try { SetPauseNow("rlv", $"RLV sat me on '{objName}'"); } catch { } afSnoozeUntil = DateTime.MaxValue; }
        var text = $"RLV {how}: {(how == "sit" ? $"sat on '{objName}'" : how == "stand" ? "stood up" : $"teleported")}; now at {where}; @unsit {(unsitOn ? "ON (can't stand)" : "off")}{(how == "sit" ? "; held here (no wander/follow/auto-stand) until David says stand or I'm released" : "")}";
        RlvLog("placement: " + text);
        try { Notify("rlv_placed", objName, obj, text, null); } catch { }
    }
    static void RlvRelease(string why)
    {
        if (rlvHoldSeat == null || !RlvReleasesHold(why)) return;
        RlvLog($"hold on '{rlvHoldName}' released ({why})");
        rlvHoldSeat = null; afSnoozeUntil = DateTime.Now; RlvSaveState();
        try { if (wanderPause == "rlv") wanderPause = null; } catch { }
    }
    static void RlvHoldLoopRun() => _ = Task.Run(async () =>
    {
        while (true)
        {
            await Task.Delay(2000);
            try
            {
                var seat = rlvHoldSeat;
                if (!LoggedIn || !RlvHoldResit(seat, client.Self.SittingOn, rlvReleasing)) continue;
                RlvLog($"hold: something stood me up from '{rlvHoldName}': sitting back down");
                client.Self.RequestSit(new UUID(seat), Vector3.Zero); await Task.Delay(1200); client.Self.Sit();
            }
            catch { }
        }
    });

    // ---- persistence across a relog (prim ids change; items do not) ----
    static readonly string RlvStateFile = "/workspace/secondlife/rlv/rlv-state.json";
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<UUID, UUID> rlvSrcItem = new();
    internal sealed record RlvSaved(string item, string name, List<string> keys);
    internal sealed record RlvStateDoc(List<RlvSaved> sources, string holdSeat, string holdName, string holdRegion, LeashSaved leash = null, bool leashWalk = false, bool leashWanderOff = true);
    static readonly object rlvSaveLock = new();
    static void RlvSaveState()
    {
        try
        {
            var doc = new RlvStateDoc(rlv.Export().Where(e => rlvSrcItem.ContainsKey(e.src))
                                         .Select(e => new RlvSaved(rlvSrcItem[e.src].ToString(), e.name, e.keys)).ToList(),
                                      rlvHoldSeat, rlvHoldName, client?.Network?.CurrentSim?.Name, LeashExport(), leashWalk, leashWanderOff);
            lock (rlvSaveLock) File.WriteAllText(RlvStateFile, System.Text.Json.JsonSerializer.Serialize(doc));
        }
        catch { }
    }
    static void RlvRestoreAtLogin() => _ = Task.Run(async () =>
    {
        RlvStateDoc doc = null;
        try { if (File.Exists(RlvStateFile)) doc = System.Text.Json.JsonSerializer.Deserialize<RlvStateDoc>(File.ReadAllText(RlvStateFile)); } catch { }
        if (doc != null) { leashWalk = doc.leashWalk; leashWanderOff = doc.leashWanderOff; }   // Leash.cs option
        if (doc == null || (doc.sources.Count == 0 && doc.holdSeat == null && doc.leash == null)) return;
        RlvLog($"relog: restoring {doc.sources.Sum(x => x.keys.Count)} saved restriction(s) from {doc.sources.Count} item(s){(doc.holdSeat != null ? $", hold on '{doc.holdName}'" : "")}");
        var left = doc.sources.ToList();
        for (int i = 0; i < 60 && left.Count > 0; i++)   // up to 2 min for the attachments to show up
        {
            await Task.Delay(2000);
            foreach (var sv in left.ToList())
            {
                var p = WornPrims().FirstOrDefault(x => AttachItemId(x).ToString() == sv.item); if (p == null) continue;
                rlvSrcItem[p.ID] = AttachItemId(p);
                foreach (var k in sv.keys) { var c = k.IndexOf(':'); rlv.Add(p.ID, sv.name, c < 0 ? k : k[..c], c < 0 ? "" : k[(c + 1)..]); }
                left.Remove(sv); RlvLog($"relog: restored {string.Join(",", sv.keys)} for '{sv.name}' (now {p.ID})");
            }
        }
        foreach (var sv in left) RlvLog($"relog: '{sv.name}' ({sv.item}) not worn after login: its saved restrictions dropped");
        if (doc.holdSeat != null && doc.holdRegion == client.Network.CurrentSim?.Name)
        {   // the collar normally re-sits her itself (@sit:..=force); the hold loop sits her back if it doesn't
            rlvHoldName = doc.holdName; rlvHoldSeat = doc.holdSeat; afSnoozeUntil = DateTime.MaxValue;
            try { SetPauseNow("rlv", $"relog: held on '{doc.holdName}'"); } catch { }
            RlvLog($"relog: hold on '{doc.holdName}' restored; re-sit there if not seated");
        }
        if (doc.leash != null && (doc.leash.region == client.Network.CurrentSim?.Name || doc.leash.mode == "leash")) LeashRestore(doc.leash);   // Leash.cs
        RlvSaveState();
    });

    // @tpto:<global x>/<global y>/<z>=force
    static string RlvTpTo(string o)
    {
        var p = o.Split('/'); if (p.Length < 3 || !float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z)) return "bad tpto";
        if (rlv.Has("tploc")) return "refused: @tploc";
        uint gx = (uint)(Math.Floor(x / 256) * 256), gy = (uint)(Math.Floor(y / 256) * 256);
        client.Self.RequestTeleport(Utils.UIntsToLong(gx, gy), new Vector3(x - gx, y - gy, z));
        return $"teleport requested to {x:F0}/{y:F0}/{z:F0}";
    }
}
