using LibreMetaverse;
using Xunit;
using GalatayText;

public class CofUnlinkTests
{
    static readonly UUID A = UUID.Random(), B = UUID.Random();
    static readonly (UUID, string)[] Links = { (A, "Jani tshirt"), (B, "VELOUR: ANGEL Skin for LaraX (SLIM/DUNE)") };
    [Fact] public void ByNameCaseInsensitive() => Assert.Equal(A, Assert.Single(Program.CofUnlinkMatches(Links, "jani TSHIRT")).id);
    [Fact] public void ByUuid() => Assert.Equal(B, Assert.Single(Program.CofUnlinkMatches(Links, B.ToString())).id);
    [Fact] public void NoPartialMatch() => Assert.Empty(Program.CofUnlinkMatches(Links, "Jani"));
    [Fact] public void EmptySpecMatchesNothing() => Assert.Empty(Program.CofUnlinkMatches(Links, " "));
}
