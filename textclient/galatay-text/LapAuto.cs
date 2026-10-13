using LibreMetaverse;
using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GalatayText;

// 2026-10-13 David: "sit on my lap" / "come sit on my lap" / "on my lap" from David (nearby chat or IM) runs the sit_lap
// flow by itself (bedroom + bikini first when he is on the beach, path-graph routes, lap pose table). The line still goes
// to the "Second Life chat" webhook, marked auto_action, so the routine only talks and never sits me on his chair again.
public static partial class Program
{
    static readonly Regex LapRx = new(@"(?<![a-z])(?:on|in|onto|into)\s+(?:my|ur|your)\s+lap(?![a-z])|(?<![a-z])(?:my|me)\s+lap(?![a-z])|lap\s*(?:sit|time)|sit\s+on\s+me(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex LapNegRx = new(@"(?<![a-z])(don'?t|do\s+not|no\s+more|stop|get\s+off|off\s+(?:of\s+)?my\s+lap|not\s+now|later|never)(?![a-z])|\?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // pure: a clear request from David to sit on his lap (questions like "did you like my lap?" and "get off my lap" are not)
    internal static bool IsLapRequest(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || t.Length > 160) return false;
        if (!LapRx.IsMatch(t)) return false;
        if (LapNegRx.IsMatch(t) && !Regex.IsMatch(t, @"(?<![a-z])(come|sit|hop|climb|get)\b[^?]*lap", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(t, @"(?<![a-z])(don'?t|do\s+not|stop|get\s+off)(?![a-z])", RegexOptions.IgnoreCase)) return false;
        return true;
    }

    static readonly ConcurrentDictionary<long, string> autoActions = new();
    public static string AutoActionFor(long id) => autoActions.TryGetValue(id, out var a) ? a : null;
    static DateTime lapAutoAt = DateTime.MinValue;

    // called from the chat / IM hooks BEFORE WanderChatIn; true = handled (the come-request approach is skipped)
    static bool LapRequestIn(UUID id, string text, long? msgId, bool im)
    {
        if (id != DavidId || !IsLapRequest(text)) return false;
        if ((DateTime.UtcNow - lapAutoAt).TotalSeconds < 20) { if (msgId is long m0) autoActions[m0] = "sit_lap already running"; return true; }
        lapAutoAt = DateTime.UtcNow;
        if (msgId is long m) autoActions[m] = "the client is already doing this by itself: sit_lap David Nightingale (bedroom/bikini first if he is on the beach, then his seat + a lap pose). Do NOT sit_near / sit / move her; only reply in chat";
        Log("lap", $"lap request from David ({(im ? "IM" : "chat")}): '{text}' -> sit_lap");
        _ = Task.Run(async () =>
        {
            try
            {
                if (WanderOn && wanderPause is not "hold") Log("lap", "wander: " + await Exec("wander hold"));
                if (walkTask != null && !walkTask.IsCompleted) { walkCts?.Cancel(); await Task.Delay(800); }
                Log("lap", "sit_lap: " + await SitLapCmd("David Nightingale"));
            }
            catch (Exception ex) { Log("lap", "auto sit_lap error: " + ex.GetBaseException().Message); }
        });
        return true;
    }
}
