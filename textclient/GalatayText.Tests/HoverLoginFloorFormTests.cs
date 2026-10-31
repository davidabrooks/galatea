using GalatayText;
using Xunit;

// 2026-10-31 14:34 login (#372 live): (1) the hover check read +3.004 m against floor z 25.04 while she stood on z 28.03: the floor prim was not in the
// export yet; (2) the stand check gave up after 6 tries on "form not known yet" though every attachment was on by +15 s: unnamed worn roots were never asked for.
public class HoverLoginFloorFormTests
{
    [Fact] public void UnnamedWornRoots_AreAsked_OncePerInterval()
    {
        var now = new DateTime(2026, 10, 31, 14, 34, 10);
        var worn = new[] { (1u, "Hair"), (2u, (string)null), (3u, "") };
        Assert.Equal(new uint[] { 2, 3 }, Hover.UnnamedWornToAsk(worn, now, new Dictionary<uint, DateTime>()));
        var asked = new Dictionary<uint, DateTime> { [2] = now.AddSeconds(-2), [3] = now.AddSeconds(-9) };
        Assert.Equal(new uint[] { 3 }, Hover.UnnamedWornToAsk(worn, now, asked));
        Assert.Empty(Hover.UnnamedWornToAsk(new[] { (1u, "Hair") }, now, asked));
    }

    [Fact] public void FloorMissingRender_IsNoReading_AndDoesNotStartTheCooldown()
    {
        var text = "GT: hover check: no reading: the floor under her is not in the export yet (cast hit z 25.043, +3.004 m below her lowest point; scene still loading)";
        Assert.True(Hover.HoverFloorMissing(text));
        Assert.Null(Hover.HoverCheckGap(text));
        Assert.False(Hover.HoverFloorMissing("GT: hover check gap +0.003 m (hover -0.086, size 1.572, floor 28.033)"));
        Assert.False(Hover.HoverRunCounts("floor not loaded yet"));
    }
}
