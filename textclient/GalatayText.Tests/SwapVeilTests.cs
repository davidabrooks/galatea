using LibreMetaverse;
using LibreMetaverse.Assets;
using GalatayText;
using Xunit;

public class SwapVeilTests
{
    static readonly string[] Human = { "Maitreya Mesh Body - LaraX V1.1", "Maitreya Mesh Body - LaraX Petite Add-on V1.1", "TETRA - Chill T-Shirt - LaraX Petite", "Maitreya LaraX Vintage Jeans Low-Waist #3 (Flat)", "DOUX - Oasis Hairstyle [M/Long]" };
    static readonly string[] Android = { "MISAKI -Kay Body V-Tech", "MISAKI -Kay 3.0 Skeleton", "CALISTA hair long (chrome cap)" };

    [Fact] public void AndroidToHumanGetsTheVeil()
    {
        var (v, why) = SwapVeil.Wanted(true, "Android", "TETRA Chill T-Shirt", Human, true, false);
        Assert.True(v, why); Assert.Contains("android -> human", why);
        Assert.True(SwapVeil.Wanted(true, "Android Base", "Naked", new[] { "Maitreya Mesh Body - LaraX V1.1" }, true, false).veil);
        // the outfit name alone (prim names not loaded yet) is enough to know she is an android
        Assert.True(SwapVeil.Wanted(false, "Android Base", "TETRA Chill T-Shirt", Human, true, false).veil);
        // the body actually worn is enough when the outfit name is unknown
        Assert.True(SwapVeil.Wanted(true, null, "TETRA Chill T-Shirt", Human, true, false).veil);
    }

    [Fact] public void OtherSwapsGetNoVeil()
    {
        Assert.False(SwapVeil.Wanted(false, "TETRA Chill T-Shirt", "Valentine Dress", Human, true, false).veil);        // human -> human
        Assert.False(SwapVeil.Wanted(false, "TETRA Chill T-Shirt", "Android Base", Android, true, false).veil);         // human -> android
        Assert.False(SwapVeil.Wanted(true, "Android", "Android Base", Android, true, false).veil);                      // android -> android
        Assert.False(SwapVeil.Wanted(true, "Android", "Robot spare", Android, true, false).veil);                       // android body, other name
        Assert.False(SwapVeil.Wanted(true, "Android", "Hat only", new[] { "Straw hat" }, true, false).veil);            // keeps the current body
    }

    [Fact] public void NoVeilWithoutItemOrUnderTheLoginVeil()
    {
        Assert.Contains("no swap veil", SwapVeil.Wanted(true, "Android", "TETRA Chill T-Shirt", Human, false, false).why);
        Assert.Contains("login veil", SwapVeil.Wanted(true, "Android", "TETRA Chill T-Shirt", Human, true, true).why);
        Assert.False(SwapVeil.Wanted(true, "Android", "TETRA Chill T-Shirt", Human, true, true).veil);
    }

    [Fact] public void OnlyABodyOnlyVeilIsUsed()
    {
        Assert.True(SwapVeil.IsBodyVeilName(LoginVeilAsset.SwapName));
        Assert.True(SwapVeil.IsBodyVeilName("Galatea login veil (body alpha)"));
        Assert.False(SwapVeil.IsBodyVeilName(LoginVeilAsset.Name));   // the full-avatar login veil would leave eyes, teeth and hair floating
        Assert.False(SwapVeil.IsBodyVeilName(null));
        var id = UUID.Random();
        Assert.Equal(id, SwapVeil.ItemFrom($"# comment\n{id} {LoginVeilAsset.SwapName}\n"));
        Assert.Equal(SwapVeil.LegacyBodyVeil, SwapVeil.ItemFrom(null));
        Assert.Equal(SwapVeil.LegacyBodyVeil, SwapVeil.ItemFrom("# nothing\n"));
    }

    [Fact] public void SwapVeilAssetMasksTheBodyOnly()
    {
        var a = LoginVeilAsset.BuildBody(UUID.Random());
        var d = new AssetClothing(UUID.Random(), a.AssetData);
        Assert.True(d.Decode());
        Assert.Equal(WearableType.Alpha, d.WearableType);
        Assert.Equal(LoginVeilAsset.Invisible, d.Textures[AvatarTextureIndex.UpperAlpha]);
        Assert.Equal(LoginVeilAsset.Invisible, d.Textures[AvatarTextureIndex.LowerAlpha]);
        foreach (var i in new[] { AvatarTextureIndex.HeadAlpha, AvatarTextureIndex.EyesAlpha, AvatarTextureIndex.HairAlpha })
            Assert.False(d.Textures.TryGetValue(i, out var t) && t == LoginVeilAsset.Invisible);
        Assert.Equal(LoginVeilAsset.SwapName, d.Name);
    }

    static SwapVeil.State S(double? end, bool bake = true, double stable = 5, string[] notOn = null, string[] notOff = null, params LoginVeil.Garment[] g)
        => new(g, notOn ?? new string[0], notOff ?? new string[0], bake, stable, end);
    const string Shirt = "TETRA - Chill T-Shirt - LaraX Petite", Jeans = "Maitreya LaraX Vintage Jeans Low-Waist #3 (Flat)";

    [Fact] public void StaysOnUntilEverythingSettled()
    {
        Assert.False(SwapVeil.OffDecision(S(null), 10).off);                                                     // the change is still running
        Assert.Contains("not on yet", SwapVeil.OffDecision(S(20, notOn: new[] { Shirt }), 21).why);
        Assert.Contains("not off yet", SwapVeil.OffDecision(S(20, notOff: new[] { "MISAKI -Kay Body V-Tech" }), 21).why);
        Assert.Contains("not Ready", SwapVeil.OffDecision(S(20, g: new LoginVeil.Garment(Shirt, 15, null)), 21).why);
        Assert.Contains("bake", SwapVeil.OffDecision(S(20, bake: false, g: new LoginVeil.Garment(Shirt, 12, 13)), 21).why);
        Assert.Contains("changing", SwapVeil.OffDecision(S(20, stable: 0.3, g: new LoginVeil.Garment(Shirt, 12, 13)), 21).why);
        Assert.Contains("grace", SwapVeil.OffDecision(S(20, g: new LoginVeil.Garment(Shirt, 12, 20.2)), 20.4).why);
        var ok = SwapVeil.OffDecision(S(20, g: new LoginVeil.Garment(Shirt, 12, 13)), 20.6);
        Assert.True(ok.off); Assert.False(ok.timedOut); Assert.StartsWith("settled", ok.why);
    }

    [Fact] public void SilentGarmentsUseTheLoginVeilTimeouts()
    {
        // the jeans never say Ready: done ReadyTimeoutSec after they were seen, or KnownSilentTimeoutSec when known silent
        Assert.False(SwapVeil.OffDecision(S(10, g: new LoginVeil.Garment(Jeans, 9, null)), 12).off);
        Assert.True(SwapVeil.OffDecision(S(10, g: new LoginVeil.Garment(Jeans, 9, null)), 9 + LoginVeil.ReadyTimeoutSec + SwapVeil.GraceSec).off);
        Assert.True(SwapVeil.OffDecision(S(10, g: new LoginVeil.Garment(Jeans, 9, null)), 11.6, new HashSet<string> { Jeans }).off);
        // garments the waits ignore (hair, add-ons) never hold it
        Assert.True(SwapVeil.OffDecision(S(10, g: new LoginVeil.Garment("DOUX - Oasis Hairstyle [M/Long]", null, null)), 10.6).off);
    }

    [Fact] public void MaxSettleAlwaysTakesItOffWithAWarning()
    {
        var t = SwapVeil.OffDecision(S(20, bake: false, notOn: new[] { Shirt }, g: new LoginVeil.Garment(Shirt, null, null)), 20 + SwapVeil.MaxSettleSec);
        Assert.True(t.off); Assert.True(t.timedOut); Assert.Contains("max settle", t.why); Assert.Contains(Shirt, t.why); Assert.Contains("bake", t.why);
        Assert.False(SwapVeil.OffDecision(S(20, bake: false), 20 + SwapVeil.MaxSettleSec - 0.1).off);
        var failed = SwapVeil.OffDecision(new SwapVeil.State(new LoginVeil.Garment[0], new string[0], new string[0], false, 0, null, SwapFailed: true), 3);
        Assert.True(failed.off); Assert.True(failed.timedOut);
    }

    [Fact] public async Task WaitSettledReleasesOnSettleAndOnTimeout()
    {
        double clock = 0;
        // Ready comes at +14 s, the change ends at +12 s: off at the first tick past 14 + grace
        var r = await SwapVeil.WaitSettled(() => clock, now => S(12, g: new LoginVeil.Garment(Shirt, 10, now >= 14 ? 14 : null)), () => { clock += SwapVeil.TickSec; return Task.CompletedTask; });
        Assert.False(r.timedOut); Assert.InRange(r.atSec, 14 + SwapVeil.GraceSec - 0.01, 14 + SwapVeil.GraceSec + SwapVeil.TickSec + 0.01);
        // never settles: off at the cap, flagged
        clock = 0; var waits = new List<string>();
        var t = await SwapVeil.WaitSettled(() => clock, now => S(12, bake: false), () => { clock += SwapVeil.TickSec; return Task.CompletedTask; }, onWait: waits.Add);
        Assert.True(t.timedOut); Assert.InRange(t.atSec, 12 + SwapVeil.MaxSettleSec - 0.01, 12 + SwapVeil.MaxSettleSec + SwapVeil.TickSec + 0.01);
        Assert.Single(waits);   // one log line per new wait reason, not one per tick
    }

    [Fact] public async Task OffRunsEvenWhenTheWaitThrows()
    {
        string got = null; bool? flagged = null;
        var r = await SwapVeil.OffAfter(() => throw new InvalidOperationException("boom"), (why, to) => { got = why; flagged = to; return Task.FromResult("off"); });
        Assert.Equal("off", r); Assert.Contains("boom", got); Assert.True(flagged);
        r = await SwapVeil.OffAfter(() => Task.FromResult((false, "settled", 15.0)), (why, to) => { got = why; flagged = to; return Task.FromResult("off2"); });
        Assert.Equal("off2", r); Assert.Contains("+15.0 s", got); Assert.False(flagged);
    }

    [Fact] public void SwapLayerPassKeepsTheVeil()
    {
        var veil = UUID.Random(); var bimbette = UUID.Random(); var shirtAlpha = UUID.Random();
        var keep = Program.EffectiveOutfitLayers(new[] { shirtAlpha }, new UUID[0], true); keep.Add(veil);
        var off = Program.LayersNotInOutfit(new[] { veil, bimbette, shirtAlpha }, keep);
        Assert.DoesNotContain(veil, off); Assert.Contains(bimbette, off);
    }
}
