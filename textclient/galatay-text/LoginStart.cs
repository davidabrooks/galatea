using System;
using System.Globalization;
using System.IO;

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
}
