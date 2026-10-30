using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LibreMetaverse;

namespace GalatayText;

// 2026-10-12 David: after a short time offline (restart / deploy / reconnect, < 10 min since 2026-10-30, David: "10 minutes, not 30"; was 30 since the last heartbeat) log in
// at the last location; the configured start (home = the bedroom) only after a longer absence.
public static partial class Program
{
    static readonly string LastOnlineFile = Env("GT_LAST_ONLINE", "/home/box/viewers/textclient/run/last-online.txt");
    internal static readonly TimeSpan ShortAbsence = TimeSpan.FromMinutes(10);
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
    internal static TimeSpan? loginGap;   // time offline before this login (null = unknown)
    // pure (tested): re-seat / re-place / re-leash after a relog only when it was quick; collar restrictions are always kept
    internal static bool RestorePlacementAfterRelog(TimeSpan? gap) => gap != null && gap.Value >= TimeSpan.Zero && gap.Value < ShortAbsence;
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

    // 2026-10-27 David (08:43 login: "your skin was the first thing to load ... you stood there naked for a long time"): on a cold login the sim
    // restores one object per attach point itself and the library's outfit send adds the rest (Kay Body, hands, feet, head, hair sit on shared points)
    // one by one, in COF order. Order that send so what covers the system avatar goes first: body, then skeleton / head / hands / feet, then hair,
    // then everything else, with HUDs / AO / collar / bridge last. (Lower first; same rank keeps COF order.)
    internal static int OutfitSendRank(string name)
    {
        var n = name ?? "";
        if (System.Text.RegularExpressions.Regex.IsMatch(n, @"\b(HUD|AO|Collar|Bridge|Authorizer)\b|Premium Collar|ANIMATIONS", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return 8;
        if (n.Contains("Body", StringComparison.OrdinalIgnoreCase) && !n.Contains("Add-on", StringComparison.OrdinalIgnoreCase)) return 0;
        if (System.Text.RegularExpressions.Regex.IsMatch(n, @"\bHEAD\b|Skeleton|\b(Hand|Palm|Foot|Hip)[LR]?\b|Bare Foot|Kay HP")) return 1;
        if (n.Contains("hair", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }
}
