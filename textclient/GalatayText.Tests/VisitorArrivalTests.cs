using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-07 visitor_arrival wake at home (pure).</summary>
public class VisitorArrivalTests
{
    static readonly UUID Self = new("11111111-1111-1111-1111-111111111111");
    static readonly UUID David = new("44ce5a36-c1c7-4a68-ac9a-635ddfff6233");
    static readonly UUID V = new("22222222-2222-2222-2222-222222222222");
    static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0);

    [Fact]
    public void New_visitor_at_home_wakes_once_per_day()
    {
        Assert.True(Program.ShouldWakeVisitor(V, Self, David, "Peronaut", false, null, Now));
        Assert.False(Program.ShouldWakeVisitor(V, Self, David, "Peronaut", true, null, Now));
        Assert.False(Program.ShouldWakeVisitor(V, Self, David, "Peronaut", false, Now.AddHours(-23), Now));
        Assert.True(Program.ShouldWakeVisitor(V, Self, David, "Peronaut", false, Now.AddHours(-25), Now));
    }

    [Fact]
    public void Not_David_not_me_not_away_from_home()
    {
        Assert.False(Program.ShouldWakeVisitor(David, Self, David, "Peronaut", false, null, Now));
        Assert.False(Program.ShouldWakeVisitor(Self, Self, David, "Peronaut", false, null, Now));
        Assert.False(Program.ShouldWakeVisitor(V, Self, David, "naberrie", false, null, Now));
    }

    [Fact]
    public void Text_names_visitor_and_avoids_second_hi()
    {
        Assert.Contains("Jane Doe", Program.VisitorArrivalText("Jane Doe", 5, false));
        Assert.Contains("do NOT say hi again", Program.VisitorArrivalText("Jane Doe", null, true));
        Assert.Contains("visitor_arrival", GalatayMcp.Webhook.UrgentKinds);
    }
}
