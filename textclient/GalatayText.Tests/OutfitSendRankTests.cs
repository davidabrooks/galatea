using System.Linq;
using GalatayText;
using Xunit;
public class OutfitSendRankTests
{
    // the 08:43 login's COF objects in their log (COF) order: the body must not be sent behind hair, headphones, eyes, the AO and the collar
    static readonly string[] Cof = {
        "CALISTA hair long (chrome cap)", "MISAKI -Kay PalmR", "MISAKI -Kay Bare Foot R", "/ HEAD / lel evox / AVALON 4.0", "MISAKI -Kay Bare Foot L",
        "MISAKI -Kay Headphones", "MISAKI -Kay Body", "Maitreya Mesh Body - LaraX HUD V1.1", "MISAKI -Kay 3.0 Skeleton", "Cyber_Eyes(mech) R", "MISAKI -Kay HipR",
        "VISTA ANIMATIONS *HUD 6.3*MARTHA STS BENTO AO-V1.7", ":: No 9 :: Premium Collar", "-Pretty Liars- Real Kisser V7.0 [FEMALE HEAD]", "MISAKI -Kay HandL", "Kay HP" };
    [Fact] public void BodyFirst_HUDsAndCollarLast()
    {
        var sorted = Cof.OrderBy(Program.OutfitSendRank).ToList();            // stable, like the library's OrderBy
        Assert.Equal("MISAKI -Kay Body", sorted[0]);
        int hair = sorted.IndexOf("CALISTA hair long (chrome cap)"), headphones = sorted.IndexOf("MISAKI -Kay Headphones");
        foreach (var part in new[] { "MISAKI -Kay PalmR", "MISAKI -Kay Bare Foot R", "MISAKI -Kay HandL", "MISAKI -Kay HipR", "MISAKI -Kay 3.0 Skeleton", "/ HEAD / lel evox / AVALON 4.0", "-Pretty Liars- Real Kisser V7.0 [FEMALE HEAD]", "Kay HP" })
            Assert.True(sorted.IndexOf(part) < hair && hair < headphones, part);
        Assert.True(sorted.IndexOf("VISTA ANIMATIONS *HUD 6.3*MARTHA STS BENTO AO-V1.7") > sorted.IndexOf("Cyber_Eyes(mech) R"));
        Assert.True(sorted.IndexOf(":: No 9 :: Premium Collar") > sorted.IndexOf("Cyber_Eyes(mech) R"));
        Assert.Equal(8, Program.OutfitSendRank("Maitreya Mesh Body - LaraX HUD V1.1"));   // a body HUD is not the body
        Assert.Equal(3, Program.OutfitSendRank("MISAKI -Kay Headphones"));               // 'Head' inside a word is not a head
    }
    [Fact] public void SameRank_KeepsCofOrder()
    {
        Assert.Equal(new[] { "b", "a", "c" }, new[] { "b", "a", "c" }.OrderBy(Program.OutfitSendRank).ToArray());
        Assert.Equal(3, Program.OutfitSendRank(null));
    }
}
