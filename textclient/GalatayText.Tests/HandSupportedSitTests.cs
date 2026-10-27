using System;
using System.IO;
using System.Linq;
using GalatayText;
using Xunit;
// 2026-10-27: VAMOSITG13 rests on both hands behind her with the hips on the floor. The hover check aligned the hips (-0.012) and the hands sank into the floor (looks bad);
// David's eye preferred +0.06 (hips hover slightly). Hand-supported sits aim a fixed lift above the hip contact; nothing is saved per pose.
public class HandSupportedSitTests
{
    static string Root() { var d = new DirectoryInfo(System.AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
    static string Src(string f) => File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text", f));

    [Fact] public void OnlyTheListedGroundSits_GetTheLift()
    {
        Assert.Equal(0.07, Program.HandSupportLift("sit", "VAMOSITG13"));
        Assert.Equal(0.07, Program.HandSupportLift("sit", " vamositg13 "));       // keyed by the AO pose name, case-insensitive
        Assert.Equal(0.0, Program.HandSupportLift("sit", "VAMOSITG23"));
        Assert.Equal(0.0, Program.HandSupportLift("sit", "sit"));                  // name unknown
        Assert.Equal(0.0, Program.HandSupportLift("sit", null));
        Assert.Equal(0.0, Program.HandSupportLift("stand", "VAMOSITG13"));         // only a ground sit
        Assert.Equal(0.0, Program.HandSupportLift("seat", "VAMOSITG13"));
        Assert.Contains("VAMOSITG13", Program.HandSupportedSits);
    }
    [Fact] public void VAMOSITG13_LandsAboutPlus006_AfterTheHipContactResultOfMinus0012()
    {
        double lift = Program.HandSupportLift("sit", "VAMOSITG13");
        // the check at hover -0.012 where the render says the hips touch (gap 0): the target is +0.058 (+0.07 above the hip-contact result)
        Assert.Equal(0.058, Program.PoseHoverFromGap(-0.012, Program.HandFirstGap(0.0, lift)), 3);
        // from the old default: render at 0.0 reading +0.012 (hips 1.2 cm up) -> the same +0.058
        Assert.Equal(0.058, Program.PoseHoverFromGap(0.0, Program.HandFirstGap(0.012, lift)), 3);
        // from where David's eye put it (+0.03, hips 4.2 cm above contact): still +0.058, whatever the start
        Assert.Equal(0.058, Program.PoseHoverFromGap(0.03, Program.HandFirstGap(0.042, lift)), 3);
    }
    [Fact] public void ARecheckAtTheLiftedHover_IsConfirmed_NoSecondChange()
    {
        double lift = 0.07;
        // at +0.058 the render reads the hips 0.07 above the floor: not a fix, contact for this pose
        Assert.True(Program.PoseGapConfirmed(Program.HandFirstGap(0.07, lift)));
        Assert.True(Program.PoseGapConfirmed(Program.HandFirstGap(0.075, lift)));
        Assert.False(Program.PoseGapConfirmed(Program.HandFirstGap(0.0, lift)));     // hips on the floor = hands sunk: it IS a fix
        Assert.False(Program.PoseGapConfirmed(Program.HandFirstGap(0.15, lift)));    // too high: lowered towards +0.06
        Assert.Equal(0.058 - 0.08, Program.PoseHoverFromGap(0.058, Program.HandFirstGap(0.15, lift)), 3);
    }
    [Fact] public void EveryOtherPose_IsUnchanged()
    {
        Assert.Equal(0.0, Program.HandFirstGap(0.0, Program.HandSupportLift("sit", "VAMOSITG26")));
        Assert.Equal(-0.034, Program.HandFirstGap(-0.034, Program.HandSupportLift("stand", "stand")));
        Assert.Equal(Program.PoseHoverFromGap(0.1, -0.05), Program.PoseHoverFromGap(0.1, Program.HandFirstGap(-0.05, 0.0)));
    }
    [Fact] public void Wiring_NoSavedValue_OneRenderPerCheck_TheLiftOnlyShiftsTheGap()
    {
        var ph = Src("PoseHover.cs"); var chk = ph[ph.IndexOf("static async Task<string> PoseHoverCheck(")..];
        Assert.Contains("HandSupportLift(kind, pose), eg = HandFirstGap(gap, lift)", chk);
        Assert.Contains("PoseGapConfirmed(eg)", chk); Assert.Contains("PoseHoverFromGap(hov, eg)", chk); Assert.Contains("HoverFixNeedsConfirm(eg)", chk);
        Assert.Contains("PoseGapConfirmed(HandFirstGap(gg, lift))", chk);
        Assert.Equal(1, Program.HoverCheckRenderCount(false)); Assert.Equal(2, Program.HoverCheckRenderCount(true));   // same render counts as before
        Assert.Contains("renders {renders}", chk);                                        // still reported
        var lines = ph.Split('\n').Where(l => l.Contains("HandSupport") && !l.TrimStart().StartsWith("//")).ToList();
        Assert.DoesNotContain(lines, l => l.Contains("File.Write") || l.Contains("PoseHoverFile") || l.Contains("Json"));   // nothing is saved per pose
    }
}
