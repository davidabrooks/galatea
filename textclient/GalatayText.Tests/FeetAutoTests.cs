using GalatayText;
using LibreMetaverse;
using Xunit;

public class FeetAutoTests
{
    const string Boots = "810c862e-8d2d-3d28-82a5-19e9fd216ce9";
    static readonly string Json = "{\"default\":\"f1\",\"footwear\":[{\"item\":\"" + Boots + "\",\"name\":\"Moca\",\"feet\":\"f3\"},{\"item\":\"x\",\"feet\":\"f3\"},{\"item\":\"" + Guid.NewGuid() + "\",\"feet\":\"high\"}]}";

    [Fact] public void ParsesOnlyValidEntries()
    {
        var r = FeetAuto.ParseFeetRules(Json);
        Assert.Single(r.Footwear); Assert.Equal("f1", r.Default);
        Assert.Equal(new UUID("961d2d4b-4948-3bac-ab88-0a7ba3b99302"), r.Hud);
    }

    [Fact] public void BadJsonFallsBackToFlat() => Assert.Equal("f1", FeetAuto.ParseFeetRules("not json").Default);

    [Fact] public void HeelWornGivesF3_RemovedGivesF1()
    {
        var r = FeetAuto.ParseFeetRules(Json);
        Assert.Equal("f3", FeetAuto.FeetWanted(new[] { UUID.Random(), new UUID(Boots) }, r).feet);
        Assert.Equal("f1", FeetAuto.FeetWanted(new[] { UUID.Random() }, r).feet);
    }

    [Theory]
    [InlineData("f3", "f3", "f1", true)]   // boots on, stable -> press f3
    [InlineData("f3", "f1", "f1", false)]  // first poll after change: wait one more
    [InlineData("f1", "f1", "f1", false)]  // already flat
    [InlineData("f1", "f1", null, true)]   // unknown state -> press once
    public void PressRule(string w, string prev, string last, bool press) => Assert.Equal(press, FeetAuto.FeetShouldPress(w, prev, last));

    static (UUID, string, int) Body(UUID id, int kids, string name = "Maitreya Mesh Body - Lara X") => (id, name, kids);

    [Fact] public void BodySignature_ChangesOnNewItemOrPrims_IgnoresOtherAttachments()
    {
        var a = UUID.Random(); var b = UUID.Random();
        var s1 = FeetAuto.FeetBodySignature(new[] { Body(a, 40), (UUID.Random(), "Moca boots", 3) });
        Assert.Equal(s1, FeetAuto.FeetBodySignature(new[] { (UUID.Random(), "Other hat", 1), Body(a, 40) })); // other attachments do not matter
        Assert.NotEqual(s1, FeetAuto.FeetBodySignature(new[] { Body(b, 40) }));                               // re-attached = new attachment id
        Assert.NotEqual(s1, FeetAuto.FeetBodySignature(new[] { Body(a, 12) }));                               // prims changed after the poll
        Assert.NotEqual(s1, FeetAuto.FeetBodySignature(new (UUID, string, int)[0]));                          // body removed
        Assert.Equal("", FeetAuto.FeetBodySignature(new[] { (UUID.Random(), "MISAKI - Kay", 5) }));           // android body is not the LaraX body
    }

    [Fact] public void BodyChanged_NotOnFirstPoll()
    {
        Assert.False(FeetAuto.FeetBodyChanged(null, "x")); Assert.False(FeetAuto.FeetBodyChanged("x", "x")); Assert.True(FeetAuto.FeetBodyChanged("x", "y"));
    }

    [Fact] public void BodyChange_WithUnchangedWantedPose_PressesAgain()
    {
        // 22:13 bug: f3 pressed and recorded, then the body was re-attached (feet reset to flat); wanted stays f3
        Assert.False(FeetAuto.FeetShouldPress("f3", "f3", "f3"));                       // old rule: never again
        var (press, last) = FeetAuto.FeetDecide("f3", "f3", "f3", bodyChanged: true);
        Assert.True(press); Assert.Null(last);
        Assert.False(FeetAuto.FeetDecide("f3", "f3", "f3", bodyChanged: false).press);  // no body change: still quiet
    }

    [Fact] public void BodyChange_StillNeedsTwoStablePolls()
    {
        Assert.False(FeetAuto.FeetDecide("f3", "f1", "f3", true).press);                // wanted just changed: wait a poll
        Assert.True(FeetAuto.FeetDecide("f3", "f3", null, false).press);                // next poll, state was cleared
        Assert.True(FeetAuto.FeetDecide("f1", "f1", "f3", true).press);                 // flat default also re-pressed after a body change
    }
}
