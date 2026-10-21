using Xunit;
using GalatayText;

public class MaitreyaShowAllTests
{
    [Fact] public void PicksHighestTabBar() => Assert.Equal(2, Program.AlphaTabIndex(new[] { ("tabs", -1f), ("show", 1.4f), ("tabs", 1.99f), ("tabs", 0.99f) }));
    [Fact] public void NoTabs() => Assert.Equal(-1, Program.AlphaTabIndex(new[] { ("show", 1f) }));
    [Fact] public void HiddenButtons() => Assert.Equal(new[] { "hide:11", "hide:22" }, Program.HiddenSegmentButtons(new (string, System.Collections.Generic.IEnumerable<float>)[] {
        ("hide:11", new[] { 0f, 0f }), ("hide:14", new[] { 1f }), ("hide:22", new[] { 1f, 0f }), ("show", new[] { 0f }) }));
}
