using System.Collections.Generic; using System.Linq; using LibreMetaverse; using Xunit; using GalatayText;

/// 2026-10-19 (David): tied alphas follow their garment off; clothing tattoos are removable, skin/makeup/body ones are not.
public class AlphaFollowTests
{
    static readonly UUID F1 = UUID.Random(), F2 = UUID.Random();
    static Program.AlphaCand C(string n, UUID f) => new(UUID.Random(), n, f);

    [Fact]
    public void TiedBySameFolderOrName_BodyAlphaKept()
    {
        var shorts = C("TETRA - Geena ripped shorts", F1);
        var a1 = C("Geena alpha (legs)", F1);                 // same folder
        var a2 = C("TETRA - Geena ripped shorts Alpha", F2);  // name convention
        var body = C("LeL_EvoX(alpha)", F2);                  // head alpha, no tie
        var other = C("Valentine Dress Alpha", F2);
        var hits = Program.AlphasTiedTo(new[] { shorts }, new[] { a1, a2, body, other }, new HashSet<UUID>());
        Assert.Equal(new[] { a1.Item, a2.Item }, hits.Select(h => h.alpha.Item));
    }

    [Fact]
    public void ProtectedNeverRemoved()
    {
        var top = C("Silk top", F1); var a = C("Silk top alpha", F1);
        Assert.Empty(Program.AlphasTiedTo(new[] { top }, new[] { a }, new HashSet<UUID> { a.Item }));
    }

    [Theory]
    [InlineData("Lace panties tattoo", "", true)] [InlineData("Bra - pink", "", true)]
    [InlineData("Freckles tattoo", "", false)] [InlineData("VELOUR: BODY NECKFIX", "", false)]
    [InlineData("Lipstick red", "Outfit - Bikini", false)] [InlineData("Pink set", "Underwear", true)]
    [InlineData("Eyebrows dark", "", false)]
    public void ClothingLayerClassifier(string name, string folder, bool clothing) => Assert.Equal(clothing, Program.IsClothingLayer(name, folder));
}
