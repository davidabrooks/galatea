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
    internal static bool IsKissOnMe((string who, string whom, string part)? k, string myName, string davidName, DateTime now, DateTime lastEvent)
        => k != null && myName != null && davidName != null
           && k.Value.whom.Equals(myName, StringComparison.OrdinalIgnoreCase) && k.Value.who.Equals(davidName, StringComparison.OrdinalIgnoreCase)
           && now - lastEvent > TimeSpan.FromSeconds(20);

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
                Log("kiss", $"kissed: {k.Value.who} -> me ({k.Value.part}); kiss back allowed={back}; line '{e.Message}'");
                Notify("kissed", k.Value.who, DavidId, text, dist, context: back ? "kiss_back_allowed" : "kiss_back_cooldown", trigger: "real_kisser_chat", line: e.Message);
            }
            catch (Exception ex) { Log("kiss", "detect error: " + ex.GetBaseException().Message); }
        };
    }
}
