// CollarScripts.cs (2026-11-16, David t3386u/t3387u): in Bonifacio's 'Welcome Area' the parcel allows group scripts only and
// Galatay is not in its group, so the N° 9 collar's scripts do not run there: its menu never opens, and the logout veil step
// logged "WARNING: collar Stealth NOT confirmed" and an Android outfit "collar menu did not open". Every collar Stealth path
// (CollarStealthSet / CollarStealthPrepare: logout veil, login show, Android* outfits, 'collar stealth') now reads the parcel's
// script permission first (the same ParcelProperties flags parcel_info decodes) and, when my attachments' scripts cannot run,
// leaves the collar untouched and says so plainly. Unknown parcel data -> the old behaviour (touch the collar).
// SL semantics used: the parcel owner's scripts always run; 'other' scripts -> everyone; 'group' scripts -> only objects whose
// group is the parcel group, which for attachments means my ACTIVE group equals the parcel group; region/estate
// "disable scripts" stops everything.
// 2026-11-23 (David t3491u, ScriptsWatch.cs) two more parts of the SL rule, see ScriptsWatch.cs for the sources:
//   - the parcel's Run Scripts limit only applies below about 50 m above the terrain (ScriptsParcelLimitAglM);
//   - a script that has called llTakeControls (with its prim's other scripts) keeps running on no-script land (many AOs do
//     this on purpose). This client cannot see which worn scripts did that, so "off" means "scripts that did not take
//     controls stop", which covers the collar, leash and HUD menus.
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    /// <summary>The collar script check: Allowed null = unknown (parcel data missing; behave as before).</summary>
    internal sealed record CollarScriptGate(bool? Allowed, string Parcel, string Scripts, string Reason, DateTime At)
    {
        public bool Off => Allowed == false;
        public string SkipLine => $"skipped Stealth: scripts are off for me on this parcel ({Parcel}, scripts={Scripts}{(Reason != null ? "; " + Reason : "")}); the collar was not touched";
        public string Summary => Allowed switch
        {
            true => $"scripts run for me on this parcel ({Parcel}, scripts={Scripts}{(Reason != null ? "; " + Reason : "")})",
            false => $"scripts are off for me on this parcel ({Parcel}, scripts={Scripts}{(Reason != null ? "; " + Reason : "")})",
            _ => $"parcel script permission unknown ({Reason ?? "no parcel data"})",
        };
    }

    /// <summary>Pure (tested): may my worn attachments' scripts run here? (allowed, reason) — reason is null for the plain cases.</summary>
    /// <param name="aboveGroundM">my height above the terrain when known; above <see cref="ScriptsParcelLimitAglM"/> the parcel's
    /// limit does not apply (the region/estate switch still does).</param>
    internal static (bool allowed, string reason) AttachmentScriptsAllowed(ParcelFlags flags, UUID parcelOwner, UUID parcelGroup, UUID me, UUID activeGroup, bool regionScriptsOff, float? aboveGroundM = null)
    {
        if (regionScriptsOff) return (false, "scripts are disabled for the whole region");
        if (me != UUID.Zero && parcelOwner == me) return (true, "I own the parcel");
        if (flags.HasFlag(ParcelFlags.AllowOtherScripts)) return (true, null);
        string why = null;
        if (flags.HasFlag(ParcelFlags.AllowGroupScripts))
        {
            if (parcelGroup == UUID.Zero) why = "the parcel has no group";
            else if (activeGroup == parcelGroup) return (true, "my active group is the parcel group");
            else why = "my active group is not the parcel group";
        }
        if (aboveGroundM is float agl && agl > ScriptsParcelLimitAglM)
            return (true, $"{(why != null ? why + ", but " : "")}I am {agl:0} m above the ground and the parcel limit only applies below {ScriptsParcelLimitAglM:0} m");
        return (false, why);
    }

    /// <summary>The parcel's Run Scripts limit applies below this height above the terrain (Maestro Linden, SL feedback 2026-08).</summary>
    // ponytail: fixed 50 m; ceiling = a server change of the cut-off (older reports say 75 m) makes the verdict wrong in that band
    internal const float ScriptsParcelLimitAglM = 50f;

    /// <summary>Pure (tested): the gate from a parcel (null = unknown).</summary>
    internal static CollarScriptGate CollarScriptGateFrom(string parcelName, ParcelFlags? flags, UUID parcelOwner, UUID parcelGroup, UUID me, UUID activeGroup, bool regionScriptsOff, DateTime now, float? aboveGroundM = null)
    {
        if (flags is not ParcelFlags f)
            return regionScriptsOff ? new CollarScriptGate(false, parcelName ?? "?", "?", "scripts are disabled for the whole region", now)
                                    : new CollarScriptGate(null, parcelName, null, "no parcel data", now);
        var (ok, why) = AttachmentScriptsAllowed(f, parcelOwner, parcelGroup, me, activeGroup, regionScriptsOff, aboveGroundM);
        return new CollarScriptGate(ok, string.IsNullOrWhiteSpace(parcelName) ? "unnamed parcel" : parcelName, DecodeParcelFlags(f).Scripts, why, now);
    }

    /// <summary>The last check, for 'collar scripts' / a later recheck after moving.</summary>
    internal static volatile CollarScriptGate collarScriptGate;

    /// <summary>Read the parcel I stand on: the simulator's parcel cache (filled by the ParcelProperties sent on arrival), else one
    /// request of at most <paramref name="timeoutMs"/>. Any failure -> unknown (the collar is touched as before).</summary>
    static async Task<CollarScriptGate> CollarScriptsCheck(int timeoutMs = 2500)
    {
        CollarScriptGate g;
        try
        {
            var sim = client.Network.CurrentSim;
            if (!LoggedIn || sim == null) g = new CollarScriptGate(null, null, null, "not in a region", DateTime.Now);
            else
            {
                var pos = ParcelSelfPos();
                var p = ParcelUnderMe(sim, pos) ?? await ParcelAt(sim, pos.X, pos.Y, timeoutMs);
                g = ScriptGateHere(sim, p, pos);
            }
        }
        catch (Exception ex) { g = new CollarScriptGate(null, null, null, "parcel check failed: " + ex.GetBaseException().Message, DateTime.Now); }
        collarScriptGate = g;
        return g;
    }

    /// <summary>The library's parcel cache (filled by every ParcelProperties: the one the sim sends on arrival and when I walk
    /// onto another parcel, and the ones we request) for my 4 m cell; null when it has none.</summary>
    static Parcel ParcelUnderMe(Simulator sim, Vector3 pos)
    {
        try
        {
            int ix = Math.Clamp((int)(pos.X / 4), 0, 63), iy = Math.Clamp((int)(pos.Y / 4), 0, 63);
            var id = sim.ParcelMap[iy, ix];
            return id > 0 && sim.Parcels.TryGetValue(id, out var p) ? p : null;
        }
        catch { return null; }
    }

    /// <summary>The gate for parcel <paramref name="p"/> (null = no data) of <paramref name="sim"/> at my position.</summary>
    static CollarScriptGate ScriptGateHere(Simulator sim, Parcel p, Vector3 pos)
    {
        bool regionOff = (sim.Flags & (RegionFlags.SkipScripts | RegionFlags.EstateSkipScripts)) != 0;
        float? agl = null;
        try { if (sim.TerrainHeightAtPoint((int)Math.Clamp(pos.X, 0, 255), (int)Math.Clamp(pos.Y, 0, 255), out var th)) agl = pos.Z - th; } catch { }
        return p == null ? CollarScriptGateFrom(null, null, UUID.Zero, UUID.Zero, client.Self.AgentID, client.Self.ActiveGroup, regionOff, DateTime.Now)
                         : CollarScriptGateFrom(p.Name, p.Flags, p.OwnerID, p.GroupID, client.Self.AgentID, client.Self.ActiveGroup, regionOff, DateTime.Now, agl);
    }

    /// <summary>Before touching the collar for Stealth: the skip line when my scripts cannot run here (logged), else null.</summary>
    static async Task<string> CollarScriptsSkip(string why)
    {
        var g = await CollarScriptsCheck();
        if (!g.Off) return null;
        Log("collar", $"{g.SkipLine} ({why})");
        return g.SkipLine;
    }

    /// <summary>Pure (tested): did a collar step skip because scripts are off (not a failure)?</summary>
    internal static bool CollarSkippedForScripts(string result) => (result ?? "").StartsWith("skipped Stealth: scripts are off", StringComparison.Ordinal);
}
