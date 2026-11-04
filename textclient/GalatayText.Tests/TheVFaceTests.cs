using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-11-03 David: Pure Pleasure face on an adult pose, The V STOP when it ends; adult to adult never re-dresses, adult to non-adult re-dresses (AdultExitTests).</summary>
public class TheVFaceTests
{
    [Fact]
    public void Starts_on_an_adult_pose_once_the_naked_flow_is_done()
    {
        Assert.Equal("start", Program.TheVFaceAction(false, true, true, false, 0));
        Assert.Null(Program.TheVFaceAction(false, true, true, true, 0));     // still undressing / taking the pose
        Assert.Null(Program.TheVFaceAction(false, true, false, false, 99));  // PG pose
        Assert.Null(Program.TheVFaceAction(false, false, false, false, 99)); // standing
    }

    [Fact]
    public void Stops_on_stand_and_on_a_settled_switch_to_a_non_adult_pose()
    {
        Assert.Equal("stop", Program.TheVFaceAction(true, false, false, false, 0));   // stood up / left: at once
        Assert.Null(Program.TheVFaceAction(true, true, true, false, 0));              // adult -> adult: keeps playing
        Assert.Null(Program.TheVFaceAction(true, true, false, false, 2));             // menu walk BACKs through non-adult menus
        Assert.Equal("stop", Program.TheVFaceAction(true, true, false, false, Program.TheVFaceStopAfterS));
    }

    [Fact]
    public void Map_file_parses_and_unmapped_buttons_stay_null()
    {
        var m = Program.ParseTheVFaceMap("""{"pleasure":{"prim":"face_menu_1_2","face":3,"open":[{"prim":"sidebar","face":1}]},"stop":{"prim":"sidebar_1","face":4}}""");
        Assert.Equal(new Program.TheVButton("face_menu_1_2", 3), m.Pleasure);
        Assert.Equal(new Program.TheVButton("sidebar_1", 4), m.Stop);
        Assert.Equal(new Program.TheVButton("sidebar", 1), Assert.Single(m.Open));
        var e = Program.ParseTheVFaceMap("{}"); Assert.Null(e.Pleasure); Assert.Null(e.Stop);
        Assert.Null(Program.ParseTheVFaceMap("not json").Pleasure);
    }

    [Fact]
    public void Adult_to_adult_switch_on_the_seat_never_redresses()
    {
        // 14:48:44 log: mid-sit change walked BACK to the root menu; the tick saw "no longer sexual" and dressed her seated
        Assert.False(Program.NakedRedressDue("TETRA Chill T-Shirt shorts", false, true, false, 93, false).due);
        Assert.False(Program.NakedRedressDue("TETRA Chill T-Shirt shorts", false, true, true, 93, false).due);
        Assert.True(Program.NakedRedressDue("TETRA Chill T-Shirt shorts", false, false, false, 93, false).due);
    }

    [Fact]
    public void Face_restarts_on_every_adult_pose_change()
    {
        Assert.Equal("restart", Program.TheVFaceRestart(true, true, true, false, "Solo* > Solo Adult* > Backhand", "Solo* > Solo Adult* > Pleasure"));
        Assert.Null(Program.TheVFaceRestart(true, true, true, false, "Solo* > Solo Adult* > Pleasure", "Solo* > Solo Adult* > Pleasure")); // same pose: once
        Assert.Null(Program.TheVFaceRestart(false, true, true, false, "", "Solo* > Solo Adult* > Pleasure"));  // not on yet: that is a start
        Assert.Null(Program.TheVFaceRestart(true, true, false, false, "a", "Solo* > Sit"));                      // non-adult: that is a stop
        Assert.Null(Program.TheVFaceRestart(true, true, true, true, "a", "b"));                                  // flow still busy
        Assert.Null(Program.TheVFaceRestart(true, false, true, false, "a", "b"));                                // stood up
    }

    [Fact]
    public void No_stand_bypasses_the_face_stop()
    {
        var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../galatay-text");
        foreach (var f in System.IO.Directory.GetFiles(dir, "*.cs"))
        {
            if (f.EndsWith("TheVFace.cs")) continue;
            Assert.DoesNotContain("client.Self.Stand()", System.IO.File.ReadAllText(f));
        }
    }
}
