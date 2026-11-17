using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-11-16 David t3387u: Bonifacio 'Welcome Area' is scripts=group and Galatay is not in its group, so the collar menu never
// opened and the logout logged "WARNING: collar Stealth NOT confirmed". The collar Stealth steps now skip such parcels.
public class CollarScriptsTests
{
    static readonly UUID Me = new("11111111-1111-1111-1111-111111111111");
    static readonly UUID Owner = new("22222222-2222-2222-2222-222222222222");
    static readonly UUID ParcelGroup = new("33333333-3333-3333-3333-333333333333");
    static readonly UUID OtherGroup = new("44444444-4444-4444-4444-444444444444");

    [Fact] public void Everyone_scripts_run()
    {
        Assert.True(Program.AttachmentScriptsAllowed(ParcelFlags.AllowOtherScripts, Owner, ParcelGroup, Me, UUID.Zero, false).allowed);
        Assert.True(Program.AttachmentScriptsAllowed(ParcelFlags.AllowOtherScripts | ParcelFlags.AllowGroupScripts, Owner, ParcelGroup, Me, OtherGroup, false).allowed);
    }

    [Fact] public void Group_only_runs_only_with_the_parcel_group_active()
    {
        Assert.False(Program.AttachmentScriptsAllowed(ParcelFlags.AllowGroupScripts, Owner, ParcelGroup, Me, UUID.Zero, false).allowed);
        Assert.False(Program.AttachmentScriptsAllowed(ParcelFlags.AllowGroupScripts, Owner, ParcelGroup, Me, OtherGroup, false).allowed);
        Assert.True(Program.AttachmentScriptsAllowed(ParcelFlags.AllowGroupScripts, Owner, ParcelGroup, Me, ParcelGroup, false).allowed);
        Assert.False(Program.AttachmentScriptsAllowed(ParcelFlags.AllowGroupScripts, Owner, UUID.Zero, Me, UUID.Zero, false).allowed);
    }

    [Fact] public void No_scripts_stops_them_but_the_owner_and_region_flag_win()
    {
        Assert.False(Program.AttachmentScriptsAllowed(ParcelFlags.None, Owner, ParcelGroup, Me, ParcelGroup, false).allowed);
        Assert.True(Program.AttachmentScriptsAllowed(ParcelFlags.None, Me, ParcelGroup, Me, UUID.Zero, false).allowed);
        Assert.False(Program.AttachmentScriptsAllowed(ParcelFlags.AllowOtherScripts, Me, ParcelGroup, Me, UUID.Zero, true).allowed);
    }

    [Fact] public void Welcome_area_gate_says_it_plainly()
    {
        var g = Program.CollarScriptGateFrom("Welcome Area", ParcelFlags.AllowGroupScripts | ParcelFlags.AllowFly, Owner, ParcelGroup, Me, UUID.Zero, false, DateTime.Now);
        Assert.True(g.Off);
        Assert.StartsWith("skipped Stealth: scripts are off for me on this parcel (Welcome Area, scripts=group", g.SkipLine);
        Assert.True(Program.CollarSkippedForScripts(g.SkipLine));
        // the logout report: plain, not a WARNING, and the login marker keeps the collar as it is
        var (ok, line) = LoginVeil.CollarLogoutReport(g.SkipLine, true);
        Assert.False(ok);
        Assert.DoesNotContain("WARNING", line);
        Assert.Contains("scripts are off for me", line);
        Assert.Equal("keep", LoginVeil.CollarMarker(false, g.SkipLine));
    }

    [Fact] public void Unknown_parcel_keeps_the_old_behaviour()
    {
        var g = Program.CollarScriptGateFrom(null, null, UUID.Zero, UUID.Zero, Me, UUID.Zero, false, DateTime.Now);
        Assert.Null(g.Allowed);
        Assert.False(g.Off);
        Assert.Contains("unknown", g.Summary);
        // a region with scripts disabled is known even without the parcel
        Assert.True(Program.CollarScriptGateFrom(null, null, UUID.Zero, UUID.Zero, Me, UUID.Zero, true, DateTime.Now).Off);
    }

    [Fact] public void Scripted_parcel_is_not_skipped()
    {
        var g = Program.CollarScriptGateFrom("Peronaut", ParcelFlags.AllowOtherScripts | ParcelFlags.AllowGroupScripts, Owner, ParcelGroup, Me, UUID.Zero, false, DateTime.Now);
        Assert.True(g.Allowed);
        Assert.False(Program.CollarSkippedForScripts("collar now invisible (Stealth on)"));
        Assert.False(Program.CollarSkippedForScripts(null));
        Assert.Contains("scripts run for me", g.Summary);
    }
}
