// LoginRequest.cs (2026-11-20): the 10:16:03 PT auto-relogin hung in the XML-RPC POST to login.agni. The box VM was frozen
// ~31 s mid-request (fork 127->128, logged 10:16:46), the open HTTPS connection did not survive it, and LibreMetaverse only
// gave up at its overall 60 s LoginTimeout ("Login canceled" at 10:17:34). Now each attempt has its own limit on the
// login-server reply (GT_LOGIN_POST_TIMEOUT_S, default 22 s) and one immediate retry, and a VM fork that lands while the
// request is in flight cancels it at once (VmForkWatch.cs) for one more immediate retry.
// Double-login guard: only a request with NO reply yet is ever cancelled (once the login server has answered, the sim connect
// runs on as before); the retry runs inside the same LoginCoreAsync, so loginGate still allows one login at a time; and
// after a cancelled request the grid may have completed that login, so a "presence / already logged in" refusal is waited
// out like a ghost session (ShouldRetryGhostLogin, 20 s steps). deliberate-stop: no retry.
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal static readonly int LoginPostTimeoutS =
        int.TryParse(Environment.GetEnvironmentVariable("GT_LOGIN_POST_TIMEOUT_S"), out var lpt) && lpt >= 5 && lpt <= 55 ? lpt : 22;
    static readonly object loginAttemptLock = new();
    static CancellationTokenSource loginAttemptCts;   // the running attempt; null between attempts
    static volatile bool loginAttemptReplied;         // the login server answered (status moved past ConnectingToLogin)
    static volatile string loginAttemptCancel;        // why we cancelled it: "timeout: ..." or "fork: ..."

    /// <summary>Pure (tested): did the login server answer? (any status after ConnectingToLogin except a failure)</summary>
    internal static bool LoginReplied(LoginStatus status) =>
        status is LoginStatus.ReadingResponse or LoginStatus.ConnectingToSim or LoginStatus.Redirecting or LoginStatus.Success;

    /// <summary>Pure (tested): retry a login attempt that we cancelled? Returns the cause to count ("timeout" / "fork") or null.
    /// One immediate retry per cause; never with deliberate-stop; never for a failure we did not cause.</summary>
    internal static string LoginRequestRetry(string cancelled, int timeoutRetries, int forkRetries, bool deliberateStop)
    {
        if (deliberateStop || string.IsNullOrEmpty(cancelled)) return null;
        if (cancelled.StartsWith("timeout", StringComparison.Ordinal)) return timeoutRetries < 1 ? "timeout" : null;
        if (cancelled.StartsWith("fork", StringComparison.Ordinal)) return forkRetries < 1 ? "fork" : null;
        return null;
    }

    /// <summary>Cancels the in-flight login request if the login server has not answered yet. True = cancelled.</summary>
    internal static bool CancelLoginRequest(string why)
    {
        CancellationTokenSource cts;
        lock (loginAttemptLock)
        {
            cts = loginAttemptCts;
            if (cts == null || loginAttemptReplied || loginAttemptCancel != null) return false;
            loginAttemptCancel = why;
        }
        try { cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    /// <summary>One LibreMetaverse login under our own cancellation. Returns (ok, why we cancelled it or null).</summary>
    static async Task<(bool ok, string cancelled)> LoginAttemptAsync(LoginParams lp)
    {
        var cts = new CancellationTokenSource();
        using var timerCts = new CancellationTokenSource();
        lock (loginAttemptLock) { loginAttemptCts = cts; loginAttemptReplied = false; loginAttemptCancel = null; }
        EventHandler<LoginProgressEventArgs> onProgress = (s, e) => { if (LoginReplied(e.Status)) loginAttemptReplied = true; };
        var net = client.Network;
        net.LoginProgress += onProgress;
        _ = Task.Delay(TimeSpan.FromSeconds(LoginPostTimeoutS), timerCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled && CancelLoginRequest($"timeout: no reply from the login server in {LoginPostTimeoutS} s"))
                Log("login", $"no reply from the login server in {LoginPostTimeoutS} s: cancelling the request");
        }, TaskScheduler.Default);
        try
        {
            var ok = await net.LoginAsync(lp, cts.Token);
            return (ok, ok ? null : loginAttemptCancel);
        }
        finally
        {
            try { timerCts.Cancel(); } catch { }
            net.LoginProgress -= onProgress;
            lock (loginAttemptLock) { if (loginAttemptCts == cts) loginAttemptCts = null; }
            cts.Dispose();
        }
    }
}
