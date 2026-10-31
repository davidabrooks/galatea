using GalatayText;
using Xunit;

// 2026-10-30 relog after #355 (David: "your ears were floating along with your nails and eyelashes"). Face data read in-world 16:1x from
// '/ HEAD / lel evox / AVALON 4.0': ears = 'HeadHD' faces 1, 2; lashes = 'ESL' / 'ESR' faces 4, 5 (alpha 0 = hidden by the HUD).
public class LoginVeilEvoxTests
{
    static List<(string, int, float)> Head(bool ears, bool lashes) => new()
    {
        ("HeadHD", 0, 1f), ("HeadHD", 1, ears ? 0f : 1f), ("HeadHD", 2, ears ? 0f : 1f), ("HeadHD", 3, 1f),
        ("ESL", 3, 0f), ("ESL", 4, lashes ? 0f : 1f), ("ESL", 5, lashes ? 0f : 1f),
        ("ESR", 3, 0f), ("ESR", 4, lashes ? 0f : 1f), ("ESR", 5, lashes ? 0f : 1f),
    };

    [Fact] public void Hidden_ReadsEarsAndLashesFromTheHeadFaces()
    {
        Assert.Equal(new EvoxHide.Parts(false, false), EvoxHide.Hidden(Head(false, false)));
        Assert.Equal(new EvoxHide.Parts(true, false), EvoxHide.Hidden(Head(true, false)));
        Assert.Equal(new EvoxHide.Parts(false, true), EvoxHide.Hidden(Head(false, true)));
    }
    [Fact] public void Hidden_OneLashSegmentHidden_IsNotAllLashesHidden()
    {   // 16:1x: 'LashesTB' face 0 hid only ESR face 5
        var h = Head(false, false); h[^1] = ("ESR", 5, 0f);
        Assert.False(EvoxHide.Hidden(h).Lashes);
    }
    [Fact] public void Hidden_UnknownHead_IsNull_SoNothingIsPressed()
    {
        Assert.Null(EvoxHide.Hidden(new[] { ("Head", 0, 1f) }));
        Assert.Empty(EvoxHide.Presses(null, new EvoxHide.Parts(true, true)));
    }
    [Fact] public void Presses_AreToggles_OnlyForPartsNotAlreadyInTheWantedState()
    {
        var p = EvoxHide.Presses(new EvoxHide.Parts(false, false), new EvoxHide.Parts(true, true));
        Assert.Equal(new[] { (EvoxHide.EarsButton, 1, "ears"), (EvoxHide.LashesButton, 4, "lashes") }, p);
        // the android look already hides the ears: pressing again would SHOW them
        Assert.Equal(new[] { "lashes" }, EvoxHide.Presses(new EvoxHide.Parts(true, false), new EvoxHide.Parts(true, true)).Select(x => x.what));
        Assert.Empty(EvoxHide.Presses(new EvoxHide.Parts(true, true), new EvoxHide.Parts(true, true)));
    }
    [Fact] public void Marker_NamesOnlyWhatWeHid_AndLoginShowsOnlyThat()
    {
        Assert.Equal("ears,lashes", EvoxHide.Marker(new(false, false), new(true, true)));
        Assert.Equal("lashes", EvoxHide.Marker(new(true, false), new(true, true)));   // android: ears were hidden before, they stay hidden
        Assert.Equal("", EvoxHide.Marker(new(false, false), new(false, false)));      // press failed: nothing to undo
        // 13:56 broken-session logout: head unseen must keep the earlier marker, not wipe it
        Assert.False(EvoxHide.MarkerReplaces(null, null));
        Assert.False(EvoxHide.MarkerReplaces(new(false, false), null));
        Assert.True(EvoxHide.MarkerReplaces(new(false, false), new(true, true)));
        Assert.Equal(new EvoxHide.Parts(true, false), EvoxHide.ShowTarget("lashes", new(true, true)));
        Assert.Equal(new EvoxHide.Parts(false, false), EvoxHide.ShowTarget("ears,lashes\n", new(true, true)));
        Assert.Empty(EvoxHide.Presses(new(false, false), EvoxHide.ShowTarget("ears,lashes", new(false, false))));
    }
    [Fact] public void SafetyNet_RestoresOnceTheVeilIsGone_OrAfterTheCap_NeverEarless()
    {
        Assert.False(EvoxHide.SafetyRestoreDue(true, true, 10));
        Assert.True(EvoxHide.SafetyRestoreDue(true, false, 10));
        Assert.True(EvoxHide.SafetyRestoreDue(true, true, EvoxHide.SafetyNetSec));
        Assert.False(EvoxHide.SafetyRestoreDue(false, false, 999));
    }
    [Fact] public async Task Logout_EarsLashesAndCollar_BeforeTheHairWave_HairWaveRightBeforeVeil()
    {
        var steps = new List<string>();
        var ok = await LoginVeil.LogoutCore(() => { steps.Add("collar"); return Task.CompletedTask; }, 1000,
            () => { steps.Add("veil"); return Task.FromResult(true); }, () => { steps.Add("stash"); return Task.CompletedTask; },
            () => { steps.Add("hair"); return Task.CompletedTask; }, () => { steps.Add("ears-lashes"); return Task.CompletedTask; });
        Assert.True(ok);
        Assert.Equal(new[] { "ears-lashes", "collar", "hair", "veil", "stash" }, steps);
    }
    [Fact] public async Task Logout_StuckHud_DoesNotHoldTheLogout()
    {
        var steps = new List<string>(); var t0 = DateTime.Now;
        await LoginVeil.LogoutCore(null, 0, () => { steps.Add("veil"); return Task.FromResult(true); }, () => Task.CompletedTask, null,
            () => Task.Delay(-1), hideHeadFacesMaxMs: 300);
        Assert.Equal(new[] { "veil" }, steps);
        Assert.True((DateTime.Now - t0).TotalSeconds < 5);
    }
    [Fact] public async Task Logout_HudThrows_LogoutGoesOn()
    {
        var ok = await LoginVeil.LogoutCore(null, 0, () => Task.FromResult(true), () => Task.CompletedTask, null, () => throw new Exception("hud"));
        Assert.True(ok);
    }
}

// 2026-10-30 16:44/16:48/17:00/17:20 logouts: the HUD said "please wear your head to activate me." and ignored the presses (NOT confirmed).
public class EvoxHudHandshakeTests
{
    [Fact] public void NoHeadLine_Detected()
    {
        Assert.True(EvoxHide.IsNoHeadLine("Hello Galatea, please wear your head to activate me."));
        Assert.False(EvoxHide.IsNoHeadLine("Ready"));
    }
    [Fact] public void Retry_UntilConfirmed_CappedByTriesAndTime()
    {
        Assert.True(EvoxHide.RetryDue(false, 1, EvoxHide.MaxAttempts, 5, EvoxHide.BudgetSec));
        Assert.False(EvoxHide.RetryDue(true, 1, EvoxHide.MaxAttempts, 5, EvoxHide.BudgetSec));
        Assert.False(EvoxHide.RetryDue(false, EvoxHide.MaxAttempts, EvoxHide.MaxAttempts, 5, EvoxHide.BudgetSec));
        Assert.False(EvoxHide.RetryDue(false, 1, EvoxHide.MaxAttempts, EvoxHide.BudgetSec, EvoxHide.BudgetSec));
        Assert.True(EvoxHide.BudgetSec < LoginVeil.EvoxHideMaxMs / 1000.0);   // the logout cap still wins
    }

    // 2026-10-31 15:02 login: the prep's HUD item fetch hung 15 s ("HUD not in inventory" after the veil-off): ears came at +24 s.
    [Fact] public void HudRecord_IsBuiltFromTheKnownIdWithoutAFetch()
    {
        var id = new LibreMetaverse.UUID("80597a5b-5254-386b-ad5e-6c260f65c8eb"); var owner = LibreMetaverse.UUID.Random();
        var r = EvoxHide.HudRecord(id, owner);
        Assert.Equal(id, r.UUID); Assert.Equal(owner, r.OwnerID); Assert.False(r.IsLink());
        Assert.Contains("lel evox", r.Name);
    }
    [Fact] public void EvoxCode_NeverFetchesTheHudItem()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "galatay-text", "LoginVeilEvox.cs"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        Assert.DoesNotContain("FetchItemRO(EvoxHudItem", File.ReadAllText(Path.Combine(dir, "galatay-text", "LoginVeilEvox.cs")));
    }
}
