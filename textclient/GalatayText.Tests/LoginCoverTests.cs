using System;
using System.Collections.Generic;
using System.Linq;
using GalatayText;
using LibreMetaverse;
using Xunit;

// The real 2026-10-28 16:11 login: the sim restored 13 of 14 COF objects; the Lelutka head (shares point #12 with the Real Kisser) was missing and only added at +20 s.
public class LoginCoverTests
{
    static UUID U(int n) => new UUID($"00000000-0000-0000-0000-{n:D12}");
    static readonly (int id, string name)[] Cof =
    {
        (1, "DOUX - Oasis Hairstyle [M/Long]"), (2, ":: No 9 :: Premium Collar"), (3, "/ HEAD / lel evox / AVALON 4.0"), (4, "TETRA - Geena ripped shorts - LaraX (low waist)"),
        (5, "/ GUMS / TEETH / lel evox 4.0"), (6, "Maitreya Mesh Body - LaraX V1.1"), (7, "TETRA - Moca Ankle Boots (Suede) - Maitreya Mid"), (8, "-Pretty Liars- Real Kisser V7.0 [FEMALE HEAD]"),
        (9, "Maitreya Mesh Body - LaraX Petite Add-on V1.1"), (10, "TETRA - Chill T-Shirt - LaraX Petite"), (11, ".Heol Star Earrings Gold"), (12, "/ EYES / lel evox 4.0"),
        (13, "VISTA ANIMATIONS *HUD 6.3*MARTHA STS BENTO AO-V1.7"), (14, "Maitreya Mesh Body - LaraX HUD V1.1"),
    };
    static List<LoginCover.Item> All() => Cof.Select(c => new LoginCover.Item(U(c.id), c.name)).ToList();

    [Fact] public void TheLiveLogin_HeadMissing_IsPlannedFirst()
    {
        var worn = Cof.Where(c => c.id != 3).Select(c => U(c.id)).ToHashSet();   // everything but the Lelutka head was restored
        var plan = LoginCover.Plan(All(), worn, Program.OutfitSendRank);
        Assert.Equal(new[] { "/ HEAD / lel evox / AVALON 4.0" }, plan.Select(p => p.Name));
    }
    [Fact] public void ColdLogin_NothingWorn_HeadFirstThenBodyThenTheRestOfTheCoverSet_NoClothesNoHuds()
    {
        var plan = LoginCover.Plan(All(), new HashSet<UUID>(), Program.OutfitSendRank).Select(p => p.Name).ToList();
        Assert.Equal("/ HEAD / lel evox / AVALON 4.0", plan[0]);
        Assert.Equal("Maitreya Mesh Body - LaraX V1.1", plan[1]);
        Assert.Contains("-Pretty Liars- Real Kisser V7.0 [FEMALE HEAD]", plan);
        Assert.Contains("/ EYES / lel evox 4.0", plan); Assert.Contains("/ GUMS / TEETH / lel evox 4.0", plan); Assert.Contains("Maitreya Mesh Body - LaraX Petite Add-on V1.1", plan);
        Assert.DoesNotContain(plan, n => n.Contains("T-Shirt") || n.Contains("shorts") || n.Contains("Boots") || n.Contains("Hairstyle") || n.Contains("Collar") || n.Contains("HUD") || n.Contains("ANIMATIONS") || n.Contains("Earrings"));
    }
    [Fact] public void EverythingWorn_PlansNothing() => Assert.Empty(LoginCover.Plan(All(), Cof.Select(c => U(c.id)).ToHashSet(), Program.OutfitSendRank));
    [Fact] public void KeptOffItemsAndDuplicatesAreSkipped()
    {
        var cof = All(); cof.Add(new LoginCover.Item(U(3), "/ HEAD / lel evox / AVALON 4.0"));   // the same item twice (link + target)
        var plan = LoginCover.Plan(cof, new HashSet<UUID>(), Program.OutfitSendRank, (id, n) => n.Contains("Kisser"));
        Assert.Single(plan, p => p.Id == U(3)); Assert.DoesNotContain(plan, p => p.Name.Contains("Kisser"));
    }
    [Fact] public void ZeroIdsAndNullAreIgnored()
    {
        Assert.Empty(LoginCover.Plan(null, new HashSet<UUID>(), Program.OutfitSendRank));
        Assert.Empty(LoginCover.Plan(new[] { new LoginCover.Item(UUID.Zero, "/ HEAD / x") }, new HashSet<UUID>(), Program.OutfitSendRank));
    }
    [Fact] public void RestoreSettles_WhenTheCountHoldsStill_OrAtTheLatestAfterTheCap()
    {
        var q = LoginCover.Quiet; var max = LoginCover.MaxWait;
        Assert.False(LoginCover.RestoreSettled(0, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), q, max));   // nothing yet is not "settled"
        Assert.False(LoginCover.RestoreSettled(5, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3), q, max));
        Assert.True(LoginCover.RestoreSettled(13, TimeSpan.FromMilliseconds(1600), TimeSpan.FromSeconds(8), q, max));
        Assert.True(LoginCover.RestoreSettled(0, TimeSpan.Zero, max, q, max));
        Assert.True(LoginCover.Quiet < TimeSpan.FromSeconds(4));   // the library's own settle is 4 s (+ the bake wait); this one is the fast path
    }
    [Fact] public void LoginOrder_SaysHowLongTheBaseFaceShowed()
    {
        const string head = "/ HEAD / lel evox / AVALON 4.0", body = "Maitreya Mesh Body - LaraX V1.1";
        var late = LoginOrder.Findings(new[] { new LoginOrder.Worn(body, 5, null), new LoginOrder.Worn(head, 19, null) });
        Assert.Contains(late, l => l.Contains("real head arrived at +19 s") && l.Contains("base face showed for about 19 s"));
        Assert.Contains(LoginOrder.Findings(new[] { new LoginOrder.Worn(body, 5, null), new LoginOrder.Worn(head, 1, null) }), l => l == "real head arrived at +1 s");
        Assert.Contains(LoginOrder.Findings(new[] { new LoginOrder.Worn(body, 5, null), new LoginOrder.Worn(head, null, null) }), l => l.Contains("base face stayed visible"));
        Assert.Contains(LoginOrder.Findings(new[] { new LoginOrder.Worn(head, 9, null) }), l => l.Contains("base face showed"));
        Assert.DoesNotContain(LoginOrder.Findings(new[] { new LoginOrder.Worn(body, 5, null), new LoginOrder.Worn("-Pretty Liars- Real Kisser V7.0 [FEMALE HEAD]", 1, null) }), l => l.Contains("real head"));
        Assert.True(LoginOrder.IsRealHead(head)); Assert.False(LoginOrder.IsRealHead("-Pretty Liars- Real Kisser V7.0 [FEMALE HEAD]"));
    }
    [Fact] public void EarlyPlan_IsOnlyTheRealHead_NotTheKisserOrTheBody()
    {   // regression 2026-10-29 21:34: the head waited for the restore settle (+6 s) although the sim never restores it
        var cof = new[] { new LoginCover.Item(U(1), "Maitreya Mesh Body - LaraX V1.1"), new LoginCover.Item(U(2), "-Pretty Liars- Real Kisser V7.0 [FEMALE HEAD]"),
                          new LoginCover.Item(U(3), "/ HEAD / lel evox / AVALON 4.0"), new LoginCover.Item(U(4), "/ EYES / lel evox 4.0") };
        var early = LoginCover.EarlyPlan(cof, new HashSet<UUID>(), Program.OutfitSendRank);
        Assert.Equal(new[] { U(3) }, early.Select(i => i.Id).ToArray());
        Assert.Empty(LoginCover.EarlyPlan(cof, new HashSet<UUID> { U(3) }, Program.OutfitSendRank));   // already worn: nothing
    }
}
