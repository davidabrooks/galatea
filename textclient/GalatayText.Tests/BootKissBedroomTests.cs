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

public class JoinDavidBeachTests
{
    static Vector3 Beach() { for (int x = 0; x < 256; x += 4) for (int y = 0; y < 256; y += 4) { var v = new Vector3(x, y, 21); if (Program.OutfitZoneFor("Peronaut", v, false) == "beach") return v; } throw new Exception("no beach"); }
    static readonly Vector3 House = new(219.9f, 72.7f, 29f);

    [Fact] public void Human_joining_David_on_beach_puts_bikini_on_first() => Assert.True(Program.JoinDavidNeedsBikini(false, false, "Peronaut", Beach()));
    [Fact] public void Android_stays_as_is() => Assert.False(Program.JoinDavidNeedsBikini(true, false, "Peronaut", Beach()));
    [Fact] public void Not_on_beach_or_already_bikini_or_elsewhere_does_nothing()
    {
        Assert.False(Program.JoinDavidNeedsBikini(false, false, "Peronaut", House));
        Assert.False(Program.JoinDavidNeedsBikini(false, true, "Peronaut", Beach()));
        Assert.False(Program.JoinDavidNeedsBikini(false, false, "Naberrie", Beach()));
        Assert.False(Program.JoinDavidNeedsBikini(false, false, "Peronaut", null));
    }
    [Fact] public void Names_David() { Assert.True(Program.NamesDavid("David")); Assert.True(Program.NamesDavid("David Nightingale")); Assert.False(Program.NamesDavid("Davide Rossi")); }
}

public class DutchieDeskTests
{
    static readonly List<string> Menu = new() { "Cuddle/Kiss*", "[SWAP]", "On desk*", "Please him*", "Satisfy him*", "On chair*", "Work for him*", "Serve him*", "Amuse him*", "Dom*", "Master (m)" };

    [Fact]
    public void Desk_never_offers_swap_or_dom_role()
    {
        var f = Program.SeatPoseFilter(Program.DutchieDesk, Menu);
        Assert.DoesNotContain("[SWAP]", f); Assert.DoesNotContain("Dom*", f); Assert.DoesNotContain("Master (m)", f);
        Assert.Contains("On desk*", f);
    }

    [Fact]
    public void Desk_partner_poses_stay_skipped_solo_even_when_sexual_allowed()
    {
        var f = Program.SeatPoseFilter(Program.DutchieDesk, Menu);
        for (int i = 0; i < 60; i++)
        {
            var p = Program.PickPoseButton(f, null, new Random(i), allowSexual: true).pick;
            Assert.True(p == null || !p.Contains("him") && !p.Contains("Kiss") && p != "[SWAP]", p);
        }
        // the sexual rule still decides: android / others present -> no sexual menus at all
        Assert.False(Program.SexualPosesAllowed(true, "Peronaut", new[] { new UUID("44ce5a36-c1c7-4a68-ac9a-635ddfff6233") }, new UUID("44ce5a36-c1c7-4a68-ac9a-635ddfff6233")));
    }
}

public class LoginStartTests
{
    static readonly DateTime Now = new(2026, 10, 12, 22, 55, 0, DateTimeKind.Utc);
    [Fact] public void Short_absence_logs_in_at_last_location() => Assert.Equal("last", Program.ChooseLoginStart("home", Now.AddMinutes(-3), Now));
    [Fact] public void Long_absence_uses_home_bedroom() => Assert.Equal("home", Program.ChooseLoginStart("home", Now.AddMinutes(-45), Now));
    [Fact] public void No_heartbeat_or_future_stamp_uses_configured()
    {
        Assert.Equal("home", Program.ChooseLoginStart("home", null, Now));
        Assert.Equal("home", Program.ChooseLoginStart("home", Now.AddMinutes(5), Now));
        Assert.Equal("home", Program.ChooseLoginStart("home", Now.AddMinutes(-30), Now));
    }
}
