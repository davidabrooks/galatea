using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-10-30 10:36:17 relog (textclient.log): boots and shorts +3 s, shorts Ready +5 s, body and T-shirt +7 s, T-shirt Ready +10 s. David saw the body uncovered
// for a few seconds before the clothes. The veil (body-only alpha layer) must stay on until every LaraX garment is attached and Ready.
public class LoginVeilTests
{
    const string Body = "Maitreya Mesh Body - LaraX V1.1", Shorts = "TETRA - Geena ripped shorts - LaraX (low waist)", Shirt = "TETRA - Chill T-Shirt - LaraX Petite",
                 Boots = "TETRA - Moca Ankle Boots (Suede) - Maitreya Mid", Hair = "DOUX - Oasis Hairstyle [M/Long]";
    static LoginVeil.Garment[] Live1036(double? shirtSeen, double? shirtReady) => new[] {
        new LoginVeil.Garment(Body, 7, null), new LoginVeil.Garment(Shorts, 3, 5), new LoginVeil.Garment(Shirt, shirtSeen, shirtReady),
        new LoginVeil.Garment(Boots, 3, null), new LoginVeil.Garment(Hair, 1, null) };

    [Fact] public void TheLiveLogin_KeepsTheVeilWhileTheBodyIsThereButTheShirtIsNot()
    {
        Assert.False(LoginVeil.Release(Live1036(null, null), 7.0).release);       // body at +7, T-shirt not attached yet
        Assert.False(LoginVeil.Release(Live1036(7, null), 8.0).release);          // attached, not Ready: the David-saw-the-body window
        var r = LoginVeil.Release(Live1036(7, 10), 10.5);
        Assert.False(r.release); Assert.Contains("grace", r.why);
        Assert.True(LoginVeil.Release(Live1036(7, 10), 11.6).release);           // Ready + grace: now the body may show, dressed
    }
    [Fact] public void BootsHairAndTheBodyItself_AreNotWaitedFor()
    { Assert.False(LoginVeil.Waits(Boots)); Assert.False(LoginVeil.Waits(Hair)); Assert.False(LoginVeil.Waits(Body)); Assert.True(LoginVeil.Waits(Shirt)); }
    [Fact] public void PurposelyNaked_ReleasesAtOnce() => Assert.True(LoginVeil.Release(new[] { new LoginVeil.Garment(Body, 4, null) }, 1).release);
    [Fact] public void MaxHold_AlwaysReleases()
    {
        var r = LoginVeil.Release(Live1036(null, null), LoginVeil.MaxHoldSec);
        Assert.True(r.release); Assert.Contains(Shirt, r.why);
    }
    [Fact] public void VeilFile()
    {
        var id = new UUID("11111111-2222-3333-4444-555555555555");
        Assert.Equal(id, LoginVeil.ParseVeilFile("# body-only alpha\n\n11111111-2222-3333-4444-555555555555  Galatea login veil\n"));
        Assert.Equal(UUID.Zero, LoginVeil.ParseVeilFile("# nothing\n")); Assert.Equal(UUID.Zero, LoginVeil.ParseVeilFile(null));
    }
}
