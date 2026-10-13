using LibreMetaverse;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace GalatayText;

// 2026-10-12 David: every outfit change at Peronaut happens in the bedroom. The 'outfit wear' / 'bikini on|off' commands
// walk there first (stand up if seated) unless 'force' is given; internal login / android-only paths call the wear
// functions directly and are not affected.
public static partial class Program
{
    // pure: does this command have to walk to the bedroom first?
    internal static bool NeedsBedroomWalk(string region, Vector3 pos, bool force)
        => !force && string.Equals(region, "Peronaut", StringComparison.OrdinalIgnoreCase) && !InBedroom(pos);

    static async Task<string> BikiniDispatch(string[] a)
    {
        if (a.Length >= 1 && a[0] == "on") return await BikiniOn(auto: false);
        if (a.Length >= 1 && a[0] == "off") return await BikiniOff();
        if (a.Length >= 1 && a[0] == "selftest") return BikiniSelfTest();
        return "usage: bikini on|off|selftest [force]";
    }

    // null = already fine; else what happened. Never throws.
    static async Task<string> ChangeInBedroomFirst(string what, bool force)
    {
        try
        {
            if (!LoggedIn) return null;
            var sim = client.Network.CurrentSim;
            if (!NeedsBedroomWalk(sim?.Name, client.Self.SimPosition, force)) return null;
            if (client.Self.SittingOn != 0) { client.Self.Stand(); await Task.Delay(2500); }
            var g = LoadGraph(sim.Name);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            bool ok = await WalkToChangeSpot(g, $"to change ({what})", cts.Token);
            var p = client.Self.SimPosition;
            var msg = $"bedroom first ({what}): {(ok && InBedroom(p) ? "changing in the bedroom" : "walk did not reach the bedroom; changing where she stopped")} at {p.X:F1},{p.Y:F1},{p.Z:F1}";
            Log("outfit", msg); return msg;
        }
        catch (Exception ex) { var m = $"bedroom first ({what}): {ex.GetBaseException().Message}"; Log("outfit", m); return m; }
    }
}
