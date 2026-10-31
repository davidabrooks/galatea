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
            async pending => { steps.Add("spec-start"); Assert.False(pending.IsCompleted); rm.SetResult(true); await Task.Yield(); return true; }, () => false);
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
