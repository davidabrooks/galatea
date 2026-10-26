using System.IO;
using System.Linq;
using GalatayText;
using Xunit;
// 2026-10-26 (David: "when you're sitting, you should be checking your hips, not your feet"): sitting ON FURNITURE, the contact close-up frames the hips
// against the seat (look `feet` view and the manual `hover check`), reports the gap, one render, and never touches the hover.
public class SeatContactTests
{
    static string Root() { var d = new DirectoryInfo(System.AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
    static string Src(string f) => File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text", f));

    [Fact] public void SeatLabel_NamesTheHips_AndGroundStandKneelLabelsAreUnchanged()
    {
        Assert.Equal("hips against the seat", Program.HoverContactLabel("seat"));
        Assert.Equal("feet (soles)", Program.HoverContactLabel("stand")); Assert.Equal("hips and any thighs / heels in contact", Program.HoverContactLabel("sit")); Assert.Equal("lowest contact", Program.HoverContactLabel("unknown"));
    }
    [Fact] public void SeatLookEnv_UsesTheSeatedPlacement_NotTheHoverModel()
    {
        var e = Program.SeatLookEnv();
        Assert.Equal("1", e["GT_SEATED"]); Assert.Equal("1", e["GT_SEAT_CHECK"]); Assert.Equal("hips against the seat", e["GT_HC_POSTURE"]);
        Assert.False(e.ContainsKey("GT_HOVER_CHECK")); Assert.False(e.ContainsKey("GT_HOVER")); Assert.False(e.ContainsKey("GT_HOVER_BIAS"));   // no hover is drawn or fixed on furniture
    }
    [Theory]
    [InlineData("GT: seat contact gap +0.012 m (hips above the seat +, sunk into it -)", 0.012)] [InlineData("seat contact gap -0.034 m (x)", -0.034)] [InlineData("seat contact gap +0.000 m", 0.0)]
    public void SeatContactGap_ParsesTheRenderLine(string line, double expect) => Assert.Equal(expect, Program.SeatContactGap("a.jpg\n" + line + "\nb"));
    [Theory] [InlineData("GT: seat contact gap unknown (seat surface not found)")] [InlineData("GT: hover check gap +0.010 m (hover +0.000)")] [InlineData("")] [InlineData(null)]
    public void SeatContactGap_IsNullWithoutAGap(string text) => Assert.Null(Program.SeatContactGap(text));
    [Fact] public void SeatCheckVerdict_ReportsTheHipsToSeatGap_AndPointsAtTheFurnitureAdjust()
    {
        Assert.StartsWith("hips on the seat", Program.SeatCheckVerdict(0.01)); Assert.StartsWith("hips on the seat", Program.SeatCheckVerdict(-0.03));
        // the #314 live test: -0.038 on a slatted rocking chair was reported as "sunk INTO the seat" while David's eye said it looked good
        Assert.StartsWith("hips on the seat", Program.SeatCheckVerdict(-0.038)); Assert.StartsWith("hips on the seat", Program.SeatCheckVerdict(-0.06)); Assert.StartsWith("hips on the seat", Program.SeatCheckVerdict(0.02));
        var up = Program.SeatCheckVerdict(0.06); Assert.Contains("6 cm ABOVE the seat", up); Assert.Contains("AVsitter Z-", up); Assert.Contains("ABOVE", Program.SeatCheckVerdict(0.03));
        var dn = Program.SeatCheckVerdict(-0.09); Assert.Contains("9 cm sunk INTO the seat", dn); Assert.Contains("AVsitter Z+", dn); Assert.Contains("INTO", Program.SeatCheckVerdict(-0.065));
        Assert.Contains("gap unknown", Program.SeatCheckVerdict(null));
        Assert.DoesNotContain("hover set", up + dn); Assert.DoesNotContain("hover apply", up + dn);   // never tells her to move the hover
    }
    [Fact] public void ManualHoverCheck_OnFurniture_IsOneSeatRender_AndNeverPostsTheHover()
    {
        var ph = Src("PoseHover.cs");
        Assert.Contains("if (why == \"manual\" && client.Self.SittingOn != 0) return await SeatContactCheck();", ph);   // before the stand / sit check loop
        var chk = ph[ph.IndexOf("static async Task<string> SeatContactCheck()")..]; chk = chk[..chk.IndexOf("// the visual check:")];
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(chk, @"await HoverCheckRender\(").Count);       // ONE render
        Assert.Contains("seat: true", chk); Assert.Contains("renders 1", chk); Assert.Contains("hover left alone", chk);
        foreach (var never in new[] { "PostHover(", "AoSit", "StandUp", "idle_pose", "HoverCheckRequest(" }) Assert.DoesNotContain(never, chk);   // no hover change, no AO / pose / seat action, no loop
        // the automatic check still skips furniture (it must not run unasked), and stand / ground-sit checks are the #313 path
        Assert.Equal((false, "skip: sitting on an object (the normal hover is restored there)"), Program.HoverCheckReady("seat", 0f, false, false, false, 99, 99, true));
        Assert.True(Program.HoverCheckReady("stand", 0f, false, false, false, 99, 99, true).go);
    }
    [Fact] public void LookSelf_WhileSeated_GetsTheSeatEnv_AndNotTheHoverModel()
    {
        var lk = Src("Look.cs"); var env = lk[lk.IndexOf("static async Task<Dictionary<string, string>> LookHoverEnv()")..]; env = env[..env.IndexOf("static async Task<string> LookCmd")];
        Assert.True(env.IndexOf("client.Self.SittingOn != 0) return SeatLookEnv();") is > 0 and var at && at < env.IndexOf("HoverCheckEnv(")); // the seated branch comes first
    }
    [Fact] public void SeatCheck_ShowsInTheRenderCountDocs_AndTheStandAndSitPathsKeepTheirRenderCount()
    {
        Assert.Equal(1, Program.HoverCheckRenderCount(false)); Assert.Equal(2, Program.HoverCheckRenderCount(true));
        var rend = Src("PoseHover.cs"); rend = rend[rend.IndexOf("static async Task<(double? gap, string img)> HoverCheckRender(")..]; rend = rend[..rend.IndexOf("internal static bool FormCertain")];
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(rend, @"Process\.Start\(").Count);            // still one blender run per call
    }
}
