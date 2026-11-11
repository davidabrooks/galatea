using System.Collections.Generic;
using Xunit;
using GalatayText;

namespace GalatayText.Tests;

/// <summary>2026-11-11 11:02 (David watched): android|VAMOSITG25 visual check posted -0.351 from a +0.288 m renderer gap
/// and "confirmed" contact on a nearly black close-up, while his viewer already had her sunk into the floor.
/// A stored / previously accepted value wins; an implausible verified entry (the -0.351 that got written) is not re-applied.</summary>
public class VerifiedPoseHoverGuardTests
{
    static string Root() { var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }

    static readonly Dictionary<string, double> Legacy = new() { ["android|VAMOSITG25"] = 0.022, ["android|VAMOSITG22"] = -0.1, ["bias|android|sit"] = 0.175 };
    static readonly Dictionary<string, double> Poisoned = new()
    {
        ["android|VAMOSITG25"] = -0.35100001096725464,
        ["kind|android|sit"] = -0.35100001096725464,
        ["human|VAMOSITG23"] = -0.124,
        ["kind|human|sit"] = 0.054,
    };

    [Fact]
    public void Todays_minus_351_is_not_a_plausible_sit_hover()
    {
        Assert.False(Hover.VerifiedHoverPlausible("sit", -0.351));
        Assert.True(Hover.VerifiedHoverPlausible("sit", 0.022));
        Assert.True(Hover.VerifiedHoverPlausible("sit", -0.132));
        Assert.True(Hover.VerifiedHoverPlausible("stand", -0.063));
        Assert.False(Hover.VerifiedHoverPlausible("sit", -0.21));
    }

    [Fact]
    public void Legacy_pose_hover_drops_bias_keys()
    {
        var v = Hover.LegacyPoseHoverValues(Legacy);
        Assert.Equal(0.022, v["android|VAMOSITG25"]);
        Assert.False(v.ContainsKey("bias|android|sit"));
    }

    [Fact]
    public void Trusted_value_skips_the_poisoned_verified_entry_and_uses_legacy()
    {
        Assert.Null(Hover.TrustedPoseHover("android|VAMOSITG25", "sit", Poisoned));
        Assert.Equal(0.022, Hover.TrustedPoseHover("android|VAMOSITG25", "sit", Poisoned, Legacy));
        Assert.Equal(-0.124, Hover.TrustedPoseHover("human|VAMOSITG23", "sit", Poisoned));
    }

    [Fact]
    public void Early_hover_does_not_reapply_minus_351()
    {
        Assert.Null(Hover.EarlyPoseHover("android|VAMOSITG25", "sit", Poisoned, -0.063));
        Assert.Equal(0.022, Hover.EarlyPoseHover("android|VAMOSITG25", "sit", Poisoned, -0.063, Legacy));
        Assert.Null(Hover.EarlyPoseHover("android|VAMOSITG25", "sit", Poisoned, 0.022, Legacy));
    }

    [Fact]
    public void Visual_check_must_not_apply_todays_false_fix()
    {
        var why = Hover.VerifiedPoseHoverBlock("android|VAMOSITG25", "sit", -0.351, new Dictionary<string, double>(), Legacy);
        Assert.NotNull(why);
        Assert.Contains("nothing applied", why);
        Assert.Contains("0.022", why);
        Assert.Null(Hover.VerifiedPoseHoverBlock("android|VAMOSITG25", "sit", 0.04, new Dictionary<string, double>(), Legacy));
        // no stored value: an implausible proposed hover is still refused
        var none = Hover.VerifiedPoseHoverBlock("android|VAMOSITG99", "sit", -0.351, new Dictionary<string, double>());
        Assert.NotNull(none);
        Assert.Contains("implausible", none);
        Assert.Null(Hover.VerifiedPoseHoverBlock("android|VAMOSITG99", "sit", -0.08, new Dictionary<string, double>()));
    }

    [Fact]
    public void The_check_calls_the_guard_before_posting_and_does_not_save_a_blocked_value()
    {
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/PoseHover.cs"));
        var fixAt = src.IndexOf("await h.PostHover(t, $\"visual hover fix:");
        Assert.True(fixAt > 0);
        var before = src[..fixAt];
        Assert.Contains("VerifiedPoseHoverBlock(key, kind, t, VerifiedPoseHover, LoadLegacyPoseHover())", before);
        Assert.Contains("if (Program.HoverCheckSkippedForIdleSit(why))", src);
    }
}
