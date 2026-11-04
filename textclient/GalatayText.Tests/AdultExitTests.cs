using GalatayText;
using Xunit;

namespace GalatayText.Tests;

// 2026-11-03 16:33 David: a mid-sit change Vibrator -> Curled up stopped the face but left her naked.
public class AdultExitTests
{
    static string Src(string f) { var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "textclient/galatay-text", f)); }
    [Fact] public void NonAdultNext_WithOutfitPending_Redresses()  => Assert.Equal("redress", Program.AdultExitStep(false, true, true));
    [Fact] public void NonAdultNext_NoOutfitPending_StopsFace()    => Assert.Equal("face-stop", Program.AdultExitStep(false, false, true));
    [Fact] public void AdultNext_KeepsEverything()                 => Assert.Null(Program.AdultExitStep(true, true, true));
    [Fact] public void NothingRunning_DoesNothing()                => Assert.Null(Program.AdultExitStep(false, false, false));
    [Fact] public void BothSeatMenuPaths_RunTheExitStepBeforeTheReply()
    {
        var src = Src("SeatPose.cs");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(src, @"await LeaveAdultPoseBeforeSwitch\([^\n]*\n\s*var t1 = DateTime.Now;\n\s*client.Self.ReplyToScriptDialog").Count);
        var ar = Src("AdultRedress.cs");
        Assert.Contains("await TheVFaceStop(\"re-dress", ar);   // the re-dress stops the face first
    }
}
