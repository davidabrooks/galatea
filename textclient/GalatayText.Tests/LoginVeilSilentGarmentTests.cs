using GalatayText;
using Xunit;

// 2026-10-31 13:13 login: veil off at +22 s. 'Maitreya LaraX Vintage Jeans Low-Waist #3 (Flat)' never announces Ready, so every login in it waited the
// full 8 s silent timeout (seen +5.1 s -> done +13.1 s). A garment silent at a past login is now waited for 2 s.
public class LoginVeilSilentGarmentTests
{
    const string Jeans = "Maitreya LaraX Vintage Jeans Low-Waist #3 (Flat)", Top = "TETRA - Chill T-Shirt - LaraX Petite";

    [Fact] public void Replay_1313_JeansSilent_UnknownWaits8s_KnownWaits2s()
    {
        var g = new[] { new LoginVeil.Garment(Jeans, 5.1, null) };
        Assert.False(LoginVeil.Release(g, 8.0, stashed: true).release);                            // old: until +13.1 (+0.5 grace)
        Assert.True(LoginVeil.Release(g, 13.7, stashed: true).release);
        var known = new HashSet<string> { Jeans };
        var r = LoginVeil.Release(g, 7.7, stashed: true, knownSilent: known);
        Assert.True(r.release); Assert.Contains("+7.1 s", r.why);                                 // 6 s earlier
        Assert.False(LoginVeil.Release(g, 6.5, stashed: true, knownSilent: known).release);        // still a short wait after it attaches
        Assert.False(LoginVeil.Release(new[] { new LoginVeil.Garment(Jeans, null, null) }, 10, true, known).release);   // never before it is attached
    }

    [Fact] public void UpdateSilent_AddsSilent_RemovesOnesThatSaidReady_IgnoresNonGarments()
    {
        var s = LoginVeil.UpdateSilent(new[] { Top }, new (string, double?, double?)[] { (Jeans, 5.1, null), (Top, 3, 6), ("Maitreya Mesh Body - LaraX HUD V1.1", 1, null), ("Unseen LaraX Skirt", null, null) });
        Assert.Equal(new[] { Jeans }, s);
    }

    [Fact] public void ReleaseWithHead_PassesTheKnownSilentList()
        => Assert.True(LoginVeil.ReleaseWithHead(new[] { new LoginVeil.Garment(Jeans, 5.1, null) }, 7.7, true, null, new HashSet<string> { Jeans }).release);
}
