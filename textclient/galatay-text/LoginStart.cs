using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LibreMetaverse;

namespace GalatayText;

// 2026-10-12 David: after a short time offline (restart / deploy / reconnect, < 30 min since the last heartbeat) log in
// at the last location; the configured start (home = the bedroom) only after a longer absence.
public static partial class Program
{
    static readonly string LastOnlineFile = Env("GT_LAST_ONLINE", "/home/box/viewers/textclient/run/last-online.txt");
    internal static readonly TimeSpan ShortAbsence = TimeSpan.FromMinutes(30);
    static DateTime lastOnlineWrite = DateTime.MinValue;

    // pure
    internal static string ChooseLoginStart(string configured, DateTime? lastOnlineUtc, DateTime nowUtc)
    {
        if (lastOnlineUtc != null && nowUtc - lastOnlineUtc.Value >= TimeSpan.Zero && nowUtc - lastOnlineUtc.Value < ShortAbsence) return "last";
        return configured;
    }

    static DateTime? ReadLastOnline()
    {
        try { var t = File.ReadAllText(LastOnlineFile).Trim(); return DateTime.Parse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal); }
        catch { return null; }
    }

    // heartbeat: called from the 1 s ticker while logged in (writes once a minute) and at logout
    static void LastOnlineHeartbeat(bool force = false)
    {
        if (!LoggedIn && !force) return;
        var now = DateTime.UtcNow; if (!force && now - lastOnlineWrite < TimeSpan.FromSeconds(60)) return;
        lastOnlineWrite = now;
        try { File.WriteAllText(LastOnlineFile, now.ToString("o", CultureInfo.InvariantCulture)); } catch { }
    }

    // 2026-10-12 16:05 David: after a quick relog that was seated, do NOT re-sit or re-pick a pose: log in, step to the
    // clear floor spot beside that furniture (stand_spot / approach / change_spot) and stand; wander carries on from there.
    static bool quickLogin;
    internal static HomeSeatInfo QuickReloginSeatNear(Vector3 me, IEnumerable<HomeSeatInfo> seats, float within = 2.5f)
        => (seats ?? Enumerable.Empty<HomeSeatInfo>())
            .Where(x => (x.StandSpot ?? x.Approach ?? x.ChangeSpot) != null && HDist(me, x.Pos) <= within)
            .OrderBy(x => HDist(me, x.Pos)).FirstOrDefault();

    static async Task AfterQuickLogin()
    {
        if (!quickLogin) return; quickLogin = false;
        try
        {
            await Task.Delay(15000);
            if (!LoggedIn || !InPeronaut || client.Self.SittingOn != 0) return;
            var me = client.Self.SimPosition;
            var seat = QuickReloginSeatNear(me, LoadHomeSeats());
            if (seat == null) { Log("login", $"quick relog at {me.X:F1},{me.Y:F1}: no furniture right here; standing where she is (no re-sit)"); return; }
            Log("login", $"quick relog beside '{seat.Name}': stepping to its floor spot, no re-sit");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await StandOutAfterSeat(seat with { StandSpot = seat.StandSpot ?? seat.Approach ?? seat.ChangeSpot }, cts.Token);
        }
        catch (Exception ex) { Log("login", "quick relog step-out: " + ex.GetBaseException().Message); }
    }
}
