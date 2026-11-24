// ScriptsWatch.cs (2026-11-23, David t3491u: "you should probably check the state of your scripts when you teleport
// somewhere"). David teleported me to Ahern, where the parcel does not let visitors' scripts run, and nothing noticed it.
// Now, after every teleport, region crossing and walk onto another parcel, the 1 s ticker reads the parcel under me (the
// library's parcel cache; the sim sends ParcelProperties on arrival and on every parcel change, else one request) and
// logs ONE line when the verdict, the region or the parcel changes:
//   [scripts] Ahern parcel 'X': scripts OFF for my attachments (...)   /   [scripts] Ahern parcel 'X': scripts on (...)
// The line is an ordinary log event, so poll_events shows it (kind "scripts"); no webhook kind is added. 'status' carries
// scripts_allowed (true / false / "unknown") and scripts_detail.
//
// The SL rule (sources checked 2026-11-23):
//   - Region/estate "Disable Scripts" (RegionFlags.SkipScripts / EstateSkipScripts): no script runs.
//   - Parcel About Land > Options > Run Scripts: Everyone (ParcelFlags.AllowOtherScripts) or Group (AllowGroupScripts: objects
//     whose group is the parcel group; for my attachments that is my ACTIVE group). The parcel owner's scripts always run.
//   - The parcel limit only applies below about 50 m above the terrain (Maestro Linden,
//     https://feedback.secondlife.com/server-bugs/p/temp-attaching-above-script-danger-height-now-causes-scripts-to-not-run).
//   - A script that has called llTakeControls keeps running (with the other scripts of the same prim) when I enter a
//     no-script parcel; many AOs do this on purpose (https://wiki.secondlife.com/wiki/LlTakeControls, Notes). I cannot see
//     which worn scripts did that, so OFF means: the collar, leash and HUD menus stop; an AO may keep going.
// The verdict itself is CollarScripts.cs AttachmentScriptsAllowed (the same one the collar Stealth steps use).
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    /// <summary>The last verdict for where I stand (null before the first one) and the region/parcel it was for.</summary>
    internal sealed record ScriptsState(ulong Region, string RegionName, int ParcelLocalId, CollarScriptGate Gate);
    static volatile ScriptsState scriptsState;
    static DateTime scriptsArrivedAt = DateTime.MinValue, scriptsAskedAt = DateTime.MinValue;

    /// <summary>Pure (tested): log a new line? Only when the region, the parcel or the verdict changed.</summary>
    internal static bool ScriptsStateChanged(ScriptsState prev, ulong region, int parcelLocalId, bool? allowed) =>
        prev == null || prev.Region != region || prev.ParcelLocalId != parcelLocalId || prev.Gate?.Allowed != allowed;

    /// <summary>Pure (tested): the [scripts] log line.</summary>
    internal static string ScriptsLine(string region, CollarScriptGate g) => g.Allowed switch
    {
        false => $"{region} parcel '{g.Parcel}': scripts OFF for my attachments (scripts={g.Scripts}{(g.Reason != null ? "; " + g.Reason : "")}): " +
                 "collar/leash and HUD scripts are stopped here; only scripts that took controls (often an AO) keep running",
        true => $"{region} parcel '{g.Parcel}': scripts on (scripts={g.Scripts}{(g.Reason != null ? "; " + g.Reason : "")})",
        _ => $"{region}: parcel script permission unknown ({g.Reason ?? "no parcel data"})",
    };

    /// <summary>Pure (tested): status value: true / false / "unknown".</summary>
    internal static object ScriptsAllowedValue(ScriptsState st, ulong currentRegion) =>
        st?.Gate?.Allowed is bool b && st.Region == currentRegion ? b : "unknown";

    static void HookScriptsWatch()
    {   // teleport / crossing / login: forget the old verdict so the new region always gets its line
        client.Network.SimChanged += (s, e) => { scriptsState = null; scriptsArrivedAt = DateTime.Now; scriptsAskedAt = DateTime.MinValue; };
    }

    static void ScriptsWatchTick()
    {
        var sim = client.Network.CurrentSim;
        if (!LoggedIn || sim == null || !sim.AgentMovementComplete) return;
        var pos = ParcelSelfPos();
        var p = ParcelUnderMe(sim, pos);
        if (p == null)
        {
            // one request per 30 s (ParcelAsk also throttles to 10 s and shares ParcelWatch's in-flight flag); the reply fills the library cache
            if ((DateTime.Now - scriptsAskedAt).TotalSeconds >= 30) { scriptsAskedAt = DateTime.Now; ParcelAsk(sim, pos); }
            // no data 15 s after arriving: say so once for this region (status says "unknown" meanwhile)
            if (scriptsState == null && (DateTime.Now - scriptsArrivedAt).TotalSeconds > 15)
            {
                var g0 = ScriptGateHere(sim, null, pos);
                scriptsState = new ScriptsState(sim.Handle, sim.Name, -1, g0);
                Log("scripts", ScriptsLine(sim.Name, g0));
            }
            return;
        }
        var g = ScriptGateHere(sim, p, pos);
        if (!ScriptsStateChanged(scriptsState, sim.Handle, p.LocalID, g.Allowed)) return;
        scriptsState = new ScriptsState(sim.Handle, sim.Name, p.LocalID, g);
        Log("scripts", ScriptsLine(sim.Name, g));
    }

    static string ScriptsDetail()
    {
        var st = scriptsState; var sim = client?.Network?.CurrentSim;
        return st?.Gate == null || sim == null || st.Region != sim.Handle ? "not checked yet in this region" : ScriptsLine(st.RegionName, st.Gate);
    }
}
