using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-10-30 16:24 login (textclient.log 16:23:37-16:26:11): David saw my head with hair and teeth, my boots and 'The V' floating over the veiled
// body; the veil stuck ~90 s (waiting for the nipple rings' "Ready") and had to come off by hand. The logout's early wave (hair, eyes, teeth,
// earrings) was confirmed gone at 16:23:54, then the veil bake's after-bake outfit send put all four back at 16:24:03, so the sim restored them at login.
public class LoginVeilHoldTests
{
    const string Hair = "DOUX - Oasis Hairstyle [M/Long]", Teeth = "/ GUMS / TEETH / lel evox 4.0", Eyes = "/ EYES / lel evox 4.0", Ears = ".Heol Star Earrings Gold",
        Bracelet = ".Heol Star Bracelet Gold", Boots = "TETRA - Moca Ankle Boots (Suede) - Maitreya Mid", Rings = "[BB] Nipple Rings - LaraX Petite (Puffy)",
        V = "The V - Bento by Session Skins & ASA Studios", VCof = "The V - Bento for Maitreya LaraX - SCULPTED - V1.61",
        Shirt = "TETRA - Chill T-Shirt - LaraX Petite", Shorts = "TETRA - Geena ripped shorts - LaraX (low waist)", Head = "/ HEAD / lel evox / AVALON 4.0";
    static LoginVeil.Stashed S(string n, int p = 1) => new(UUID.Random(), p, n);

    [Fact] public void BodyAddOns_AreNotGarments_AndAreNeverWaitedOn()
    {
        foreach (var n in new[] { V, VCof, Rings }) { Assert.True(LoginVeil.IsBodyAddOn(n), n); Assert.False(LoginVeil.Waits(n), n); Assert.False(LoginVeil.IsCoveringGarment(n), n); }
        Assert.True(LoginVeil.Waits(Shirt)); Assert.True(LoginVeil.IsCoveringGarment(Shirt)); Assert.True(LoginVeil.IsCoveringGarment(Shorts));
        Assert.False(LoginVeil.IsCoveringGarment(Boots)); Assert.False(LoginVeil.IsCoveringGarment(Bracelet)); Assert.False(LoginVeil.IsCoveringGarment(Hair));
    }

    [Fact] public void TheLiveStash_Undressed_HoldsEverythingUntilVeilOff()
    {   // 16:24: "clothes now (4)" = bracelet, boots, rings, The V were sent at +1 s and floated
        var (now, held) = LoginVeil.SplitStash(new[] { S(Eyes), S(Ears), S(Hair), S(Teeth), S(Bracelet), S(Boots), S(Rings), S(V) });
        Assert.Empty(now); Assert.Equal(8, held.Count);
    }

    [Fact] public void Dressed_SendsGarmentsAndShoesNow_HoldsAddOnsJewelleryAndHeadParts()
    {
        var (now, held) = LoginVeil.SplitStash(new[] { S(Hair), S(Teeth), S(Shirt), S(Shorts), S(Boots), S(Bracelet), S(Rings), S(V) });
        Assert.Equal(new[] { Shirt, Shorts, Boots }, now.Select(x => x.Name));
        Assert.Equal(new[] { Hair, Teeth, Bracelet, Rings, V }, held.Select(x => x.Name));
    }

    [Fact] public void RealHead_IsNeverHeld() { Assert.False(LoginVeil.HeldForVeilOff(Head, false)); Assert.False(LoginVeil.HeldForVeilOff(Head, true)); }

    [Fact] public void HeldItemsWornBeforeVeilOff_AreTakenOffAgain()
    {
        UUID h = UUID.Random(), t = UUID.Random(), b = UUID.Random();
        var off = LoginVeil.HeldWornEarly(new[] { h, t }, new HashSet<UUID> { h, b });
        Assert.Equal(new[] { h }, off);
    }

    [Fact] public void ASilentGarment_DoesNotHoldTheVeil_PastTheReadyTimeout()
    {   // the rings (seen +5 s) never said Ready: the veil stayed to +90 s. Now a seen, silent garment counts after ReadyTimeoutSec.
        var g = new[] { new LoginVeil.Garment(Shirt, 3, 4), new LoginVeil.Garment("Some LaraX top that never talks", 5, null) };
        Assert.False(LoginVeil.Release(g, 6, stashed: true).release);
        Assert.True(LoginVeil.Release(g, 5 + LoginVeil.ReadyTimeoutSec + LoginVeil.GraceSec + 0.01, stashed: true).release);
        // an add-on in the waited list is ignored outright
        Assert.True(LoginVeil.Release(new[] { new LoginVeil.Garment(Shirt, 3, 4), new LoginVeil.Garment(Rings, 5, null) }, 5, stashed: true).release);
        // not attached at all still holds (never nude)
        Assert.False(LoginVeil.Release(new[] { new LoginVeil.Garment(Shirt, null, null) }, 30, stashed: true).release);
    }

    [Fact] public void HeadGate_StaysStrict_WithTheReadyTimeout()
        => Assert.False(LoginVeil.ReleaseWithHead(new[] { new LoginVeil.Garment(Shirt, 3, 4) }, 30, true, new[] { Head }).release);

    [Fact] public void LoginOrder_HeadUnderTheVeil_IsNotABaseFaceFlash()
    {   // 16:25:26: "real head arrived at +5 s: the base face showed for about 5 s" while the veil was on until +90 s
        var w = new[] { new LoginOrder.Worn("Maitreya Mesh Body - LaraX V1.1", 4, null), new LoginOrder.Worn(Head, 5, null), new LoginOrder.Worn(Rings, 5, null) };
        var f = LoginOrder.Findings(w, veilOffSec: 90);
        Assert.Contains(f, l => l.Contains("under the login veil") && l.Contains("hidden"));
        Assert.DoesNotContain(f, l => l.Contains("base face showed") || l.Contains("Nipple Rings"));
        Assert.Contains(LoginOrder.Findings(w, veilOffSec: 2), l => l.Contains("AFTER the veil came off") && l.Contains("base face showed"));
    }
}
