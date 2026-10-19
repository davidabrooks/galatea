// RlvPlace.cs (2026-10-19, David): when RLV sits her, stands her or teleports her, the chat webhook gets an 'rlv_placed'
// event (object, where she is, whether @unsit is on). After an RLV sit she is HELD there: wander pauses ('rlv'), autofollow
// is snoozed, and any automatic stand-up (wander, follow, pose fixes, height guard, ...) is undone by sitting her back on
// the same seat, until she is released: the 'stand' command (David asked), @unsit=force, MAYDAY or rlv_clear.
// ponytail: re-sit after the fact instead of blocking each of the 17 Stand() call sites; ceiling = a 2 s visible hop.
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    // collar TPE checkbox, cached from any collar Access menu ('☐ TPE' / '☑ TPE'); never changed by us
    internal static volatile bool rlvTpe;
    static readonly string TpeFile = "/workspace/secondlife/rlv/collar-tpe.txt";
    internal static bool? TpeFromButtons(IEnumerable<string> b)
    {
        var t = (b ?? Array.Empty<string>()).FirstOrDefault(x => x != null && System.Text.RegularExpressions.Regex.IsMatch(x, @"\bTPE\b"));
        return t == null ? null : (t.Contains('☑') || t.Contains('☒'));
    }
    static void TpeHook()
    {
        try { rlvTpe = File.Exists(TpeFile) && File.ReadAllText(TpeFile).Trim() == "on"; } catch { }
        client.Self.ScriptDialog += (s, e) =>
        {
            if (e.ObjectName != "Collar" || e.OwnerID != client.Self.AgentID) return;
            var v = TpeFromButtons(e.ButtonLabels); if (v == null) return;
            if (v.Value != rlvTpe) RlvLog($"collar TPE read from its Access menu: {(v.Value ? "ON" : "off")}");
            rlvTpe = v.Value; try { File.WriteAllText(TpeFile, v.Value ? "on\n" : "off\n"); } catch { }
        };
    }
    // pure (tested): may she stand by her own choice? @unsit=n always blocks; while held after a placement, TPE on blocks too
    internal static string SelfStandBlock(bool unsitRestricted, bool held, bool tpe) =>
        unsitRestricted ? "refused (RLV @unsit is on; say MAYDAY or use 'rlv clear' to release)"
        : held && tpe ? "refused (placed by RLV and the collar's TPE is on: David stands me, or MAYDAY / rlv clear)" : null;

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
        if (how == "sit") { rlvHoldSeat = obj.ToString(); rlvHoldName = objName; try { SetPauseNow("rlv", $"RLV sat me on '{objName}'"); } catch { } afSnoozeUntil = DateTime.MaxValue; }
        var text = $"RLV {how}: {(how == "sit" ? $"sat on '{objName}'" : how == "stand" ? "stood up" : $"teleported")}; now at {where}; @unsit {(unsitOn ? "ON (can't stand)" : "off")}{(how == "sit" ? "; held here (no wander/follow/auto-stand) until David says stand or I'm released" : "")}";
        RlvLog("placement: " + text);
        try { Notify("rlv_placed", objName, obj, text, null); } catch { }
    }
    static void RlvRelease(string why)
    {
        if (rlvHoldSeat == null || !RlvReleasesHold(why)) return;
        RlvLog($"hold on '{rlvHoldName}' released ({why})");
        rlvHoldSeat = null; afSnoozeUntil = DateTime.Now;
        try { if (wanderPause == "rlv") wanderPause = null; } catch { }
    }
    static void RlvHoldLoop() { TpeHook(); RlvHoldLoopRun(); }
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
