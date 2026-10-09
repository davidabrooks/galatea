using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-09 David: no group tag. 'group activate' + routes/_active-group.json (pure parts only).</summary>
public class ActiveGroupTests
{
    static readonly UUID Sun = new("394073e3-0000-0000-0000-000000000001");
    static readonly UUID Other = new("11111111-2222-3333-4444-555555555555");
    static readonly Dictionary<UUID, string> Groups = new() { [Sun] = "Sunrise Suites", [Other] = "Sunset Club" };

    [Theory]
    [InlineData("none")]
    [InlineData("NONE")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void None_is_zero(string want)
    {
        var (id, _, err) = Program.ResolveActiveGroup(want, Groups);
        Assert.Null(err);
        Assert.Equal(UUID.Zero, id);
    }

    [Theory]
    [InlineData("sunrise suites")]
    [InlineData("'Sunrise Suites'")]
    [InlineData("Sunrise")]
    [InlineData("394073e3-0000-0000-0000-000000000001")]
    public void Resolves_by_name_prefix_or_uuid(string want)
    {
        var (id, name, err) = Program.ResolveActiveGroup(want, Groups);
        Assert.Null(err);
        Assert.Equal(Sun, id);
        Assert.Equal("Sunrise Suites", name);
    }

    [Theory]
    [InlineData("Sun")]                                     // ambiguous prefix
    [InlineData("Fancy Club")]                              // not a member
    [InlineData("99999999-9999-9999-9999-999999999999")]    // not a member
    [InlineData("")]
    public void Refuses_unknown_or_ambiguous(string want) => Assert.NotNull(Program.ResolveActiveGroup(want, Groups).error);

    [Fact]
    public void Pref_json_round_trips()
    {
        Assert.Equal("none", Program.ParseActiveGroupPref(Program.ActiveGroupPrefJson(UUID.Zero, null)));
        Assert.Equal(Sun.ToString(), Program.ParseActiveGroupPref(Program.ActiveGroupPrefJson(Sun, "Sunrise Suites")));
        Assert.Equal("Sunset Club", Program.ParseActiveGroupPref("{\"group\":\"Sunset Club\"}"));
        Assert.Null(Program.ParseActiveGroupPref("not json"));
        Assert.Null(Program.ParseActiveGroupPref("{}"));
    }

    [Fact]
    public void Repo_routes_file_is_none()
    {
        var f = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", "_active-group.json"));
        Assert.Equal("none", Program.ParseActiveGroupPref(File.ReadAllText(f)));
    }
}
