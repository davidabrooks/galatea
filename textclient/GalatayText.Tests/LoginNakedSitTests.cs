using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GalatayText;
using Xunit;
// 2026-11-02 11:09 login (David): no veil (the Oct 31 23:17 logout lost the network during an 11 s collar wait, before the veil step) and
// already in the AO's force ground sit, played 'from object childprim' e6a05fec (a child prim of the AO HUD) that IsAoHudSource missed.
public class LoginNakedSitTests
{
    static string Root() { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
    static string Src(string f) => File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text", f));

    const uint Self = 100, HudRoot = 200, HudChild = 201, HudGrandchild = 202, World = 300;
    static uint? Parent(uint l) => l switch { HudRoot => Self, HudChild => HudRoot, HudGrandchild => HudChild, World => 0u, Self => 0u, _ => null };

    [Fact] public void AChildPrimOfMyHud_ResolvesToTheAttachmentRoot()
    {
        Assert.Equal(HudRoot, Program.AttachRootLocal(HudRoot, Parent, Self));
        Assert.Equal(HudRoot, Program.AttachRootLocal(HudChild, Parent, Self));       // the 11:09 case: parent is the HUD root, not me
        Assert.Equal(HudRoot, Program.AttachRootLocal(HudGrandchild, Parent, Self));
        Assert.Equal(0u, Program.AttachRootLocal(World, Parent, Self));               // an in-world object is not mine
        Assert.Equal(0u, Program.AttachRootLocal(999, Parent, Self));                 // unknown prim
        Assert.Equal(0u, Program.AttachRootLocal(5, l => l == 5 ? 6u : 5u, Self));    // a parent loop never hangs
    }
    [Fact] public void IsAoHudSource_AndIsOwnNonAo_NoLongerRequireParentToBeMe()
    {
        var s = Src("AttachWatch.cs");
        Assert.DoesNotContain("p.ParentID != client.Self.LocalID", s);
        Assert.DoesNotContain("p.ParentID == client.Self.LocalID && !IsAoHudSource", s);
        Assert.Contains("AttachRootPrim(src)", s);
    }
    [Fact] public void AnUnlistedAnimationFromMyAttachment_IsUnknown_NeverStand()
    {
        Assert.True(Program.AoUnknownCandidate(true, true, false));
        Assert.True(Program.AoUnknownCandidate(false, true, false));    // childprim of an attachment, AO not recognised: still unknown
        Assert.False(Program.AoUnknownCandidate(false, true, true));    // a known collar pose
        Assert.False(Program.AoUnknownCandidate(false, false, false));  // furniture / another avatar's object
    }
    [Fact] public void AlreadyOff_NeedsAFreshStandingRenderOrAFullyClassifiedList()
    {
        Assert.Null(Program.AoOffVerdict(true, true, false));
        Assert.Null(Program.AoOffVerdict(false, false, true));
        Assert.StartsWith("NOT VERIFIED", Program.AoOffVerdict(false, false, false));
        Assert.Contains("no fresh render", Program.AoOffVerdict(false, false, false));
    }
    [Fact] public async Task LogoutDisconnectAfterTheCollar_SkipsNothingSilently_AndKeepsCollarFirst()
    {
        var steps = new List<string>(); string missed = null; bool up = true;
        var ok = await LoginVeil.LogoutCore(() => { steps.Add("collar"); up = false; return Task.CompletedTask; }, 1000,
            () => { steps.Add("veil"); return Task.FromResult(true); }, () => { steps.Add("stash"); return Task.CompletedTask; },
            connected: () => up, veilMissed: s => missed = s);
        Assert.False(ok); Assert.Equal(new[] { "collar" }, steps); Assert.NotNull(missed);
        steps.Clear(); missed = null; up = true;
        ok = await LoginVeil.LogoutCore(() => { steps.Add("collar"); return Task.CompletedTask; }, 1000,
            () => { steps.Add("veil"); return Task.FromResult(true); }, () => { steps.Add("stash"); return Task.CompletedTask; },
            connected: () => up, veilMissed: s => missed = s);
        Assert.True(ok); Assert.Equal(new[] { "collar", "veil", "stash" }, steps); Assert.Null(missed);
    }
    [Fact] public async Task AStuckCollarMenu_CannotDelayTheVeilPastAbout3s()
    {
        Assert.InRange(LoginVeil.CollarStealthMaxMs, 1000, 3500);
        Assert.True(LoginVeil.CollarLogoutTouches >= 2);
        var sw = System.Diagnostics.Stopwatch.StartNew(); var steps = new List<string>();
        await LoginVeil.LogoutCore(() => new TaskCompletionSource().Task, LoginVeil.CollarStealthMaxMs, () => { steps.Add("veil"); return Task.FromResult(true); }, () => Task.CompletedTask);
        Assert.Equal(new[] { "veil" }, steps); Assert.InRange(sw.ElapsedMilliseconds, 0, LoginVeil.CollarStealthMaxMs + 1500);
    }
    [Fact] public void AMissedVeilOrAMissingVeilLink_HoldsTheReleaseForRealReady()
    {
        Assert.True(LoginVeil.VeilMissedHold(true, false, false));
        Assert.True(LoginVeil.VeilMissedHold(false, true, false));     // the 11:09 login: COF read, veil link not found
        Assert.False(LoginVeil.VeilMissedHold(false, true, true));
        Assert.False(LoginVeil.VeilMissedHold(false, false, false));   // COF not read yet: the normal waits apply
        // a known-silent garment no longer shortens the wait when held
        const string J = "Maitreya LaraX Vintage Jeans Low-Waist #3 (Flat)"; Assert.True(LoginVeil.Waits(J));
        var g = new[] { new LoginVeil.Garment(J, 0.5, null) };
        var silent = new HashSet<string> { J };
        Assert.True(LoginVeil.Release(g, LoginVeil.KnownSilentTimeoutSec + 1, false, silent).release);
        Assert.False(LoginVeil.Release(g, LoginVeil.KnownSilentTimeoutSec + 1, false, null).release);
    }
    [Fact] public void TheMarkerIsWrittenOnAMidLogoutDisconnect_AndReadAtLogin()
    {
        var v = Src("LoginVeil.cs"); var p = Src("Program.cs");
        Assert.Contains("veilMissed: LoginVeilMissedWrite", v);
        Assert.Contains("File.Exists(LoginVeilMissedPath)", v);
        Assert.Contains("LoginVeilMissedWrite(", p);
    }
}
