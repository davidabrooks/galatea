using System.Collections.Generic;
using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-12 'glow set' parsing + TextureEntry glow edit (pure).</summary>
public class GlowSetTests
{
    [Fact]
    public void Parses_faces_and_value()
    {
        var (a, err) = Program.ParseGlowSetArgs("local:179973331 2,0,0 0.1");
        Assert.Null(err); Assert.Equal(179973331u, a!.LocalId); Assert.Equal(new List<int> { 0, 2 }, a.Faces); Assert.Equal(0.1f, a.Value, 3);
        var (b, _) = Program.ParseGlowSetArgs("42 all 0");
        Assert.Null(b!.Faces); Assert.Equal(0f, b.Value);
    }

    [Theory]
    [InlineData("42 all")] [InlineData("x all 0.1")] [InlineData("42 9 0.1")] [InlineData("42 all 1.5")] [InlineData("42 all -0.1")] [InlineData("42 all abc")]
    public void Rejects_bad_input(string s) => Assert.NotNull(Program.ParseGlowSetArgs(s).err);

    [Fact]
    public void Changes_only_glow_on_chosen_faces()
    {
        var tex = new UUID("707f6ebc-b89d-a1fb-a062-0012c3df5d64");
        var te = new Primitive.TextureEntry(tex);
        te.DefaultTexture.Fullbright = true;
        te.CreateFace(1).Glow = 0.2f;
        var o = Program.ApplyGlow(te, 3, new[] { 0 }, 0.1f);
        Assert.Equal(0.1f, o.GetFace(0).Glow, 2); Assert.Equal(0.2f, o.GetFace(1).Glow, 2); Assert.Equal(0f, o.GetFace(2).Glow, 2);
        Assert.True(o.GetFace(0).Fullbright); Assert.Equal(tex, o.GetFace(0).TextureID);
        Assert.Equal(0.2f, te.GetFace(1).Glow, 2); Assert.Equal(0f, te.GetFace(0).Glow, 2);   // source untouched
        var all = Program.ApplyGlow(te, 3, null, 0f);
        Assert.Equal("0:0.00 1:0.00 2:0.00", Program.FormatGlow(all, 3));
        Assert.True(all.GetFace(2).Fullbright);
    }
}
