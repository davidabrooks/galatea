using GalatayText;
using LibreMetaverse;
using Xunit;
// 2026-11-09 (David t3202u): "you shouldn't do anything when I leash you. I might want to lead you around without any of it
// visible." A leash/unleash never touches the collar (no Stealth change or restore, no Magic Leash change), in any form.
// An invisible chain (TEXTURE_TRANSPARENT or fully see-through particles) still means leashed, but is not a VISIBLE chain.
public class LeashStealthTests
{
    static readonly UUID Chain = new("7bad955b-3e29-ee67-7f07-ea53b515b23f");   // the N° 9 chain texture seen 2026-11-09 16:30:59
    [Fact] public void AndroidOutfitNames()
    {
        Assert.True(Program.IsAndroidOutfit("Android")); Assert.True(Program.IsAndroidOutfit("Android Base"));
        Assert.False(Program.IsAndroidOutfit("Beach")); Assert.False(Program.IsAndroidOutfit(null));
    }
    [Fact] public void StealthPressOnlyWhenStateDiffers()   // explicit collar_stealth / veil calls still work this way
    {
        Assert.True(Program.StealthNeedsPress("☑ Stealth", true, want: false));
        Assert.False(Program.StealthNeedsPress("☐ Stealth", false, want: false));
        Assert.False(Program.StealthNeedsPress(null, false, want: false));
        Assert.True(Program.StealthNeedsPress("☐ Stealth", false, want: true));
        Assert.False(Program.StealthNeedsPress("☑ Stealth", true, want: true));
    }
    [Fact] public void LeashAndUnleashNeverTouchTheCollar()
    {
        var root = Root();
        string Src(string f) => System.IO.File.ReadAllText(System.IO.Path.Combine(root, "textclient/galatay-text", f));
        var leash = Src("Leash.cs");
        string Body(string sig) { var b = leash[leash.IndexOf(sig)..]; return b[..b.IndexOf("\n    }\n")]; }
        foreach (var body in new[] { Body("static void LeashStart("), Body("static void LeashEnd(") })
            foreach (var bad in new[] { "CollarStealth", "MagicLeash", "LeashCollarOn", "Premium Collar", "dialog " })
                Assert.DoesNotContain(bad, body);
        foreach (var f in new[] { "Leash.cs", "LeashPull.cs", "LeashRelease.cs", "Rlv.cs" })
            foreach (var bad in new[] { "CollarStealthSet(", "CollarStealthOn(", "MagicLeashSet(", "touch-attachment \"Premium Collar\"" })
                Assert.DoesNotContain(bad, Src(f));
        var cs = Src("CollarStealth.cs");
        foreach (var gone in new[] { "LeashCollarOnLeashRun", "LeashCollarOnUnleash", "MagicLeashSet", "LeashChainResetOnUnleash", "LeashCollarPlan" })
            Assert.DoesNotContain(gone, cs);
        Assert.Contains("CollarStealthOn(\"command\")", Src("Program.cs"));   // the explicit collar_stealth command stays
    }
    [Fact] public void ChainVisibleOnlyWithARealTextureAndAlpha()
    {
        Assert.True(Program.LeashChainVisible(Chain, 1f, 1f));
        Assert.True(Program.LeashChainVisible(Chain, 0f, 0.5f));
        Assert.False(Program.LeashChainVisible(Program.TextureTransparent, 1f, 1f));   // Magic Leash invisible (16:30:41, 16:31:36)
        Assert.False(Program.LeashChainVisible(Chain, 0f, 0f));                       // fully see-through particles
        Assert.False(Program.LeashChainVisible(Chain, 0.04f, 0.05f));
        Assert.Equal(new UUID("8dcd4a48-2d37-4909-9f78-f7a9eb4ef903"), Program.TextureTransparent);
    }
    [Fact] public void StatusText()
    {
        Assert.Equal("none", Program.LeashChainText(false, false));
        Assert.Equal("visible", Program.LeashChainText(true, true));
        Assert.Equal("invisible (still leashed)", Program.LeashChainText(true, false));
    }
    [Fact] public void HiddenCollarAndInvisibleChain_PicksGroundSitNotKneel()
    {
        // human form, Stealth on, leashed with an invisible chain: leashVisible=false -> ground sit family, never the collar kneel
        Assert.Equal("groundsit", Program.IdlePoseKind(false, Program.LeashChainVisible(Program.TextureTransparent, 1f, 1f)));
        Assert.Equal("collar", Program.IdlePoseKind(false, Program.LeashChainVisible(Chain, 1f, 1f)));   // visible chain unchanged
        // kneeling when the chain turns invisible and the collar is hidden: after the unleash it switches to the ground sit
        Assert.Equal("groundsit", Program.IdleSwitch("collar", false, false, false, true));
        // while leashed the existing rule holds: no NEW sit/kneel (IdleEnterKindFor), and an invisible chain is still "leashed"
        Assert.Null(Program.IdleEnterKindFor("groundsit", android: false, leashed: true, leashHold: false));
        var ip = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/IdlePose.cs"));
        Assert.Contains("static bool IdleLeashedNow() => leash != null || leashDriving || LeashParticleTarget() != null;", ip);
        Assert.DoesNotContain("IdlePoseKind(CollarVisibleNow(), LeashParticleTarget() != null", ip);
        Assert.Contains("LeashChainText(LeashParticleTarget() != null, LeashChainVisibleNow())", ip);
    }
    static string Root() { var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
}
