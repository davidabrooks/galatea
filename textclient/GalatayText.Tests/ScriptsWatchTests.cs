using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-11-23 David t3491u: after a teleport to Ahern (scripts off) nothing noticed. ScriptsWatch.cs logs one [scripts] line per
// region / parcel / verdict change and puts scripts_allowed in 'status'.
public class ScriptsWatchTests
{
    static readonly UUID Me = new("11111111-1111-1111-1111-111111111111");
    static readonly UUID Owner = new("22222222-2222-2222-2222-222222222222");
    static readonly UUID ParcelGroup = new("33333333-3333-3333-3333-333333333333");

    [Fact] public void Region_and_estate_skip_scripts_beat_everything()
    {
        Assert.False(Program.AttachmentScriptsAllowed(ParcelFlags.AllowOtherScripts, Owner, ParcelGroup, Me, ParcelGroup, true, 200f).allowed);
        var g = Program.CollarScriptGateFrom(null, null, UUID.Zero, UUID.Zero, Me, UUID.Zero, true, DateTime.Now);
        Assert.False(g.Allowed);
    }

    [Fact] public void No_scripts_parcel_stops_attachments_on_the_ground()
    {
        Assert.False(Program.AttachmentScriptsAllowed(ParcelFlags.AllowFly, Owner, ParcelGroup, Me, UUID.Zero, false, 2f).allowed);
        Assert.False(Program.AttachmentScriptsAllowed(ParcelFlags.AllowFly, Owner, ParcelGroup, Me, UUID.Zero, false, null).allowed);   // height unknown: assume ground
        Assert.False(Program.AttachmentScriptsAllowed(ParcelFlags.None, Owner, ParcelGroup, Me, UUID.Zero, false, Program.ScriptsParcelLimitAglM).allowed);
    }

    [Fact] public void Parcel_limit_does_not_apply_high_above_the_ground()
    {
        var (ok, why) = Program.AttachmentScriptsAllowed(ParcelFlags.None, Owner, ParcelGroup, Me, UUID.Zero, false, 300f);
        Assert.True(ok);
        Assert.Contains("300 m above the ground", why);
        var (ok2, why2) = Program.AttachmentScriptsAllowed(ParcelFlags.AllowGroupScripts, Owner, ParcelGroup, Me, UUID.Zero, false, 120f);
        Assert.True(ok2);
        Assert.StartsWith("my active group is not the parcel group, but", why2);
    }

    [Fact] public void Group_and_everyone_flags_and_owner()
    {
        Assert.True(Program.AttachmentScriptsAllowed(ParcelFlags.AllowOtherScripts, Owner, ParcelGroup, Me, UUID.Zero, false, 1f).allowed);
        Assert.True(Program.AttachmentScriptsAllowed(ParcelFlags.AllowGroupScripts, Owner, ParcelGroup, Me, ParcelGroup, false, 1f).allowed);
        Assert.False(Program.AttachmentScriptsAllowed(ParcelFlags.AllowGroupScripts, Owner, ParcelGroup, Me, UUID.Zero, false, 1f).allowed);
        Assert.True(Program.AttachmentScriptsAllowed(ParcelFlags.None, Me, ParcelGroup, Me, UUID.Zero, false, 1f).allowed);
    }

    [Fact] public void Log_line_says_off_plainly_and_names_the_takecontrols_exception()
    {
        var g = Program.CollarScriptGateFrom("Ahern Welcome", ParcelFlags.AllowFly, Owner, ParcelGroup, Me, UUID.Zero, false, DateTime.Now, 1f);
        var line = Program.ScriptsLine("Ahern", g);
        Assert.StartsWith("Ahern parcel 'Ahern Welcome': scripts OFF for my attachments (scripts=no-scripts)", line);
        Assert.Contains("collar/leash and HUD scripts are stopped", line);
        Assert.Contains("took controls", line);
        var on = Program.CollarScriptGateFrom("Home", ParcelFlags.AllowOtherScripts, Owner, ParcelGroup, Me, UUID.Zero, false, DateTime.Now, 1f);
        Assert.Equal("Peronaut parcel 'Home': scripts on (scripts=other)", Program.ScriptsLine("Peronaut", on));
        var unk = Program.CollarScriptGateFrom(null, null, UUID.Zero, UUID.Zero, Me, UUID.Zero, false, DateTime.Now);
        Assert.Contains("unknown", Program.ScriptsLine("Ahern", unk));
    }

    [Fact] public void Logs_only_on_region_parcel_or_verdict_change()
    {
        var g = Program.CollarScriptGateFrom("P", ParcelFlags.None, Owner, ParcelGroup, Me, UUID.Zero, false, DateTime.Now, 1f);
        var st = new Program.ScriptsState(42UL, "Ahern", 7, g);
        Assert.True(Program.ScriptsStateChanged(null, 42UL, 7, false));
        Assert.False(Program.ScriptsStateChanged(st, 42UL, 7, false));   // same place, same verdict: no spam
        Assert.True(Program.ScriptsStateChanged(st, 42UL, 7, true));     // verdict changed (e.g. active group switched)
        Assert.True(Program.ScriptsStateChanged(st, 42UL, 8, false));    // other parcel
        Assert.True(Program.ScriptsStateChanged(st, 43UL, 7, false));    // other region
        var unknown = new Program.ScriptsState(42UL, "Ahern", -1, Program.CollarScriptGateFrom(null, null, UUID.Zero, UUID.Zero, Me, UUID.Zero, false, DateTime.Now));
        Assert.True(Program.ScriptsStateChanged(unknown, 42UL, 7, false)); // data arrived after the "unknown" line
    }

    [Fact] public void Status_value_is_true_false_or_unknown()
    {
        var off = new Program.ScriptsState(42UL, "Ahern", 7, Program.CollarScriptGateFrom("P", ParcelFlags.None, Owner, ParcelGroup, Me, UUID.Zero, false, DateTime.Now, 1f));
        Assert.Equal(false, Program.ScriptsAllowedValue(off, 42UL));
        Assert.Equal("unknown", Program.ScriptsAllowedValue(off, 43UL));   // verdict is for another region
        Assert.Equal("unknown", Program.ScriptsAllowedValue(null, 42UL));
        var on = new Program.ScriptsState(42UL, "Ahern", 7, Program.CollarScriptGateFrom("P", ParcelFlags.AllowOtherScripts, Owner, ParcelGroup, Me, UUID.Zero, false, DateTime.Now, 1f));
        Assert.Equal(true, Program.ScriptsAllowedValue(on, 42UL));
    }
}
