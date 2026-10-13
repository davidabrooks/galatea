using GalatayText;
using Xunit;

namespace GalatayText.Tests;

public class PhotoHdArgsTests
{
    [Theory]
    [InlineData(new[] { "hd" }, "view", null, "")]
    [InlineData(new[] { "hd", "view" }, "view", null, "")]
    [InlineData(new[] { "hd", "self" }, "self", null, "")]
    [InlineData(new[] { "hd", "self", "landscape" }, "self", null, "landscape")]
    [InlineData(new[] { "hd", "Kay", "Resident", "portrait" }, "at", "Kay Resident", "portrait")]
    [InlineData(new[] { "hd", "portrait" }, "view", null, "portrait")]
    public void Parses(string[] a, string mode, string target, string orient)
    {
        var p = Program.PhotoHdArgs(a);
        Assert.NotNull(p);
        Assert.Equal((mode, target, orient), p.Value);
    }

    [Fact]
    public void RejectsWithoutHd()
    {
        Assert.Null(Program.PhotoHdArgs(new string[0]));
        Assert.Null(Program.PhotoHdArgs(new[] { "self" }));
    }
}
