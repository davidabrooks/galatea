using LibreMetaverse;
using Xunit;
using GalatayText;

public class AttFacesTests
{
    [Fact] public void ShowsAlphaAndTint()
    {
        var s = Program.FaceSummary(new[] { (0, 1f, 1f, 1f, 0f, UUID.Zero), (1, 0.5f, 1f, 1f, 1f, UUID.Zero) });
        Assert.Contains("f0:a0/", s); Assert.Contains("f1:a1/rgb0.5,1,1", s);
    }
    [Fact] public void PrintsFullTextureId()
    {
        var id = new UUID("cdc3dc19-1111-2222-3333-444455556666");
        var s = Program.FaceSummary(new[] { (3, 1f, 1f, 1f, 1f, id) });
        Assert.Contains("f3:a1/tcdc3dc19-1111-2222-3333-444455556666", s);
    }
    [Fact] public void Empty() => Assert.Equal("(no faces)", Program.FaceSummary(new (int, float, float, float, float, UUID)[0]));
}
