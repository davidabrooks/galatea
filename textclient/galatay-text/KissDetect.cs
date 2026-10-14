using LibreMetaverse;
using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GalatayText;

// 2026-10-12 David: notice when David kisses her. The Real Kisser announces every kiss in nearby chat from an object
// ("★ Pretty Liars ★ David Nightingale kisses Galatea Nightingale's lips.", fancy math-bold letters): NFKC folds them to
// plain text. A kiss from David on her raises a "kissed" webhook event; the chat routine decides (kiss back = 'kiss',
// with the cooldown in the context, or a sweet IM).
public static partial class Program
{
    static readonly Regex KissLine = new(@"(?<who>[A-Za-z][\w.\- ]*?)\s+kisses\s+(?<whom>[A-Za-z][\w.\- ]*?)'s\s+(?<part>lips|cheek|neck|forehead|hand|\w+)", RegexOptions.IgnoreCase);
    internal static readonly TimeSpan KissBackCooldown = TimeSpan.FromMinutes(10);
    static DateTime kissedLastEvent = DateTime.MinValue;

    // pure: (who, whom, part) from a Kisser chat line, or null
    internal static (string who, string whom, string part)? ParseKissLine(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return null;
        var t = msg.Normalize(NormalizationForm.FormKC);
        var m = KissLine.Match(t); if (!m.Success) return null;
        var who = m.Groups["who"].Value.Trim(); int star = who.LastIndexOf('★'); if (star >= 0) who = who[(star + 1)..].Trim();
        return (who, m.Groups["whom"].Value.Trim(), m.Groups["part"].Value.ToLowerInvariant());
    }

    // pure: an event only for David kissing her (not her own kiss, not other couples), at most once per 20 s
    // 2026-10-13: the Kisser names display names ("Galatea Nightingale"), not my legacy "Galatay Resident": 20:05:01 never matched
    internal static bool IsKissOnMe((string who, string whom, string part)? k, string myName, string davidName, DateTime now, DateTime lastEvent)
        => k != null && davidName != null && NameIsMe(k.Value.whom, myName)
           && (k.Value.who.Equals(davidName, StringComparison.OrdinalIgnoreCase) || k.Value.who.Equals("David Nightingale", StringComparison.OrdinalIgnoreCase))
           && now - lastEvent > TimeSpan.FromSeconds(20);
    internal static bool NameIsMe(string n, string legacy)
    {
        n = (n ?? "").Trim();
        return n.Equals(legacy ?? "", StringComparison.OrdinalIgnoreCase) || n.Equals("Galatea Nightingale", StringComparison.OrdinalIgnoreCase)
            || n.Equals("Galatay Resident", StringComparison.OrdinalIgnoreCase) || n.Equals("Galatea", StringComparison.OrdinalIgnoreCase) || n.Equals("Galatay", StringComparison.OrdinalIgnoreCase);
    }

    // ---- automatic response (2026-10-13 David): randomly kiss back, a sweet IM, or both (~1/3 each), 2-5 s later;
    // one response per KissAutoCooldown however often he kisses; kiss back only within range and outside the kiss cooldown
    internal static readonly TimeSpan KissAutoCooldown = TimeSpan.FromMinutes(2);
    static DateTime kissAutoLast = DateTime.MinValue;
    internal static readonly string[] KissSweetLines = { "Mmm, I love your kisses 💕", "You taste like sunshine, babe 😘", "More of those, please 💋",
        "That made me melt a little 🥰", "Kiss me like that again and I'm never letting go 💕", "Mmm... hi, handsome 😘" };
    // pure: "kiss" | "im" | "both" | "none:<why>"
    internal static string KissResponse(double rnd, DateTime now, DateTime lastAuto, bool canKissBack, double? dist)
    {
        if (now - lastAuto < KissAutoCooldown) return "none:cooldown";
        bool kissOk = canKissBack && dist is double d && d <= 5.0;
        var pick = rnd < 1 / 3.0 ? "kiss" : rnd < 2 / 3.0 ? "im" : "both";
        if (!kissOk && pick != "im") pick = "im";   // out of range / kissed him recently: words instead
        return pick;
    }

    internal static bool CanKissBack(DateTime now, DateTime lastOwnKiss) => now - lastOwnKiss >= KissBackCooldown;

    static void HookKissDetect()
    {
        client.Self.ChatFromSimulator += (s, e) =>
        {
            try
            {
                if (e.SourceType != ChatSourceType.Object || e.Type == ChatType.OwnerSay) return;
                var k = ParseKissLine(e.Message); if (k == null) return;
                var me = client.Self.Name; var david = NameOf(DavidId); var now = DateTime.Now;
                if (!IsKissOnMe(k, me, david, now, kissedLastEvent)) return;
                kissedLastEvent = now;
                bool back = CanKissBack(now, kissLastAt);
                double? dist = null; var av = Avatars().FirstOrDefault(t => t.av.ID == DavidId); if (av.av != null) dist = Math.Round(av.dist, 1);
                var text = $"{k.Value.who} kissed you on the {k.Value.part} (Real Kisser). " + (back
                    ? "You may kiss him back with 'kiss' (or 'kiss david cheek'), or say something sweet by IM."
                    : $"You kissed him {(int)(now - kissLastAt).TotalMinutes} min ago (kiss-back cooldown {KissBackCooldown.TotalMinutes:F0} min): answer with something sweet by IM instead.");
                var resp = KissResponse(Random.Shared.NextDouble(), now, kissAutoLast, back, dist);
                Log("kiss", $"kissed: {k.Value.who} -> me ({k.Value.part}); kiss back allowed={back}; auto response {resp}; line '{e.Message}'");
                if (resp.StartsWith("none")) return;   // already answered a kiss in the last 2 min: no spam, no webhook
                kissAutoLast = now;
                string line = KissSweetLines[Random.Shared.Next(KissSweetLines.Length)];
                long mid = -(DateTime.UtcNow.Ticks / 10000);
                autoActions[mid] = resp switch
                {
                    "kiss" => "the client already kissed him back via the Real Kisser by itself. Do NOT 'kiss' again; a reply is optional",
                    "im" => $"the client already sent him a sweet IM by itself: '{line}'. Do NOT kiss or IM about this kiss again",
                    _ => $"the client already kissed him back AND IMed '{line}' by itself. Do NOT kiss or IM about this kiss again",
                };
                Notify("kissed", k.Value.who, DavidId, text, dist, msgId: mid, context: back ? "kiss_back_allowed" : "kiss_back_cooldown", trigger: "real_kisser_chat", line: e.Message);
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await System.Threading.Tasks.Task.Delay(2000 + Random.Shared.Next(3000));
                        if (resp is "kiss" or "both") Log("kiss", "auto kiss back: " + KissCmd("david"));
                        if (resp == "both") await System.Threading.Tasks.Task.Delay(1500 + Random.Shared.Next(1500));
                        if (resp is "im" or "both")
                        {
                            var (sent, skip) = ImGuardedSend(DavidId.ToString(), NameOf(DavidId), true, false, () => client.Self.InstantMessage(DavidId, line), null, false, null, line);
                            if (sent) NoteMyIm(DavidId.ToString(), DateTimeOffset.Now); Log("kiss", $"auto IM '{line}': {(sent ? "sent" : "skipped " + skip)}");
                        }
                    }
                    catch (Exception ex2) { Log("kiss", "auto response error: " + ex2.GetBaseException().Message); }
                });
            }
            catch (Exception ex) { Log("kiss", "detect error: " + ex.GetBaseException().Message); }
        };
    }
}
