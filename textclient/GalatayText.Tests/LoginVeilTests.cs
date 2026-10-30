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
        var r = LoginVeil.Release(Live1036(7, 10), 10.2);
        Assert.False(r.release); Assert.Contains("grace", r.why);
        Assert.True(LoginVeil.Release(Live1036(7, 10), 10.6).release);           // Ready + 0.5 s grace (12:03 relog: no more +12 s wait)
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
    // 11:34:10-13 logout: "bake requested" after "Skipping duplicate outfit send (already sent for COF v2918)", then "veil ... on for logout";
    // the 11:34:38 login was still COF v2918 and "veil not worn". The veil must only count as on when its link is in a newer COF.
    [Fact] public void LogoutVeil_UnchangedCofVersion_IsNotOn()
    {
        Assert.False(LoginVeil.LogoutVerdict(false, 2918, 2918).ok);
        Assert.False(LoginVeil.LogoutVerdict(true, 2918, 2918).ok);
        Assert.True(LoginVeil.LogoutVerdict(true, 2918, 2919).ok);
    }

    // 11:48 relog: "[login-veil] veil off at +18.3 s: every garment attached and Ready by +8.3 s", yet David saw the alpha still worn; at 11:49
    // outfit_wear logged "layer off: 'Galatea login veil (body alpha)'" and "1 COF link(s) removed in one batch": the link had never gone.
    [Fact] public void VeilOff_NeedsLinkGoneAndCofMoved()
    {
        Assert.True(LoginVeil.Release(new[] { new LoginVeil.Garment(Shirt, 7, 8.3) }, 18.3).release);
        Assert.False(LoginVeil.OffVerdict(true, 2925, 2925).ok);   // the 11:48 state
        Assert.False(LoginVeil.OffVerdict(true, 2925, 2926).ok);
        Assert.False(LoginVeil.OffVerdict(false, 2925, 2925).ok);
        Assert.True(LoginVeil.OffVerdict(false, 2925, 2926).ok);
    }

    [Fact] public void MaxHold_ReleasesIntoTheSameVerifiedOff()
    {
        var r = LoginVeil.Release(new[] { new LoginVeil.Garment(Shirt, null, null) }, LoginVeil.MaxHoldSec);
        Assert.True(r.release); Assert.Contains("max hold", r.why);
    }

    // 2026-10-30 12:03 relog: 'veil off at +21.5 s: every garment attached and Ready by +9.2 s'; hair and teeth floated around the invisible body.
    const string Teeth = "LeL EvoX Teeth", Head = "-Pretty Liars- Real Kisser V7.0 [FEMALE HEAD]", Kisser = "Real Kisser V7.0", Hud = "Maitreya Mesh Body - LaraX HUD", Collar = "Premium Collar";
    [Fact] public void Stash_TakesHairTeethAndClothes_NotBodyHudsOrCollar()
    {
        Assert.True(LoginVeil.ShouldStash(Hair, 2)); Assert.True(LoginVeil.ShouldStash(Shirt, 1)); Assert.True(LoginVeil.ShouldStash(Boots, 7)); Assert.True(LoginVeil.ShouldStash(Teeth, 11));
        Assert.False(LoginVeil.ShouldStash(Body, 1));
        Assert.False(LoginVeil.ShouldStash(Hud, 35)); Assert.False(LoginVeil.ShouldStash("Some screen thing", 33)); Assert.False(LoginVeil.ShouldStash(Collar, 39)); Assert.False(LoginVeil.ShouldStash("", 5));
    }
    [Fact] public void Reveal_HairAndTeethWithTheVeil_ClothesAtLogin()
    { Assert.True(LoginVeil.RevealWithVeil(Hair)); Assert.True(LoginVeil.RevealWithVeil(Teeth)); Assert.False(LoginVeil.RevealWithVeil(Shirt)); Assert.False(LoginVeil.RevealWithVeil(Boots)); }
    [Fact] public void Stash_RoundTrips()
    {
        var a = new[] { new LoginVeil.Stashed(UUID.Random(), 2, Hair), new LoginVeil.Stashed(UUID.Random(), 55, Shirt) };
        var b = LoginVeil.ParseStash(LoginVeil.FormatStash(a, DateTime.Now));
        Assert.Equal(a, b); Assert.Empty(LoginVeil.ParseStash("# only a comment\n")); Assert.Empty(LoginVeil.ParseStash(null));
    }
    [Fact] public void WithAStash_TheVeilNeverComesOffOverABareBody()
    {
        Assert.False(LoginVeil.Release(Live1036(null, null), LoginVeil.MaxHoldSec, stashed: true).release);   // no 25 s release
        var k = LoginVeil.Release(Live1036(null, null), LoginVeil.MaxHoldStashedSec, stashed: true);
        Assert.False(k.release); Assert.StartsWith("KEEP", k.why);                                          // past 90 s: stays invisible
        Assert.True(LoginVeil.Release(Live1036(7, null), LoginVeil.MaxHoldStashedSec, stashed: true).release); // attached, not Ready: dressed, may release
        Assert.True(LoginVeil.Release(Live1036(7, 10), 10.6, stashed: true).release);
    }
    [Fact] public void StashDetach_KeepsTheCofLinks_SoTheLibraryRestoreIsAFallback() => Assert.True(Program.KeepCofForDetachWhy("login veil stash"));

    // 2026-10-30 14:01 relog (David): "I could see your fingernails and eyelashes floating". No separate nail / lash attachment was worn: the lashes
    // are a non-BOM face of the Lelutka head (the nails of the Maitreya body), which the full alpha cannot hide. The head and lips are stashed now.
    const string EvoxHead = "/ HEAD / lel evox / AVALON 4.0", Eyes = "/ EYES / lel evox 4.0", Gums = "/ GUMS / TEETH / lel evox 4.0", AddOn = "Maitreya Mesh Body - LaraX Petite Add-on V1.1",
                 Bracelet = ".Heol Star Bracelet Gold", Bridge = "#Firestorm LSL Bridge v2.29", Ao = "VISTA ANIMATIONS *HUD 6.3*MARTHA STS BENTO AO-V1.7", N9 = ":: No 9 :: Premium Collar";
    [Fact] public void Stash_IsGeneric_HeadAndLipsIncluded_BodyAddonCollarHudBridgeNot()
    {
        foreach (var (n, p) in new[] { (EvoxHead, 2), (Head, 12), (Kisser, 12), (Eyes, 15), (Gums, 47), (Bracelet, 6), ("Some Brand Lashes", 17), ("Some Brand Nails", 6), ("Unknown Necklace", 39) })
            Assert.True(LoginVeil.ShouldStash(n, p), n);
        foreach (var (n, p) in new[] { (Body, 40), (AddOn, 1), (N9, 39), (Bridge, 30), (Ao, 35), ("Authorizer", 39), ("Maitreya Mesh Body - LaraX HUD V1.1", 34), ("Skeleton fix", 1), ("Bento HandL", 5) })
            Assert.False(LoginVeil.ShouldStash(n, p), n);
    }
    [Fact] public void HeadAndLips_ComeBackWithTheVeil_HeadFirst()
    {
        Assert.True(LoginVeil.RevealWithVeil(EvoxHead)); Assert.True(LoginVeil.RevealWithVeil(Head)); Assert.True(LoginVeil.RevealWithVeil(Eyes));
        var order = new[] { Gums, Hair, Head, EvoxHead }.OrderBy(LoginVeil.RevealOrder).ToArray();
        Assert.Equal(EvoxHead, order[0]); Assert.Equal(Head, order[1]); Assert.Equal(Hair, order[2]);
    }

    // 14:01 relog: Ready +12.3 s, veil decision +15.5 s (the clothes attach was awaited in front of the loop), bake POST +1.75 s after the link removal.
    [Fact] public async Task TheReleaseLoop_DoesNotWaitForTheClothesAttach()
    {
        var gate = new TaskCompletionSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await LoginVeil.BesideLoop(() => gate.Task, () => Task.FromResult(42));
        Assert.Equal(42, r); Assert.True(sw.ElapsedMilliseconds < 1000); gate.SetResult();
    }
    [Fact] public async Task VeilOff_HeadPartsBurstGoesAfterTheLinkRemovalAndBeforeTheBake()
    {
        var steps = new List<string>();
        var (rm, ok) = await LoginVeil.OffCore(() => steps.Add("drop"), () => { steps.Add("links"); return Task.FromResult(true); },
            () => { steps.Add("bake"); return Task.CompletedTask; }, () => { steps.Add("confirm"); return Task.FromResult(true); },
            () => { steps.Add("burst"); return Task.CompletedTask; });
        Assert.True(rm && ok); Assert.Equal(new[] { "drop", "links", "burst", "bake", "confirm" }, steps);
        steps.Clear();
        await LoginVeil.OffCore(null, () => Task.FromResult(false), () => { steps.Add("bake"); return Task.CompletedTask; }, () => Task.FromResult(true), () => { steps.Add("burst"); return Task.CompletedTask; });
        Assert.Empty(steps);   // links not removed: no head parts over a still-veiled body, no bake
    }

    // 14:01 relog (David): "the collar disappeared last instead of first".
    [Fact] public async Task Logout_CollarStealthFinishesBeforeTheVeilAndTheStash()
    {
        var steps = new List<string>();
        var ok = await LoginVeil.LogoutCore(async () => { await Task.Delay(50); steps.Add("collar"); }, 5000,
            () => { steps.Add("veil"); return Task.FromResult(true); }, () => { steps.Add("stash"); return Task.CompletedTask; });
        Assert.True(ok); Assert.Equal(new[] { "collar", "veil", "stash" }, steps);
        steps.Clear();
        await LoginVeil.LogoutCore(() => new TaskCompletionSource().Task, 100, () => { steps.Add("veil"); return Task.FromResult(false); }, () => { steps.Add("stash"); return Task.CompletedTask; });
        Assert.Equal(new[] { "veil" }, steps);   // a hung collar menu is capped; veil not verified: nothing comes off
    }
}

// 2026-10-30 14:38:27 relog: FetchInventory2 not available at login, every stash fetch hung ~10 s: the clothes came on at +10.5 s (the library's
// outfit send, not the stash) and the 4 head parts were "not prefetched yet" at veil-off. The stash now attaches from its own ids, no fetch.
public class LoginVeilNoFetchTests
{
    [Fact] public void AttachRecord_NeedsNoFetch_ItemIdAndNameFromTheStashFile()
    {
        var x = new LoginVeil.Stashed(UUID.Random(), 55, "TETRA - Chill T-Shirt - LaraX Petite"); var me = UUID.Random();
        var r = LoginVeil.AttachRecord(x, me, null);
        Assert.Equal(x.Item, r.UUID); Assert.Equal(x.Item, r.ResolvedItemID); Assert.Equal(x.Name, r.Name); Assert.Equal(me, r.OwnerID); Assert.False(r.IsLink());
    }
    [Fact] public void AttachRecord_PrefersACachedRealItem_NeverALinkOrAnotherItem()
    {
        var x = new LoginVeil.Stashed(UUID.Random(), 2, "DOUX - Oasis Hairstyle [M/Long]");
        var real = new InventoryObject(x.Item) { Name = "real", OwnerID = UUID.Random() };
        Assert.Same(real, LoginVeil.AttachRecord(x, UUID.Random(), real));
        var other = new InventoryObject(UUID.Random()) { Name = "other" };
        Assert.Equal(x.Item, LoginVeil.AttachRecord(x, UUID.Random(), other).UUID);
    }
    [Fact] public void WaitSource_UsesTheStashClothes_UntilTheCofIsRead()
    {
        var shirt = (UUID.Random(), "TETRA - Chill T-Shirt - LaraX Petite"); var boots = (UUID.Random(), "TETRA - Moca Ankle Boots (Suede) - Maitreya Mid");
        Assert.Equal(new List<(UUID, string)> { shirt, boots }, LoginVeil.WaitSource(null, new() { shirt, boots }));
        Assert.Null(LoginVeil.WaitSource(null, new() { boots }));        // nothing body-cutting stashed: wait for the COF as before
        var cof = new List<(UUID, string)> { shirt };
        Assert.Same(cof, LoginVeil.WaitSource(cof, new() { shirt, boots }));
        // a stash shirt that is not attached yet keeps the veil on (never nude)
        var g = LoginVeil.WaitSource(null, new() { shirt })!.Select(l => new LoginVeil.Garment(l.Name, null, null));
        Assert.False(LoginVeil.Release(g, 3, stashed: true).release);
    }
}
public class LoginVeilLayerDropTests
{
    [Fact] public void LayerRecord_WithoutAFetch_StillDropsTheWornAlpha()
    {
        var id = UUID.Random(); var r = LoginVeil.LayerRecord(id, null, WearableType.Alpha);
        var w = Assert.IsType<InventoryWearable>(r); Assert.Equal(id, w.UUID); Assert.Equal(WearableType.Alpha, w.WearableType); Assert.NotEqual(AssetType.Bodypart, w.AssetType);
        Assert.Equal(WearableType.Alpha, ((InventoryWearable)LoginVeil.LayerRecord(id, null, null)).WearableType);
    }
    [Fact] public void LayerRecord_PrefersTheFetchedWearable()
    { var f = new InventoryWearable(UUID.Random()) { WearableType = WearableType.Alpha }; Assert.Same(f, LoginVeil.LayerRecord(f.UUID, f, WearableType.Tattoo)); }
}
