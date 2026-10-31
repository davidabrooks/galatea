using System.Linq;
using Xunit;
namespace GalatayText.Tests;

// 2026-10-30 17:03 relog (David): the head handles the eyes; the veil flow must not stash, detach, hold or re-attach them.
public class LoginVeilEyesTests
{
    [Fact] public void Eyes_AreNeverStashed()
    {
        Assert.False(LoginVeil.ShouldStash("/ EYES / lel evox 4.0", 15));
        Assert.False(LoginVeil.ShouldStash("Some mesh eyes", 2));
        Assert.True(LoginVeil.ShouldStash("DOUX - Oasis Hairstyle [M/Long]", 2));
        Assert.True(LoginVeil.ShouldStash("/ GUMS / TEETH / lel evox 4.0", 47));
        Assert.False(LoginVeil.IsHeadEyes("Glam Affair eyeshadow", 2));
    }
    [Fact] public void OldStashFile_EyesLineDropped_SoLoginNeitherHoldsNorReattachesThem()
    {
        var text = "# login veil stash 2026-10-30 17:03:27\n"
            + "0408644c-9d6b-387d-9871-c2792a54069a\t2\tDOUX - Oasis Hairstyle [M/Long]\n"
            + "ddfa1cca-a6c8-35ba-936a-79b61e5dc5e9\t15\t/ EYES / lel evox 4.0\n"
            + "5825ee5e-5452-32e4-b11e-1e714f0341a3\t47\t/ GUMS / TEETH / lel evox 4.0\n";
        var s = LoginVeil.ParseStash(text);
        Assert.Equal(2, s.Count);
        Assert.DoesNotContain(s, x => x.Item.ToString() == "ddfa1cca-a6c8-35ba-936a-79b61e5dc5e9");
    }
}
