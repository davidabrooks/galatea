using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-07 per-region wander rules from routes/_wander-rules.json (pure).</summary>
public class WanderRegionRulesTests
{
    static string Json()
    {
        var p = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", "_wander-rules.json"));
        return File.ReadAllText(p);
    }

    [Fact]
    public void Home_sits_near_David_but_not_within_3m()
    {
        var r = Program.ParseWanderRule(Json(), "Peronaut");
        Assert.True(r.Greet);
        Assert.True(Program.SeatAllowedByRule(r, 4f));
        Assert.False(Program.SeatAllowedByRule(r, 2.5f));
    }

    [Fact]
    public void Buddha_Center_is_quiet_10m_and_no_greetings()
    {
        var r = Program.ParseWanderRule(Json(), "naberrie");
        Assert.False(r.Greet);
        Assert.False(Program.SeatAllowedByRule(r, 9.9f));
        Assert.True(Program.SeatAllowedByRule(r, 10f));
    }

    [Fact]
    public void Unknown_region_uses_default_and_empty_json_is_safe()
    {
        Assert.Equal(3f, Program.ParseWanderRule(Json(), "Elsewhere").SeatAvatarM);
        Assert.True(Program.ParseWanderRule("", null).Greet);
    }
}
