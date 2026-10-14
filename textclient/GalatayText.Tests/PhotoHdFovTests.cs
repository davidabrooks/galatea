using GalatayText;
using Xunit;

public class PhotoHdFovTests
{
    [Fact]
    public void FovWordAndEqualsForm()
    {
        var (r, f) = Program.PhotoHdFov(new[] { "self", "fov", "45", "landscape" });
        Assert.Equal(new[] { "self", "landscape" }, r); Assert.Equal("45", f);
        Assert.Equal("70", Program.PhotoHdFov(new[] { "fov=70" }).fov);
        Assert.Null(Program.PhotoHdFov(new[] { "self" }).fov);
        Assert.Equal("?", Program.PhotoHdFov(new[] { "fov", "500" }).fov);
        Assert.Equal("?", Program.PhotoHdFov(new[] { "fov" }).fov);
    }
}
