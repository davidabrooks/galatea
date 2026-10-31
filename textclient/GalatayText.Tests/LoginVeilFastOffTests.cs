using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-10-30 12:37 relog: every garment Ready by +5.8 s, release decided ~+6.3 s, but the COF link removal was logged at +16 s and the bake at +17.5 s:
// three server inventory reads (root folder, COF, COF read-back) ran BEFORE the bake, each ~3 s behind the login inventory traffic.
// Hair, teeth and eyes were attached before the veil came off (and login-cover put eyes + teeth on at +5.8 s): David saw them floating.
public class LoginVeilFastOffTests
{
    [Fact] public async Task VeilOff_RequestsTheBakeBeforeAnyConfirmationRead()
    {
        var order = new List<string>();
        var (removed, confirmed) = await LoginVeil.OffCore(
            () => order.Add("layer"),
            async () => { await Task.Yield(); order.Add("links"); return true; },
            async () => { await Task.Yield(); order.Add("bake"); },
            async () => { await Task.Yield(); order.Add("confirm"); return true; });
        Assert.True(removed); Assert.True(confirmed);
        Assert.Equal(new[] { "links", "bake", "layer", "confirm" }, order);
    }

    [Fact] public async Task VeilOff_WithSlowServerReads_BakesWithinASecondOfRelease()
    {   // the confirmation (a server round trip at login) may take seconds; the bake request must not wait for it
        var t0 = DateTime.UtcNow; DateTime bakeAt = default;
        await LoginVeil.OffCore(null, () => Task.FromResult(true), () => { bakeAt = DateTime.UtcNow; return Task.CompletedTask; },
                                async () => { await Task.Delay(1500); return true; });
        Assert.True((bakeAt - t0).TotalMilliseconds < 1000);
    }

    [Fact] public async Task VeilOff_FailedLinkRemoval_DoesNotBake()
    {
        bool baked = false;
        var r = await LoginVeil.OffCore(null, () => Task.FromResult(false), () => { baked = true; return Task.CompletedTask; }, () => Task.FromResult(true));
        Assert.False(r.removed); Assert.False(baked);
    }

    [Fact] public void BakeConfirmed_NeedsANewerSimCofVersion()
    {   // 12:37:38: "AvatarAppearance for us (COF v2928)" after the removal from v2927
        Assert.False(LoginVeil.BakeConfirmed(2927, 2927));
        Assert.True(LoginVeil.BakeConfirmed(2927, 2928));
    }

    [Fact] public void HeldHeadParts_AreSkippedByLoginCoverUntilReleased()
    {
        var eyes = new UUID("ddfa1cca-a6c8-35ba-936a-79b61e5dc5e9"); var teeth = new UUID("5825ee5e-5452-32e4-b11e-1e714f0341a3");
        var head = new UUID("11710311-039b-3143-b3d2-d1fc5eeccf15");
        var cof = new[] { new LoginCover.Item(head, "/ HEAD / lel evox / AVALON 4.0"), new LoginCover.Item(eyes, "/ EYES / lel evox 4.0"), new LoginCover.Item(teeth, "/ GUMS / TEETH / lel evox 4.0") };
        var held = new HashSet<UUID> { eyes, teeth };
        var plan = LoginCover.Plan(cof, new HashSet<UUID>(), Program.OutfitSendRank, LoginVeil.HoldExclude(null, held.Contains));
        Assert.Equal(new[] { head }, plan.Select(p => p.Id));   // the 12:37:26 plan attached EYES + TEETH here
        held.Clear();
        Assert.Equal(3, LoginCover.Plan(cof, new HashSet<UUID>(), Program.OutfitSendRank, LoginVeil.HoldExclude(null, held.Contains)).Count);
    }

    [Fact] public void HoldExclude_KeepsTheBaseExclude()
    {
        var ex = LoginVeil.HoldExclude((id, n) => n.StartsWith("#Firestorm"), _ => false);
        Assert.True(ex(UUID.Random(), "#Firestorm LSL Bridge v2.29")); Assert.False(ex(UUID.Random(), "DOUX - Oasis Hairstyle [M/Long]"));
    }

    [Fact] public void HairTeethEyes_AreRevealedWithTheVeilNotWithTheClothes()
    {
        Assert.True(LoginVeil.RevealWithVeil("DOUX - Oasis Hairstyle [M/Long]"));
        Assert.True(LoginVeil.RevealWithVeil("/ GUMS / TEETH / lel evox 4.0"));
        Assert.True(LoginVeil.RevealWithVeil("/ EYES / lel evox 4.0"));
        Assert.False(LoginVeil.RevealWithVeil("TETRA - Chill T-Shirt - LaraX Petite"));
    }

    [Fact] public void Collar_ShownAtLoginOnlyIfItWasVisibleBeforeTheLogoutStealth()
    {
        Assert.Equal("show", LoginVeil.CollarMarker(false, "collar now invisible (Stealth on)"));
        Assert.Equal("keep", LoginVeil.CollarMarker(true, "collar already invisible (Stealth on)"));     // android: stays hidden
        Assert.Equal("keep", LoginVeil.CollarMarker(false, "pressed Stealth but it still reads off"));  // not verified: nothing to undo
        Assert.Equal("keep", LoginVeil.CollarMarker(null, "collar menu did not open"));
        Assert.True(LoginVeil.CollarShowAfterLogin("show\n")); Assert.False(LoginVeil.CollarShowAfterLogin("keep\n")); Assert.False(LoginVeil.CollarShowAfterLogin(null));
    }
}
