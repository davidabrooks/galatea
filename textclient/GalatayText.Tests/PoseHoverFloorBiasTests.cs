using GalatayText;
using Xunit;
// 2026-10-29 21:38: VAMOSITG12 on clear floor really floated +0.28 m (David's screenshot) but was rejected as implausible;
// the human form also used android biases, so render and David's viewer disagreed (stand ~5 cm, sit ~13 cm)
public class PoseHoverFloorBiasTests
{
    [Fact] public void GroundSitOnClearFloorAppliesALargeRealGap()
    {
        Assert.True(Hover.PoseGapSane(0.28, "sit", nearFurniture: false));
        Assert.False(Hover.PoseGapSane(0.28, "sit", nearFurniture: true));   // on furniture: step clear instead
        Assert.False(Hover.PoseGapSane(0.28, "stand", nearFurniture: false));
        Assert.False(Hover.PoseGapSane(3.3, "sit", nearFurniture: false));
    }
    [Fact] public void HumanFormHasItsOwnBiasesFromDavidsView()
    {
        Assert.Equal(0.2315, Hover.PoseBiasDefault("stand", android: true), 4);
        Assert.Equal(-0.055, Hover.PoseBiasDefault("sit", android: true), 4);
        Assert.Equal(0.1825, Hover.PoseBiasDefault("stand", android: false), 4);
        Assert.Equal(0.075, Hover.PoseBiasDefault("sit", android: false), 4);
        Assert.Equal(0.1825, Hover.PoseBiasFor(new System.Collections.Generic.Dictionary<string, double>(), false, "stand", "x"), 4);
    }
}
public class McpWanderToolTests
{
    [Fact] public void McpExposesWanderRoutedToTheTextCommand()
    {
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, "../../../../", "galatay-mcp/McpProgram.cs"));
        Assert.Contains("Name = \"wander\"", src);
        Assert.Contains("R(\"wander \" + Clean(action))", src);
    }
}
