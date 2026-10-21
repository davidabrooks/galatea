using LibreMetaverse;
using Xunit;
using GalatayText;

public class OutfitCollarLinkTests
{
    [Fact] public void DetectsCollarLink() => Assert.True(Program.OutfitHasCollarLink(new[] { (false, UUID.Random()), (true, Program.N9CollarItem) }));
    [Fact] public void CollarOriginalIsNotALink() => Assert.False(Program.OutfitHasCollarLink(new[] { (false, Program.N9CollarItem) }));
    [Fact] public void SaveAddsCollarWhenMissing() { Assert.True(Program.PlanNeedsCollar(new[] { UUID.Random() })); Assert.False(Program.PlanNeedsCollar(new[] { Program.N9CollarItem })); }
}

public class RedressNeverNakedTests
{
    [Fact] public void NakedPreStripFallsBackToClothed() => Assert.Equal("TETRA Chill T-Shirt shorts", Program.RedressTarget("Naked", "Naked", "Naked", "TETRA Chill T-Shirt shorts"));
    [Fact] public void PreStripWins() => Assert.Equal("Bikini", Program.RedressTarget("Bikini", "Naked", "Naked", "TETRA"));
    [Fact] public void NothingClothedGivesNull() => Assert.Null(Program.RedressTarget(null, "Naked", "naked v2", null));
    [Fact] public void AndroidSkipped() => Assert.Equal("Robe", Program.RedressTarget(null, "Android 1", null, "Robe"));
}

public class RlvDetachNameAndLinkTests
{
    [Fact] public void MatchesInventoryName()
    {
        var item = UUID.Random();
        var worn = new[] { new Program.RlvWorn(item, UUID.Random(), "The V - Bento by Session Skins & ASA Studios", 28, "The V - Bento for Maitreya LaraX - SCULPTED - V1.61") };
        Assert.Single(Program.RlvDetachPlan(worn, "The V - Bento for Maitreya LaraX - SCULPTED - V1.61", new HashSet<UUID>(), new HashSet<UUID>()));
    }
    [Fact] public void LinkIdResolvesToTarget()
    {
        UUID link = UUID.Random(), item = UUID.Random();
        Assert.Equal(item.ToString(), Program.RlvResolveOpt(link.ToString(), new Dictionary<UUID, UUID> { [link] = item }));
        Assert.Equal("chest", Program.RlvResolveOpt("chest", new Dictionary<UUID, UUID>()));
    }
}
