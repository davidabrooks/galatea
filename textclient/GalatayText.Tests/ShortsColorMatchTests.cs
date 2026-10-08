using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-08 David: shorts versions of the three tops (+ bikini top with shorts / jeans); when the top gets a random
/// color, the Chill Shorts HUD picks a denim wash that goes with it.</summary>
public class ShortsColorMatchTests
{
    static readonly UUID Shorts = new("596631d6-a5fd-3d98-aa43-70e0996ded81"), ShortsHud = new("bff83228-ba99-320c-ad9b-7f2abe1832f8");
    static readonly UUID TeeHud = new("a2591928-d005-3af2-9b02-a04c9e5f93e7"), BethHud = new("6899891e-79d1-3a31-93ab-fac14a3bd75e");
    static readonly UUID ArtisHud = new("cb0dc6c4-545c-3be6-8351-e049094315a7"), BikiniHud = new("67c881aa-5adf-3f68-9c5c-0adb67e8e484");

    static List<Program.ClothingHudSpec> Specs()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", "_clothing-huds.json"));
        return Program.ParseClothingHudSpecs(File.ReadAllText(File.Exists(repo) ? repo : "/workspace/galatea-sl-repo/textclient/routes/_clothing-huds.json"));
    }

    // the shorts HUD's real button set: denim D1-35 plus belt/buckle/lighter/rivet buttons that must never be picked
    static List<Program.HudOption> ShortsOpts(Program.ClothingHudSpec spec)
    {
        var prims = new List<(int, uint, string, string)> { (1, 1, "<HUD> Chill Shorts", ""), (2, 2, "[DETACH]", ""), (3, 3, "[DELSCRIPTS]", "") };
        uint n = 10;
        foreach (var d in Enumerable.Range(1, 35)) prims.Add(((int)n, n++, "[TEXTURE]", "D" + d));
        foreach (var k in new[] { "B", "M", "R" }) foreach (var d in Enumerable.Range(1, 20)) prims.Add(((int)n, n++, "[TEXTURE]", k + d));
        foreach (var d in Enumerable.Range(1, 10)) prims.Add(((int)n, n++, "[TEXTURE]", "L" + d));
        return Program.HudOptionsFor(prims, spec);
    }

    static HashSet<string> Codes(IEnumerable<Program.HudOption> o) => o.Select(x => x.Label.Split(' ')[0]).ToHashSet();

    [Fact]
    public void Shorts_spec_maps_the_shorts_to_their_HUD_denim_only()
    {
        var sh = Specs().Single(s => s.Clothing.Contains(Shorts));
        Assert.Equal(ShortsHud, sh.Hud);
        Assert.True(sh.Matches);
        var opts = ShortsOpts(sh);
        Assert.Equal(35, opts.Count);
        Assert.All(opts, o => Assert.StartsWith("D", o.Label));
        Assert.Contains(opts, o => o.Label == "D17 deep ocean");
        Assert.Single(Specs(), s => s.Matches);   // only the shorts follow another piece
    }

    [Theory]
    [InlineData("a2591928-d005-3af2-9b02-a04c9e5f93e7", "C1 jet", new[] { "D1", "D6", "D15" }, new[] { "D7", "D25" })]          // black tee: black denim or light wash
    [InlineData("a2591928-d005-3af2-9b02-a04c9e5f93e7", "C7 navy", new[] { "D15", "D25" }, new[] { "D7", "D27", "D1" })]         // navy tee: never navy-on-navy
    [InlineData("a2591928-d005-3af2-9b02-a04c9e5f93e7", "C28 peony", new[] { "D14", "D25" }, new[] { "D1", "D7" })]              // pink: light washes
    [InlineData("a2591928-d005-3af2-9b02-a04c9e5f93e7", "C12 cherry", new[] { "D11", "D18" }, new[] { "D1", "D25" })]            // red: classic blue
    [InlineData("cb0dc6c4-545c-3be6-8351-e049094315a7", "cotton 28 dark navy", new[] { "D15" }, new[] { "D27" })]                // ARTi'S names
    [InlineData("cb0dc6c4-545c-3be6-8351-e049094315a7", "cotton 21 forest green", new[] { "D14", "D25" }, new[] { "D1" })]
    [InlineData("6899891e-79d1-3a31-93ab-fac14a3bd75e", "tx 2m black heart", new[] { "D7", "D11", "D1" }, new[] { "D25", "D15" })] // white Beth top, whatever the heart
    [InlineData("67c881aa-5adf-3f68-9c5c-0adb67e8e484", "W21 wet burgundy", new[] { "D11" }, new[] { "D25" })]                    // bikini top (wet button)
    public void Shorts_wash_goes_with_the_top(string hud, string topLabel, string[] allowed, string[] never)
    {
        var sh = Specs().Single(s => s.Clothing.Contains(Shorts));
        var got = Codes(Program.HudMatchOptions(ShortsOpts(sh), sh, new List<(UUID, string)> { (new UUID(hud), topLabel) }, out var why));
        Assert.NotNull(why);
        foreach (var a in allowed) Assert.Contains(a, got);
        foreach (var n in never) Assert.DoesNotContain(n, got);
        for (int k = 0; k < 40; k++) Assert.Contains(Program.PickHudOption(Program.HudMatchOptions(ShortsOpts(sh), sh, new List<(UUID, string)> { (new UUID(hud), topLabel) }, out _), null, new Random(k)).Label.Split(' ')[0], got);
    }

    [Fact]
    public void Print_tie_or_unknown_pick_uses_the_default_set_and_no_spec_means_all()
    {
        var sh = Specs().Single(s => s.Clothing.Contains(Shorts));
        var all = ShortsOpts(sh);
        var p = Program.HudMatchOptions(all, sh, new List<(UUID, string)> { (TeeHud, "P5") }, out var why);
        Assert.Equal("default set", why);
        Assert.True(Codes(p).SetEquals(sh.MatchDefault));
        Assert.NotEmpty(Program.HudMatchOptions(all, sh, new List<(UUID, string)>(), out var w2));
        Assert.Equal("default set", w2);
        Assert.Equal(all.Count, Program.HudMatchOptions(all, Specs().Single(s => s.Hud == TeeHud), new List<(UUID, string)> { (BethHud, "tx 1m") }, out var w3).Count);
        Assert.Null(w3);
        // wanted buttons missing on the HUD -> every option (never an empty pick)
        var odd = new Program.ClothingHudSpec(ShortsHud, "", new() { Shorts }, null, null, new(), new() { new Program.HudMatchRule(UUID.Zero, "jet", new() { "Z9" }) });
        Assert.Equal(all.Count, Program.HudMatchOptions(all, odd, new List<(UUID, string)> { (TeeHud, "C1 jet") }, out _).Count);
    }

    [Fact]
    public void Tee_and_bikini_buttons_carry_color_names()
    {
        var specs = Specs();
        var tee = specs.Single(s => s.Hud == TeeHud);
        var o = Program.HudOptionsFor(new List<(int, uint, string, string)> { (1, 1, "<HUD> Chill T-Shirt", ""), (2, 2, "[TEXTURE]", "C27"), (3, 3, "[TEXTURE]", "P3") }, tee);
        Assert.Contains(o, x => x.Label == "C27 cream");
        Assert.Contains(o, x => x.Label == "P3");
        var bik = specs.Single(s => s.Hud == BikiniHud);
        var b = Program.HudOptionsFor(new List<(int, uint, string, string)> { (1, 1, "<HUD> Spicy Bikini", ""), (2, 2, "[TEXTURE]", "D21"), (3, 3, "[TEXTURE]", "T9"), (4, 4, "[TEXTURE]", "D33") }, bik);
        Assert.Equal(new[] { "D21 burgundy", "D33 print 3", "T9" }, b.Select(x => x.Label).OrderBy(x => x));
        Assert.NotNull(specs.Single(s => s.Hud == ArtisHud).Grid);
    }
}
