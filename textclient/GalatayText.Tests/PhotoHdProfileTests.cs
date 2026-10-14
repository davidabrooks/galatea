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
        Assert.Equal(128f, Program.PhotoHdRadius(null, 12)); Assert.Equal(96f, Program.PhotoHdRadius(null, 6));
        Assert.Equal(256f, Program.PhotoHdRadius("ultra", 15)); Assert.Equal(128f, Program.PhotoHdRadius("ultra", 11));
    }

    [Fact]
    public void Particles_exported_only_when_running()
    {
        Assert.True(Program.ExportParticles(2f, 2)); Assert.False(Program.ExportParticles(0f, 2)); Assert.False(Program.ExportParticles(2f, 0));
    }
}
