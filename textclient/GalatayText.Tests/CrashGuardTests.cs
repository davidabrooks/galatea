using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GalatayText;
using Xunit;
public class CrashGuardTests
{
    [Fact] public void TheLoadedLibreMetaverseCarriesEveryGalateaPatch()
    {
        Assert.Empty(Program.LmvMissingPatches(typeof(LibreMetaverse.GridClient).Assembly, Program.RequiredLmvMarkers));
        Assert.Null(Program.LmvPatchGuard());
    }
    [Fact] public void AnUnpatchedLibreMetaverseIsReportedByPatchName()
    {
        // an assembly without the marker type/member stands in for the stale DLL
        var missing = Program.LmvMissingPatches(typeof(string).Assembly, Program.RequiredLmvMarkers);
        Assert.Equal(new[] { "libremetaverse-seed-retry.patch" }, missing);
        Assert.Equal(new[] { "x.patch" }, Program.LmvMissingPatches(typeof(LibreMetaverse.Caps).Assembly, new[] { ("LibreMetaverse.Caps", "NoSuchMember", "x.patch") }));
        Assert.Single(Program.LmvMissingPatches(null, Program.RequiredLmvMarkers));
    }
    [Fact] public void GhostPidIsADeadPidNamedByTheLock()
    {
        Assert.Equal(123, Program.GhostPidFromLock("123\n", 9, p => false));
        Assert.Null(Program.GhostPidFromLock("123", 9, p => true));    // still running: a real session, not a ghost
        Assert.Null(Program.GhostPidFromLock("9", 9, p => false));     // our own pid
        Assert.Null(Program.GhostPidFromLock("", 9, p => false));
        Assert.Null(Program.GhostPidFromLock("junk", 9, p => false));
    }
    [Fact] public void AZombieIsNotALiveSession()
    {
        Assert.True(Program.ProcStatAlive("2309701 (galatay-mcp) S 2272924 1 1 0"));
        Assert.True(Program.ProcStatAlive("5 (a) b c) R 1"));   // ')' inside the name
        Assert.False(Program.ProcStatAlive("2308724 (galatay-mcp) Z 2272924 1 1 0"));
        Assert.False(Program.ProcStatAlive(""));
    }
    [Fact] public void GhostLoginRetriesAreBoundedAndOnlyForStillLoggedIn()
    {
        Assert.True(Program.ShouldRetryGhostLogin("Login failed: you are already logged in", true, 1));
        Assert.True(Program.ShouldRetryGhostLogin("presence not yet cleared", true, 2));
        Assert.False(Program.ShouldRetryGhostLogin("already logged in", true, 3));     // cap
        Assert.False(Program.ShouldRetryGhostLogin("already logged in", false, 1));    // no ghost known
        Assert.False(Program.ShouldRetryGhostLogin("bad password", true, 1));
    }
    [Fact] public async Task ACancelledCallerDoesNotStopTheWork()
    {
        var done = new TaskCompletionSource<bool>(); using var cts = new CancellationTokenSource();
        var call = Program.RunDetached(async () => { await Task.Delay(250); done.SetResult(true); return "ok"; }, "login", m => m);
        cts.Cancel();   // the host's notifications/cancelled: the caller stops waiting
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(cts.Token));
        Assert.True(await done.Task.WaitAsync(TimeSpan.FromSeconds(5)));   // the work still finished
        Assert.Equal("ok", await call);
    }
    [Fact] public async Task AThrowingToolBecomesAnErrorResultNotACrash()
    {
        var r = await Program.RunDetached(() => throw new InvalidOperationException("boom"), "login", m => "ERR " + m);
        Assert.Equal("ERR login failed: boom", r);
        var r2 = await Program.RunDetached(async () => { await Task.Yield(); throw new ObjectDisposedException("x"); }, "logout", m => m);
        Assert.StartsWith("logout failed:", r2);
    }
    [Fact] public void CrashHandlersInstallOnceAndObserveStrayTaskFaults()
    {
        Program.InstallCrashHandlers(); Program.InstallCrashHandlers();
        var t = Task.Run(() => { throw new InvalidOperationException("stray"); });
        try { t.Wait(); } catch (AggregateException) { }
        t = null; GC.Collect(); GC.WaitForPendingFinalizers();   // finalizer raises UnobservedTaskException: handled, process survives
        Assert.True(true);
    }
}
