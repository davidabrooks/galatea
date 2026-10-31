using GalatayText;
using Xunit;

// 2026-10-31 14:47 login (David: "it took too long for your collar to appear, and then it was even longer before your ears appeared"):
// collar Stealth off at +22.5 s, EvoX ears/lashes at ~+34 s (HUD handshake "please wear your head" + re-attach after the veil-off).
public class LoginRevealOrderTests
{
    [Fact] public async Task Collar_WaitsForTheEarsAndLashesStep_SoItIsLast()
    {
        var steps = new List<string>();
        await LoginVeil.RevealCore(async () => { await Task.Delay(150); steps.Add("ears"); }, 5000, () => { steps.Add("collar"); return Task.CompletedTask; });
        Assert.Equal(new[] { "ears", "collar" }, steps);
    }
    [Fact] public async Task StuckEarsStep_DoesNotHoldTheCollarPastItsCap()
    {
        var steps = new List<string>(); var t0 = DateTime.UtcNow;
        await LoginVeil.RevealCore(() => Task.Delay(10000), 200, () => { steps.Add("collar"); return Task.CompletedTask; });
        Assert.Equal(new[] { "collar" }, steps);
        Assert.True((DateTime.UtcNow - t0).TotalSeconds < 3);
    }
    [Fact] public async Task ThrowingEarsStep_StillShowsTheCollar()
    {
        bool collar = false;
        await LoginVeil.RevealCore(() => throw new InvalidOperationException(), 1000, () => { collar = true; return Task.CompletedTask; });
        Assert.True(collar);
    }
    [Fact] public void SafetyNet_WaitsWhileTheVeilOffRevealIsRunning_UntilTheCap()
    {   // 14:47: the safety net fired at +16 s mid-reveal ("head not worn") and competed for the HUD
        Assert.False(EvoxHide.SafetyRestoreDue(true, false, 16, revealPending: true));
        Assert.True(EvoxHide.SafetyRestoreDue(true, false, 16, revealPending: false));
        Assert.True(EvoxHide.SafetyRestoreDue(true, true, EvoxHide.SafetyNetSec, revealPending: true));
    }
    [Fact] public void EvoxHead_FoundByItemIdBeforeItsNameLoads()
    {
        // 2026-10-31 15:15 login: head attached at +4 s, no prim name yet; the prep waited and the HUD came after the veil-off
        Assert.True(EvoxHide.IsEvoxHead(true, null));
        Assert.True(EvoxHide.IsEvoxHead(false, "/ HEAD / lel evox / AVALON 4.0"));
        Assert.False(EvoxHide.IsEvoxHead(false, null));
        Assert.False(EvoxHide.IsEvoxHead(false, "/ HUD / lel evox (f) 4.0"));
        Assert.InRange(EvoxHide.VeilHoldMs, 1000, 5000);
    }
    [Fact] public void HudPrep_RetriesNoHeadOnlyUnderTheVeil()
    {
        Assert.True(EvoxHide.PrepRetryDue(false, true, 1, EvoxHide.PrepMaxAttempts));
        Assert.False(EvoxHide.PrepRetryDue(true, true, 1, EvoxHide.PrepMaxAttempts));
        Assert.False(EvoxHide.PrepRetryDue(false, false, 1, EvoxHide.PrepMaxAttempts));   // veil off: the reveal does it
        Assert.False(EvoxHide.PrepRetryDue(false, true, EvoxHide.PrepMaxAttempts, EvoxHide.PrepMaxAttempts));
        Assert.True(EvoxHide.NoHeadWaitMs <= 3000);
    }
    [Fact] public void PreOpenedCollarPage_UsedOnlyForTheSameWantAndWhileFresh()
    {
        var now = DateTime.Now;
        var p = new Program.CollarPrepared(null, "☑ Stealth", true, false, now.AddSeconds(-10));
        Assert.True(Program.CollarPrepUsable(p, false, now));
        Assert.False(Program.CollarPrepUsable(p, true, now));
        Assert.False(Program.CollarPrepUsable(p with { At = now.AddSeconds(-Program.CollarPrepMaxAgeSec - 1) }, false, now));
        Assert.False(Program.CollarPrepUsable(null, false, now));
    }
}
