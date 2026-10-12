using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

public class ColorSetTests
{
    [Fact]
    public void Parses_and_rejects()
    {
        var (a, rgb, err) = Program.ParseColorArgs("local:7 all 170 200 230");
        Assert.Null(err); Assert.Equal(7u, a!.LocalId); Assert.Equal(new byte[] { 170, 200, 230 }, rgb);
        Assert.NotNull(Program.ParseColorArgs("7 all 170 200").err);
        Assert.NotNull(Program.ParseColorArgs("7 all 170 200 256").err);
        Assert.NotNull(Program.ParseColorArgs("7 9 1 2 3").err);
    }

    [Fact]
    public void Changes_only_rgb_and_keeps_alpha_glow_fullbright()
    {
        var te = new Primitive.TextureEntry(new UUID("707f6ebc-b89d-a1fb-a062-0012c3df5d64"));
        te.DefaultTexture.RGBA = new Color4(0.01f, 0.85f, 0.99f, 0.94f); te.DefaultTexture.Fullbright = true; te.DefaultTexture.Glow = 0.1f;
        var o = Program.ApplyColor(te, 2, null, new byte[] { 170, 200, 230 });
        Assert.Equal("0:170,200,230,a0.94 1:170,200,230,a0.94", Program.FormatColor(o, 2));
        Assert.True(o.GetFace(1).Fullbright); Assert.Equal(0.1f, o.GetFace(0).Glow, 2);
        Assert.Equal(0.85f, te.GetFace(0).RGBA.G, 2);
    }
}
