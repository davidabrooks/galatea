using GalatayText;
using LibreMetaverse;
using Xunit;

public class FeetAutoTests
{
    const string Boots = "810c862e-8d2d-3d28-82a5-19e9fd216ce9";
    static readonly string Json = "{\"default\":\"f1\",\"footwear\":[{\"item\":\"" + Boots + "\",\"name\":\"Moca\",\"feet\":\"f3\"},{\"item\":\"x\",\"feet\":\"f3\"},{\"item\":\"" + Guid.NewGuid() + "\",\"feet\":\"high\"}]}";

    [Fact] public void ParsesOnlyValidEntries()
    {
        var r = Program.ParseFeetRules(Json);
        Assert.Single(r.Footwear); Assert.Equal("f1", r.Default);
        Assert.Equal(new UUID("961d2d4b-4948-3bac-ab88-0a7ba3b99302"), r.Hud);
    }

    [Fact] public void BadJsonFallsBackToFlat() => Assert.Equal("f1", Program.ParseFeetRules("not json").Default);

    [Fact] public void HeelWornGivesF3_RemovedGivesF1()
    {
        var r = Program.ParseFeetRules(Json);
        Assert.Equal("f3", Program.FeetWanted(new[] { UUID.Random(), new UUID(Boots) }, r).feet);
        Assert.Equal("f1", Program.FeetWanted(new[] { UUID.Random() }, r).feet);
    }

    [Theory]
    [InlineData("f3", "f3", "f1", true)]   // boots on, stable -> press f3
    [InlineData("f3", "f1", "f1", false)]  // first poll after change: wait one more
    [InlineData("f1", "f1", "f1", false)]  // already flat
    [InlineData("f1", "f1", null, true)]   // unknown state -> press once
    public void PressRule(string w, string prev, string last, bool press) => Assert.Equal(press, Program.FeetShouldPress(w, prev, last));
}
