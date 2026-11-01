using System.Collections.Generic;
using Xunit;
using GalatayText;

namespace GalatayText.Tests;

/// <summary>2026-10-31 18:26 (David: "you're floating a little"): Moca boots f3, verified 0.012; after a relog the 3 samples read -0.062/-0.067/-0.059
/// (the export's agent z was 0.080 m low), the check set and saved 0.074. A fix that far from the verified value is refused and nothing is saved.</summary>
public class SavedHoverDeviationTests
{
    static readonly Dictionary<string, double> Saved = new() { ["f3"] = 0.012, ["f1"] = -0.08 };

    [Fact]
    public void The_1826_false_fix_is_refused()
    {
        var why = Program.SavedHoverDeviationBlock("f3", Saved, 0.012 + 0.062);
        Assert.NotNull(why);
        Assert.Contains("nothing saved", why);
    }

    [Fact]
    public void A_small_floor_difference_is_still_applied()
        => Assert.Null(Program.SavedHoverDeviationBlock("f3", Saved, 0.030));

    [Fact]
    public void No_saved_value_lets_the_render_decide()
        => Assert.Null(Program.SavedHoverDeviationBlock("f5", Saved, 0.2));
}
