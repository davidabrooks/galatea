using System; using System.IO; using System.Text.RegularExpressions; using GalatayText; using Xunit;

/// 2026-10-15: [anim] start/stop/RESTART lines go to anim-events.log (compact), not textclient.log; anims_at.py replays them, so format must stay.
public class AnimLogGateTests
{
    // the exact replay regex from vision/scene-mesher/anims_at.py
    static readonly Regex Replay = new(@"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) \[anim\] (start|RESTART|stop) ([0-9a-f]{8}-[0-9a-f-]{27})\b");
    const string Id = "9153bff9-b40b-b8a3-848c-be8c51abd893";
    const string AoDesc = "attachment 'VISTA ANIMATIONS *HUD 6.3*MARTHA STS BENTO AO-V1.7' @HUDCenter item fe2c99ba-2abd-370e-a047-1129b8b1db7c";
    const string ObjDesc = "object 'childprim' 884043a9-069d-fa37-aa29-c39eccf080df";

    [Fact]
    public void FullModeIsTheOldLine()
    {
        Assert.Equal($"start {Id} seq 7 from {AoDesc}", Program.AnimLogMsg("start", Id, 7, AoDesc, false));
        Assert.Equal($"stop {Id} (was from {ObjDesc})", Program.AnimLogMsg("stop", Id, 0, ObjDesc, false));
    }

    [Theory]
    [InlineData("start")] [InlineData("RESTART")] [InlineData("stop")]
    public void CompactLinesStillMatchTheReplayParser(string verb)
    {
        foreach (var src in new[] { AoDesc, ObjDesc, "none", "self/sim (default or override state)", "seat 'Bench'", $"object {Id} (not in view)" })
        {
            var m = Replay.Match($"2026-10-15 12:00:00 [anim] {Program.AnimLogMsg(verb, Id, 5, src, true)}");
            Assert.True(m.Success, src); Assert.Equal(verb, m.Groups[2].Value); Assert.Equal(Id, m.Groups[3].Value);
        }
    }

    [Fact]
    public void CompactIsShorterAndKeepsTheUsefulSource()
    {
        Assert.Equal("att @HUDCenter", Program.AnimSrcShort(AoDesc));
        Assert.Equal("object 'childprim'", Program.AnimSrcShort(ObjDesc));
        Assert.Equal("self", Program.AnimSrcShort("self/sim (default or override state)"));
        Assert.Equal("none", Program.AnimSrcShort("none"));
        Assert.Equal("seat 'Bench'", Program.AnimSrcShort("seat 'Bench'"));
        Assert.Equal("object 9153bff9.. (not in view)", Program.AnimSrcShort($"object {Id} (not in view)"));
        Assert.Equal("object 'ABCDEFGHIJKLMNOPQRSTUVWX'", Program.AnimSrcShort("object 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123' 884043a9-069d-fa37-aa29-c39eccf080df"));
        Assert.True(Program.AnimLogMsg("start", Id, 7, AoDesc, true).Length < Program.AnimLogMsg("start", Id, 7, AoDesc, false).Length - 80);
    }

    [Fact]
    public void LinesAreWrittenToTheirOwnFileNotTheMainLog_AndNothingParsesTheTextclientLog()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../galatay-text"));
        var aw = File.ReadAllText(Path.Combine(root, "AttachWatch.cs"));
        Assert.Contains("if (log) AnimLogWrite(AnimLogMsg(", aw);
        Assert.DoesNotContain("if (log) Log(\"anim\"", aw);
        // no C# code reads "[anim]" lines (anims_at.py is the only reader: it replays them, hence the unchanged format)
        foreach (var f in Directory.GetFiles(root, "*.cs"))
            foreach (var l in File.ReadAllLines(f))
            {
                var m = Regex.Match(l, @"(?<![\w\]\)])\[anim\]"); if (!m.Success) continue;
                var cm = l.IndexOf("//", StringComparison.Ordinal);
                Assert.True((cm >= 0 && cm < m.Index) || l.Contains("{DateTime.Now:yyyy-MM-dd HH:mm:ss} [anim] {msg}"), $"{Path.GetFileName(f)} uses \"[anim]\" outside a comment: {l.Trim()}");
            }
    }
}
