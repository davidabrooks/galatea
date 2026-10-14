using GalatayText;
using Xunit;

public class PhotoHdProfileTests
{
    [Fact]
    public void Profile_word_anywhere()
    {
        var (r, p) = Program.PhotoHdProfile(new[] { "self", "landscape", "ultra" });
        Assert.Equal(new[] { "self", "landscape" }, r); Assert.Equal("ultra", p);
        Assert.Equal("highplus", Program.PhotoHdProfile(new[] { "high+", "self" }).profile);
        Assert.Null(Program.PhotoHdProfile(new[] { "self" }).profile);
        Assert.Equal("?", Program.PhotoHdProfile(new[] { "profile=low" }).profile);
        Assert.Equal(96f, Program.PhotoHdRadius(null)); Assert.Equal(96f, Program.PhotoHdRadius("ultra"));
    }
}
