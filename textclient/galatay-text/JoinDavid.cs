using LibreMetaverse;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GalatayText;

// 2026-10-12 David: setting off to join David (goto_avatar David, follow David, his lure) while he is in the Peronaut
// beach zone: human form only, she first walks to the bedroom and puts the bikini on, then goes to him. beachMode is set
// as on a wander beach trip, so the way back up changes back in the bedroom (wander / outfit-zone tick). Androids stay.
public static partial class Program
{
    // pure
    internal static bool JoinDavidNeedsBikini(bool androidWorn, bool bikiniWorn, string region, Vector3? davidPos)
        => !androidWorn && !bikiniWorn && davidPos != null && string.Equals(region, "Peronaut", StringComparison.OrdinalIgnoreCase)
           && OutfitZoneFor("Peronaut", davidPos.Value, false) == "beach";

    internal static bool NamesDavid(string s) => !string.IsNullOrWhiteSpace(s) &&
        (s.Trim().Equals("david", StringComparison.OrdinalIgnoreCase) || s.Trim().Equals("me", StringComparison.OrdinalIgnoreCase)
         || s.Trim().StartsWith("david nightingale", StringComparison.OrdinalIgnoreCase));

    static Vector3? DavidPos()
    {
        var a = Avatars().FirstOrDefault(t => t.av.ID == DavidId);
        if (a.av != null) return a.pos;
        try { foreach (var kv in coarseBy.ToArray()) if (kv.Key == client.Network.CurrentSim?.Handle && kv.Value.TryGetValue(DavidId, out var p)) return p; } catch { }
        return null;
    }

    // null = nothing to do
    static async Task<string> JoinDavidBeachPrep(string what)
    {
        try
        {
            if (!LoggedIn) return null;
            var dp = DavidPos();
            if (!JoinDavidNeedsBikini(AndroidWorn(), BikiniWorn(), client.Network.CurrentSim?.Name, dp)) return null;
            if (client.Self.SittingOn != 0) { client.Self.Stand(); await Task.Delay(2500); }
            var g = LoadGraph("Peronaut");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            Log("outfit", $"join David on the beach ({what}): bedroom + bikini first (David at {dp.Value.X:F0},{dp.Value.Y:F0})");
            await BikiniIndoorsIfBeachBound(g, dp.Value, null, $"join David ({what})", cts.Token);
            return BikiniWorn() ? "joined-David prep: bikini on in the bedroom" : "joined-David prep: bikini change did not happen (see log)";
        }
        catch (Exception ex) { var m = "join David prep: " + ex.GetBaseException().Message; Log("outfit", m); return m; }
    }
}
