using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-11-03 David: 'wander sit &lt;seat&gt;' sends her to a chosen seat the full wander way.</summary>
public class WanderSitTests
{
    static string Repo(params string[] p)
    {
        var a = Path.GetFullPath(Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(p).ToArray()));
        return File.Exists(a) ? a : Path.Combine(new[] { "/workspace/galatea-sl-repo/textclient" }.Concat(p).ToArray());
    }
    static List<Program.HomeSeatInfo> Seats() => Program.ParseHomeSeats(File.ReadAllText(Repo("routes", "_seats-peronaut-home.json")));

    [Fact]
    public void Matches_by_place_uuid_and_name()
    {
        var s = Seats();
        var tub = Assert.Single(Program.MatchHomeSeatQuery(s, "tub"));
        Assert.Equal("undress", tub.Special); Assert.True(tub.Adult); Assert.NotNull(tub.ChangeSpot);
        Assert.Equal(tub.Id, Assert.Single(Program.MatchHomeSeatQuery(s, tub.Id.ToString())).Id);
        Assert.Equal(tub.Id, Assert.Single(Program.MatchHomeSeatQuery(s, "CLAWFOOT")).Id);
        Assert.Equal("toilet", Assert.Single(Program.MatchHomeSeatQuery(s, "toilet")).Special);
        Assert.Empty(Program.MatchHomeSeatQuery(s, "nonexistent thing"));
        Assert.Empty(Program.MatchHomeSeatQuery(s, ""));
    }

    [Fact]
    public void Choose_group_place_ok_excluded_and_ambiguous_refused()
    {
        var s = Seats();
        var (c, e) = Program.WanderSitChoose(Program.MatchHomeSeatQuery(s, "chairs"), "chairs", new Random(1));
        Assert.Null(e); Assert.Equal("chairs", c.Place);
        var (d, de) = Program.WanderSitChoose(Program.MatchHomeSeatQuery(s, "desk"), "desk", new Random(1));
        Assert.Null(d); Assert.Contains("excluded", de);
        var (b, be) = Program.WanderSitChoose(Program.MatchHomeSeatQuery(s, "Nutmeg"), "Nutmeg", new Random(1));
        Assert.Null(b); Assert.Contains("different seats", be);
    }

    [Fact]
    public void Adult_seat_needs_David_near_and_nobody_else()
    {
        var tub = Program.MatchHomeSeatQuery(Seats(), "tub")[0];
        var sofa = Program.MatchHomeSeatQuery(Seats(), "sofa")[0];
        Assert.Contains("David is not near", Program.WanderSitAdultRefusal(tub, false, 0));
        Assert.Contains("other avatar", Program.WanderSitAdultRefusal(tub, true, 1));
        Assert.Null(Program.WanderSitAdultRefusal(tub, true, 0));
        Assert.Null(Program.WanderSitAdultRefusal(sofa, false, 3));
    }

    [Fact]
    public void Visit_reuses_the_picker_visit_function_not_a_copy()
    {
        var src = File.ReadAllText(Repo("galatay-text", "WanderSit.cs"));
        Assert.Contains("HomeRandomSit(g, ct, force: cand)", src);
        foreach (var dup in new[] { "UndressForSeat", "GoToChangeSpot", "ToiletUndress", "SeatPose(", "Exec(\"sit" })
            Assert.DoesNotContain(dup, src);
        var w = File.ReadAllText(Repo("galatay-text", "Wander.cs"));
        Assert.Contains("case \"sit\": case \"go\": return await WanderSitCmd(", w);
        // the forced seat still goes through the special branch (undress at the change spot) inside HomeRandomSit
        var body = w[w.IndexOf("static async Task<bool> HomeRandomSit(")..];
        Assert.True(body.IndexOf("info?.Special == \"undress\"") < body.IndexOf("Exec(\"sit \" + c.p.ID)"));
    }
}
