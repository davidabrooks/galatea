using GalatayText;
using LibreMetaverse;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-07 The V Play HUD pubic hair buttons: words -> faces of 'vagina_look_menu_2' (pure).</summary>
public class TheVPubesTests
{
    [Theory]
    [InlineData("blond strip", "3,4")]
    [InlineData("landing strip", "4")]
    [InlineData("blonde landing strip", "3,4")]
    [InlineData("strip blond", "3,4")]
    [InlineData("shaved", "0")]
    [InlineData("ginger bush", "5,7")]
    [InlineData("brown", "2")]
    [InlineData("trimmed", "6")]
    public void Plan_faces(string words, string want)
    {
        var (faces, err) = Program.TheVPubesPlan(words.Split(' '));
        Assert.Null(err);
        Assert.Equal(want, string.Join(",", faces));
    }

    [Theory]
    [InlineData("")]
    [InlineData("pink")]
    [InlineData("shaved strip")]
    [InlineData("black brown")]
    [InlineData("strip bush")]
    public void Bad_words_are_usage(string words) => Assert.StartsWith("usage", Program.TheVPubesPlan(words.Split(' ')).err);

    [Fact]
    public void Labels()
    {
        Assert.Equal("blond strip", Program.TheVPubesLabel(new UUID("5c4db955-e569-2caf-251d-0392af150fab"), 1f));
        Assert.StartsWith("shaved", Program.TheVPubesLabel(new UUID("5c4db955-e569-2caf-251d-0392af150fab"), 0f));
        Assert.StartsWith("unknown texture", Program.TheVPubesLabel(UUID.Random(), 1f));
    }

    [Theory]
    [InlineData("", "none")]
    [InlineData("1", "ball")]
    [InlineData("2", "hoop (ring)")]
    [InlineData("3", "bars")]
    public void Pierce_labels(string counts, string want) =>
        Assert.Equal(want, Program.TheVPierceLabel(counts.Split(',', System.StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)));

    [Fact]
    public void Pierce_faces_hoop_is_7_none_is_5()
    {
        Assert.Equal(7, Program.TheVPierceFace["hoop"]);
        Assert.Equal(7, Program.TheVPierceFace["ring"]);
        Assert.Equal(5, Program.TheVPierceFace["none"]);
    }

    [Fact]
    public void Attach_verify_resends_only_missing_and_reports_still_missing()
    {
        UUID a = UUID.Random(), b = UUID.Random(), c = UUID.Random();
        var (resend, missing) = Program.AttachVerifyPlan(new[] { a, b, c, a }, new HashSet<UUID> { a }, new HashSet<UUID> { a, b });
        Assert.Equal(new[] { b, c }, resend);
        Assert.Equal(new[] { c }, missing);
        var (none, _) = Program.AttachVerifyPlan(new[] { a }, new HashSet<UUID> { a }, null);
        Assert.Empty(none);
    }

    [Fact]
    public void Pubes_face_is_7_on_the_skin_patch_prim_only()
    {
        var te = new Primitive.TextureEntry(new UUID("0b81f6ab-dbbd-797f-2d91-fc1ad935983c"));
        te.CreateFace(0).TextureID = new UUID("8dc72b73-e833-77e9-4fe9-1dc52fdd7a8c");
        te.CreateFace(1).TextureID = new UUID("8dc72b73-e833-77e9-4fe9-1dc52fdd7a8c");
        var f = Program.TheVPubesFaceOf(te);
        Assert.NotNull(f);
        Assert.Equal("brown strip", Program.TheVPubesLabel(f.TextureID, f.RGBA.A));
        Assert.Null(Program.TheVPubesFaceOf(new Primitive.TextureEntry(new UUID("506f3252-907b-38cd-7c26-1883691770a7"))));
    }

    [Fact]
    public void Detach_stops_only_that_objects_anims()
    {
        UUID obj = UUID.Random(), other = UUID.Random(), a1 = UUID.Random(), a2 = UUID.Random(), a3 = UUID.Random();
        var anims = new Dictionary<UUID, (int seq, UUID src)> { [a1] = (1, obj), [a2] = (2, other), [a3] = (3, obj) };
        Assert.Equal(new HashSet<UUID> { a1, a3 }, Program.AnimsFromObject(anims, obj).ToHashSet());
        Assert.Empty(Program.AnimsFromObject(anims, UUID.Zero));
    }
}
