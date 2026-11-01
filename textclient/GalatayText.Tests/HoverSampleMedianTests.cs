using Xunit;
using GalatayText;

namespace GalatayText.Tests;

/// <summary>2026-10-31: the same Moca boots on the same floor read -0.25, -0.001, 0.089, 0.091 (David: 0.012 is right). One render of one export
/// drove each fix; now several fresh exports, the median of the agreeing ones, and no fix while they disagree or the feet just settled.</summary>
public class HoverSampleMedianTests
{
    [Fact]
    public void Two_agree_one_outlier_gives_their_median()
    {
        var (m, why) = Hover.HoverSampleMedian(new double?[] { 0.004, 0.010, -0.075 }, 3);
        Assert.Equal(0.007, m!.Value, 3);
        Assert.Contains("outlier", why);
    }

    [Fact]
    public void Readings_that_disagree_change_nothing()
    {
        var (m, _) = Hover.HoverSampleMedian(new double?[] { 0.170, 0.013, -0.169 }, 3);
        Assert.Null(m);
    }

    [Fact]
    public void Missing_readings_are_not_settled()
    {
        Assert.Null(Hover.HoverSampleMedian(new double?[] { 0.01, null, 0.012 }, 3).median);
        Assert.Equal(0.05, Hover.HoverSampleMedian(new double?[] { 0.05 }, 1).median!.Value, 3);   // sit / kneel: one render, unchanged
    }

    [Fact]
    public void Stand_check_waits_after_the_feet_are_verified()
    {
        Assert.NotNull(Hover.StandSettleBlock(2));
        Assert.Null(Hover.StandSettleBlock(12));
    }
}
