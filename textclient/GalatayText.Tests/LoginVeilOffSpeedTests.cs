using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
namespace GalatayText.Tests;

// 2026-10-30 17:00:40 / 17:04:03 logins: garments Ready +7.0 s, veil off +19.2 / +15.9 s. (1) RemoveFromOutfit before the links armed the
// library's delayed bake, which raced our bake POST (4.2 s, once a 5 s timeout). (2) the release loop sat in a COF re-read for ~3 s.
public class LoginVeilOffSpeedTests
{
    [Fact] public async Task LayerDrop_ComesAfterOurBakePost_SoTheLibraryBakeCannotRaceIt()
    {
        var steps = new List<string>();
        await LoginVeil.OffCore(() => steps.Add("drop"), () => { steps.Add("links"); return Task.FromResult(true); },
            () => { steps.Add("bake"); return Task.CompletedTask; }, () => { steps.Add("confirm"); return Task.FromResult(true); });
        Assert.True(steps.IndexOf("drop") > steps.IndexOf("bake"));
        Assert.True(steps.IndexOf("drop") < steps.IndexOf("confirm"));
    }
    [Fact] public async Task LinkRemovalFails_LayerStillDropped_NoBake()
    {
        var steps = new List<string>();
        var r = await LoginVeil.OffCore(() => steps.Add("drop"), () => Task.FromResult(false), () => { steps.Add("bake"); return Task.CompletedTask; }, () => Task.FromResult(true));
        Assert.Equal(new[] { "drop" }, steps); Assert.False(r.removed);
    }
    [Fact] public void CofRefresh_RunsBesideTheLoop_NeverTwiceAtOnce()
    {
        Assert.False(LoginVeil.CofRefreshDue(true, 6, inFlight: true));
        Assert.True(LoginVeil.CofRefreshDue(true, 6, inFlight: false));
        Assert.False(LoginVeil.CofRefreshDue(true, 3, inFlight: false));
        Assert.True(LoginVeil.CofRefreshDue(false, 1.5, inFlight: false));
    }
}
