using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

public class FullbrightSetTests
{
    [Fact]
    public void Parses_on_off_and_rejects_bad()
    {
        var (a, on, err) = Program.ParseFullbrightArgs("local:5 all off");
        Assert.Null(err); Assert.Equal(5u, a!.LocalId); Assert.Null(a.Faces); Assert.False(on);
        Assert.True(Program.ParseFullbrightArgs("5 1,2 on").on);
        Assert.NotNull(Program.ParseFullbrightArgs("5 all maybe").err);
        Assert.NotNull(Program.ParseFullbrightArgs("5 9 off").err);
    }

    [Fact]
    public void Changes_only_fullbright()
    {
        var te = new Primitive.TextureEntry(new UUID("707f6ebc-b89d-a1fb-a062-0012c3df5d64"));
        te.DefaultTexture.Fullbright = true; te.DefaultTexture.Glow = 0.2f; te.CreateFace(1).Glow = 0f;
        var o = Program.ApplyFullbright(te, 2, null, false);
        Assert.Equal("0:off 1:off", Program.FormatFullbright(o, 2));
        Assert.Equal(0.2f, o.GetFace(0).Glow, 2); Assert.Equal(0f, o.GetFace(1).Glow, 2);
        Assert.True(te.GetFace(0).Fullbright);
    }
}
