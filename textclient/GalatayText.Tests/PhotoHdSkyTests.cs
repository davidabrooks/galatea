using GalatayText;
using Xunit;

namespace GalatayText.Tests;

// 2026-10-13 (David): photo hd follows the shared environment by default; a named preset overrides it
public class PhotoHdSkyTests
{
    [Theory]
    [InlineData(new[] { "hd" }, new[] { "hd" }, null)]
    [InlineData(new[] { "hd", "sunset" }, new[] { "hd" }, "sunset")]
    [InlineData(new[] { "hd", "self", "Midnight" }, new[] { "hd", "self" }, "midnight")]
    [InlineData(new[] { "hd", "self", "sunrise", "landscape" }, new[] { "hd", "self", "landscape" }, "sunrise")]
    [InlineData(new[] { "hd", "view", "preset=foggy", "portrait" }, new[] { "hd", "view", "portrait" }, "foggy")]
    [InlineData(new[] { "hd", "sky=shared" }, new[] { "hd" }, null)]
    [InlineData(new[] { "hd", "shared" }, new[] { "hd" }, null)]
    [InlineData(new[] { "hd", "preset=mars" }, new[] { "hd" }, "?")]
    [InlineData(new[] { "hd", "Sunset", "Resident", "portrait" }, new[] { "hd", "Sunset", "Resident", "portrait" }, null)]
    public void PullsTheSky(string[] a, string[] rest, string sky)
    {
        var (r, s) = Program.PhotoHdSky(a);
        Assert.Equal(rest, r);
        Assert.Equal(sky, s);
    }

    [Fact]
    public void PresetThenTargetStillParses()
    {
        var (r, s) = Program.PhotoHdSky(new[] { "hd", "Kay", "Resident", "midday" });
        Assert.Equal("midday", s);
        Assert.Equal(("at", "Kay Resident", ""), Program.PhotoHdArgs(r).Value);
    }
}
