using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
namespace GalatayText.Tests;

// 2026-10-30 17:21:23 login: links removed +5140 ms, bake requested +6812 ms. The bake now goes beside the AIS delete.
public class LoginVeilSpecBakeTests
{
    [Fact] public async Task SpeculativeBake_RunsBesideTheDelete_FinalBakeSkippedWhenItCovers()
    {
        var steps = new List<string>(); var rm = new TaskCompletionSource<bool>();
        var t = LoginVeil.OffCore(null, () => rm.Task, () => { steps.Add("final"); return Task.CompletedTask; }, () => Task.FromResult(true), null,
            async pending => { steps.Add("spec-start"); Assert.False(pending.IsCompleted); rm.SetResult(true); await Task.CompletedTask; return true; }, () => false);
        var r = await t;
        Assert.True(r.removed); Assert.Equal(new[] { "spec-start" }, steps);
    }
    [Fact] public async Task SpeculativeBake_Failed_FinalBakeRuns()
    {
        var steps = new List<string>();
        await LoginVeil.OffCore(null, () => Task.FromResult(true), () => { steps.Add("final"); return Task.CompletedTask; }, () => Task.FromResult(true), null,
            _ => Task.FromResult(false), () => false);
        Assert.Equal(new[] { "final" }, steps);
    }
    [Fact] public async Task SpeculativeBake_AtAnOlderVersion_FinalBakeStillRuns()
    {
        var steps = new List<string>();
        await LoginVeil.OffCore(null, () => Task.FromResult(true), () => { steps.Add("final"); return Task.CompletedTask; }, () => Task.FromResult(true), null,
            _ => Task.FromResult(true), () => !LoginVeil.SpecCovers(2966, 2967));
        Assert.Equal(new[] { "final" }, steps);
        Assert.True(LoginVeil.SpecCovers(2967, 2967)); Assert.False(LoginVeil.SpecCovers(-1, 2967));
    }
    [Fact] public async Task DeleteFails_NoBakeAtAll()
    {
        var steps = new List<string>();
        var r = await LoginVeil.OffCore(null, () => Task.FromResult(false), () => { steps.Add("final"); return Task.CompletedTask; }, () => Task.FromResult(true), null,
            _ => Task.FromResult(false));
        Assert.False(r.removed); Assert.Empty(steps);
    }
}

// 2026-10-30 17:29:21 login: link delete back in 293 ms, but OffCore awaited the speculative POST still in flight (~3.4 s).
public class LoginVeilSpecNoWaitTests
{
    [Fact] public async Task PendingSpec_IsNotAwaited_FinalBakeGoesAtOnce()
    {
        var steps = new List<string>(); var never = new TaskCompletionSource<bool>(); var t0 = System.DateTime.Now;
        var r = await LoginVeil.OffCore(null, () => Task.FromResult(true), () => { steps.Add("final"); return Task.CompletedTask; }, () => Task.FromResult(true), null,
            _ => never.Task, () => true);
        Assert.True(r.removed); Assert.Equal(new[] { "final" }, steps);
        Assert.True((System.DateTime.Now - t0).TotalSeconds < 1);
    }
}

// 2026-10-30 17:33:25 login: first Current Outfit read at +0.0 s had 0 links; no re-read for 5 s, release took the slow path.
public class LoginVeilLinkReadTests
{
    [Fact] public void NoVeilLinkYet_ReadsAgainEverySecond()
    {
        Assert.True(LoginVeil.CofRefreshDue(false, 1.2, false));
        Assert.False(LoginVeil.CofRefreshDue(true, 1.2, false));
        Assert.True(LoginVeil.CofRefreshDue(true, 5.1, false));
    }
}
