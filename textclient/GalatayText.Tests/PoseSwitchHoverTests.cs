using System.Collections.Generic;
using Xunit;
using GalatayText;

namespace GalatayText.Tests;

/// <summary>2026-10-31 20:17 David's photo: she switched cutie (kneel, hover 0.087) -> VAMOSITG23 (sit) and floated ~20 cm for the 10 s
/// the visual check took (fixed to -0.029 at 20:17:25). The verified hover for the new pose is now applied at the switch.</summary>
public class PoseSwitchHoverTests
{
    static string Root() { var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
    static readonly Dictionary<string, double> V = new()
    {
        ["human|VAMOSITG23"] = -0.029, ["human|cutie"] = 0.087,
        ["kind|human|sit"] = -0.029, ["kind|human|kneel"] = 0.087,
    };

    [Fact] public void Kneel_to_sit_applies_the_sits_verified_hover_at_once()
        => Assert.Equal(-0.029, Hover.EarlyPoseHover("human|VAMOSITG23", "sit", V, 0.087));

    [Fact] public void Unverified_pose_starts_from_its_kinds_last_verified_hover()
        => Assert.Equal(-0.029, Hover.EarlyPoseHover("human|VAMOSITG11", "sit", V, 0.087));

    [Fact] public void Nothing_when_already_in_force_or_never_verified()
    {
        Assert.Null(Hover.EarlyPoseHover("human|VAMOSITG23", "sit", V, -0.028));
        Assert.Null(Hover.EarlyPoseHover("human|stand", "stand", V, 0.032));
        Assert.Null(Hover.EarlyPoseHover("android|VAMOSITG23", "sit", V, 0.0));   // another form's values never cross over
    }

    [Fact] public void The_check_log_no_longer_claims_a_pose_bias()
    {
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/PoseHover.cs"));
        Assert.DoesNotContain("bias calibrated against David's viewer\" :", src);
        Assert.Contains("Firestorm placement, no per-pose bias", src);
    }
}
