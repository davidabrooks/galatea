using System;
using System.Collections.Generic;
using System.Linq;
using LibreMetaverse;
using GalatayText;
using Xunit;
namespace GalatayText.Tests;

public class BootMatchTests
{
    static readonly UUID Chill = new("a2591928-d005-3af2-9b02-a04c9e5f93e7");
    static readonly UUID Shorts = new("38d5548f-9033-3385-8a03-45e948fe6855");
    static List<Program.HudOption> Boots() => Enumerable.Range(1, 16).Select(i => new Program.HudOption(i, (uint)i, $"B{i}")).ToList();
    static bool Dark(string l) => Program.BootIsDark(Program.BootRgb[l.Split(' ')[0]]);

    [Fact]
    public void Light_top_gives_light_boots()
    {
        foreach (var top in new[] { "C25 snow", "C9 sky", "C29 sakura", "C19 ivory" })
        {
            var o = Program.BootOptionsForTop(Boots(), new[] { (Chill, top) }, out var why);
            Assert.InRange(o.Count, 2, 3); Assert.DoesNotContain(o, x => Dark(x.Label));
        }
        Assert.Contains("B13", Program.BootOptionsForTop(Boots(), new[] { (Chill, "C9 sky") }, out _).Select(x => x.Label));
        Assert.Contains("B8", Program.BootOptionsForTop(Boots(), new[] { (Chill, "P13") }, out _).Select(x => x.Label)); // measured heather grey
    }

    [Fact]
    public void Dark_top_allows_dark_boots()
    {
        var o = Program.BootOptionsForTop(Boots(), new[] { (Chill, "C1 jet") }, out _);
        Assert.Contains(o, x => Dark(x.Label));
    }

    [Fact]
    public void Bottoms_colour_is_ignored()
    {
        var a = Program.BootOptionsForTop(Boots(), new[] { (Shorts, "C23 midnight"), (Chill, "C25 snow") }, out _).Select(x => x.Label);
        var b = Program.BootOptionsForTop(Boots(), new[] { (Shorts, "C1 calm"), (Chill, "C25 snow") }, out _).Select(x => x.Label);
        Assert.Equal(a, b);
        var only = Program.BootOptionsForTop(Boots(), new[] { (Shorts, "C23 midnight") }, out var why);
        Assert.Contains("unknown", why); Assert.DoesNotContain(only, x => Dark(x.Label));
    }
}

public class KissDetectTests
{
    const string Line = "★ 𝐏𝐫𝐞𝐭𝐭𝐲 𝐋𝐢𝐚𝐫𝐬 ★ David Nightingale 𝐤𝐢𝐬𝐬𝐞𝐬 Galatea Nightingale's 𝐥𝐢𝐩𝐬. ";
    [Fact]
    public void Parses_real_kisser_line_and_only_David_on_me_counts()
    {
        var k = Program.ParseKissLine(Line);
        Assert.Equal(("David Nightingale", "Galatea Nightingale", "lips"), k);
        var now = DateTime.Now;
        Assert.True(Program.IsKissOnMe(k, "Galatea Nightingale", "David Nightingale", now, DateTime.MinValue));
        Assert.False(Program.IsKissOnMe(k, "Galatea Nightingale", "David Nightingale", now, now.AddSeconds(-5)));
        var mine = Program.ParseKissLine("★ 𝐏𝐫𝐞𝐭𝐭𝐲 𝐋𝐢𝐚𝐫𝐬 ★ Galatea Nightingale 𝐤𝐢𝐬𝐬𝐞𝐬 David Nightingale's 𝐥𝐢𝐩𝐬.");
        Assert.False(Program.IsKissOnMe(mine, "Galatea Nightingale", "David Nightingale", now, DateTime.MinValue));
        Assert.Null(Program.ParseKissLine("hello there"));
        Assert.True(Program.CanKissBack(now, now.AddMinutes(-11)));
        Assert.False(Program.CanKissBack(now, now.AddMinutes(-3)));
    }
}

public class BedroomChangeTests
{
    [Fact]
    public void Outfit_change_at_Peronaut_walks_to_bedroom_unless_forced()
    {
        Assert.True(Program.NeedsBedroomWalk("Peronaut", new Vector3(228.4f, 72.5f, 29f), false));
        Assert.False(Program.NeedsBedroomWalk("Peronaut", new Vector3(219.9f, 72.7f, 29f), false));
        Assert.False(Program.NeedsBedroomWalk("Peronaut", new Vector3(228.4f, 72.5f, 29f), true));
        Assert.False(Program.NeedsBedroomWalk("Naberrie", new Vector3(10, 10, 20), false));
    }
}
