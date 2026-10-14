using GalatayText;
using Xunit;

namespace GalatayText.Tests;

// 2026-10-13 David: Reiley Net Chair, he got the woman's AVsitter role and I ended up under him; swap first.
public class CoupleRolesTests
{
    [Fact]
    public void BareSitterNumberIsNotARole()
    {
        var (r, why) = Program.RoleFromMenuText("AVsitter™ 1.29\nSitter 1");
        Assert.Equal(Program.CoupleRole.Unknown, r);
        Assert.Contains("only a sitter number", why);
    }

    [Theory]
    [InlineData("AVsitter™ 2.2\nSitter 0 (Female)", "Female")]
    [InlineData("Sitter 1 - Her", "Female")]
    [InlineData("Sitter 0 [M]", "Male")]
    [InlineData("Position 2: Him", "Male")]
    public void MenuNamesTheRole(string text, string want) => Assert.Equal(want, Program.RoleFromMenuText(text).role.ToString());

    [Fact]
    public void ReileyCase_HeIsOnMyLap_IsReversed()
    {   // 19:59: default pose had him above me
        var (v, _) = Program.CoupleRoleVerdict(Program.CoupleRole.Unknown, "Sitter 1", 29.30f, 29.62f, true);
        Assert.Equal("reversed", v);
    }

    [Fact]
    public void MeOnHisLap_IsOk()
    {
        Assert.Equal("ok", Program.CoupleRoleVerdict(Program.CoupleRole.Unknown, "", 29.70f, 29.35f, true).verdict);
    }

    [Fact]
    public void SideBySideOrTallerStandingPartner_IsUnknown_NoSwap()
    {
        Assert.Equal("unknown", Program.CoupleRoleVerdict(Program.CoupleRole.Unknown, "", 29.40f, 29.45f, true).verdict);
        Assert.Equal("unknown", Program.CoupleRoleVerdict(Program.CoupleRole.Unknown, "", 30.00f, 30.18f, false).verdict);   // he's just taller
        Assert.Equal("unknown", Program.CoupleRoleVerdict(Program.CoupleRole.Unknown, "", null, 30f, true).verdict);
    }

    [Fact]
    public void ExplicitMenuRoleBeatsHeights()
    {
        Assert.Equal("reversed", Program.CoupleRoleVerdict(Program.CoupleRole.Male, "menu", 31f, 29f, true).verdict);
        Assert.Equal("ok", Program.CoupleRoleVerdict(Program.CoupleRole.Female, "menu", 29f, 31f, true).verdict);
    }

    [Fact]
    public void FindsTheSwapButton()
    {
        var reiley = new[] { "My Heart", "[SWAP]", "[ADJUST]", "Relax", "Just Us", "Feelings", "Drama", "Snuggle", "Sweetness" };
        Assert.Equal((1, "swap"), Program.RoleSwapButton(reiley));
        Assert.Equal((2, "role-select"), Program.RoleSwapButton(new[] { "M", "[BACK]", "F" }));
        Assert.Equal(-1, Program.RoleSwapButton(new[] { "Relax", "[ADJUST]" }).index);
    }
}
