using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

public class TextureSetTests
{
    static readonly UUID Dial = new("5031d749-c529-bf4f-9adf-a6be07ed05e5");
    static readonly UUID Style2 = new("b6136879-5a2d-9688-be3e-77d3fb4f9865");

    [Fact]
    public void Parses_and_rejects()
    {
        var (a, tex, err) = Program.ParseTextureSetArgs("local:9 1 5031d749-c529-bf4f-9adf-a6be07ed05e5");
        Assert.Null(err); Assert.Equal(9u, a!.LocalId); Assert.Equal(new System.Collections.Generic.List<int> { 1 }, a.Faces); Assert.Equal(Dial, tex);
        Assert.NotNull(Program.ParseTextureSetArgs("9 1 nope").err);
        Assert.NotNull(Program.ParseTextureSetArgs("9 1 00000000-0000-0000-0000-000000000000").err);
        Assert.NotNull(Program.ParseTextureSetArgs("9 1").err);
    }

    [Fact]
    public void Changes_only_the_texture_of_chosen_faces()
    {
        var te = new Primitive.TextureEntry(Style2);
        te.DefaultTexture.Fullbright = true; 
        var f0 = te.CreateFace(0); f0.TextureID = Dial;
        var o = Program.ApplyTexture(te, 2, new[] { 1 }, Dial);
        Assert.Equal(Dial, o.GetFace(0).TextureID); Assert.Equal(Dial, o.GetFace(1).TextureID);
        Assert.True(o.GetFace(1).Fullbright);
        Assert.Equal(Style2, te.GetFace(1).TextureID);
    }
}
