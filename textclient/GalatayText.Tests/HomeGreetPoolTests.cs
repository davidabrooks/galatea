using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-08 home wander greetings must never mention the Buddha Center or the gardens.</summary>
public class HomeGreetPoolTests
{
    [Fact]
    public void Home_greeting_templates_never_mention_Buddha_Center_or_gardens()
    {
        Assert.NotEmpty(Program.HomeGreetTemplates);
        foreach (var t in Program.HomeGreetTemplates)
        {
            Assert.DoesNotContain("Buddha Center", t, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gardens", t, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NextGreeting_at_home_without_robe_uses_only_home_pool()
    {
        var now = new DateTime(2026, 10, 8, 15, 0, 0);
        int idx = -1;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 200; i++)
            seen.Add(Program.NextGreeting(now, "Maya", ref idx, atDeerPark: false, robe: false, place: "home"));

        Assert.Equal(Program.HomeGreetTemplates.Length, seen.Count);
        foreach (var line in seen)
        {
            Assert.DoesNotContain("Buddha Center", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gardens", line, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Maya", line, StringComparison.Ordinal);
        }
        Assert.True(Program.IsHomeGreetPlace("home"));
        Assert.True(Program.IsHomeGreetPlace("Home"));
        Assert.False(Program.IsHomeGreetPlace("The Buddha Center"));
        Assert.False(Program.IsHomeGreetPlace("default"));
    }

    [Fact]
    public void NextGreeting_at_home_with_robe_still_uses_home_pool()
    {
        var now = new DateTime(2026, 10, 8, 15, 0, 0);
        int idx = -1;
        for (int i = 0; i < 50; i++)
        {
            var line = Program.NextGreeting(now, "Maya", ref idx, atDeerPark: false, robe: true, place: "home");
            Assert.DoesNotContain("Buddha Center", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gardens", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Namaste", line, StringComparison.OrdinalIgnoreCase);
        }
    }
}
