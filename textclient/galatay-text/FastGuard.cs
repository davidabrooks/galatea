// FastGuard.cs (2026-10-18, David): stricter code-enforced guards for FAST REPLIES TO VISITORS (David's lines are untouched).
// Why: in a ~90-avatar club fast chat answered ~40 crowd greetings not aimed at her (and said "Sorry about that" 3 times),
// a resident complained of spam and she was banned from the parcel. The persona text asks for the same behaviour, but these
// rules are enforced here, whatever the model says. A line a guard refuses goes to the chat routine exactly like an empty reply.
//   Pre-guard (before the API call, FastGuardPre): emote/emoji-only lines, crowd greetings, name lists, lines addressed to
//     someone else and anything not clearly addressed to her are skipped; so are all nearby lines while she is silent after a
//     stop/spam complaint. Addressed = her name (Galatay/Galatea/Gala), an IM, a reply to something she said to that person in
//     the last 60 s, or (few avatars around) a question with "you".
//   Final guard (right before sending, FastGuardFinal, atomic with recording the send): near-identical to her last nearby line,
//     or to a line sent to that person in the last 10 min; nearby rate cap (6/min, only 3/min when more than 15 avatars are
//     within chat range); at most one apology line per 30 min in nearby chat.
//   Stop/spam complaints: reply at most once (per person for IMs), then no nearby fast replies for 15 min (IMs still answered).
using System.Text.RegularExpressions;

namespace GalatayText;

public static partial class Program
{
    internal sealed class FastGuardState
    {
        internal readonly object Gate = new();
        internal DateTimeOffset SilentUntil = DateTimeOffset.MinValue;
        internal readonly List<DateTimeOffset> NearbySent = new();                       // fast replies to visitors in nearby chat
        internal readonly List<(DateTimeOffset t, string reply, string who)> Sent = new(); // every guarded fast reply
        internal DateTimeOffset LastApology = DateTimeOffset.MinValue;
        internal readonly Dictionary<string, DateTimeOffset> RepliedTo = new();           // speaker id -> my last fast reply to them
        internal readonly Dictionary<string, DateTimeOffset> StopAcked = new();           // speaker id -> IM stop request answered
    }
    internal static readonly FastGuardState fastGuard = new();
    static int fastGuarded;

    static readonly Regex FastNameRx = new(@"\b(?:galatay|galatea|gala)(?:'s)?\b", RegexOptions.IgnoreCase);
    static readonly Regex FastApologyRx = new(@"\b(?:sorry|apolog\w*|my bad|forgive me|oops)\b", RegexOptions.IgnoreCase);
    static readonly Regex FastComplaintRx = new(
        @"\bspam\w*|\bflood\w*|\bstfu\b|\b(?:shut\s*up|be\s+quiet|quiet\s+down|too\s+(?:much|many)\s+(?:chat|text|messages|talk\w*))\b|^\W*(?:please\s+|pls\s+)?(?:stop|enough)\W*$|" +
        @"\b(?:stop|quit|enough|cut\s+it\s+out|knock\s+it\s+off)\b.{0,25}\b(?:chat\w*|talk\w*|repl\w*|post\w*|typ\w*|it|that|bot)\b|\b(?:annoying|mute)\b.{0,20}\b(?:bot|her|you)\b",
        RegexOptions.IgnoreCase);
    static readonly Regex FastYouRx = new(@"\b(?:you|your|yours|u|ur)\b", RegexOptions.IgnoreCase);
    const string FastGreetWords = "hi|hello|hey|heya|hiya|hai|howdy|yo|sup|welcome|greetings|gm|gn|good\\s+(?:morning|evening|afternoon|night)|happy\\s+\\w+day";
    const string FastCrowdWords = "everyone|everybody|all|guys|folks|people|y'?all|room|there|friends|peeps|ladies|gentlemen|world|club|back|again|and|here|ya";
    static readonly Regex FastCrowdGreetRx = new($@"^(?:(?:{FastGreetWords})\b[\s,!.~]*)+(?:(?:{FastCrowdWords})\b[\s,!.~]*)*$", RegexOptions.IgnoreCase);
    static readonly Regex FastOtherRx = new($@"^\s*(?:(?<g>{FastGreetWords})\s+)?@?(?<n>[\p{{L}}][\p{{L}}'\-]{{1,24}})(?<p>\s*[,:!?])?", RegexOptions.IgnoreCase);
    static readonly HashSet<string> FastNotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "everyone","everybody","all","guys","folks","people","yall","y'all","room","there","friends","peeps","ladies","gentlemen","world","club","you","u","ur","your","again","back",
        "lol","lmao","haha","hehe","yes","yeah","yep","no","nope","thanks","thank","thx","ok","okay","sorry","please","pls","well","so","and","wow","oh","aww","aw","omg","sure","right",
        "nice","cool","great","good","hm","hmm","what","why","how","who","where","when","i","im","it","that","this","the","a","my","me","we","do","does","did","is","are","can","could",
        "galatay","galatea","gala","here","ya","too","not","maybe","true","same","exactly",
    };
    static readonly Regex FastLettersRx = new(@"\p{L}");

    // pure: nothing but emoji / punctuation, or a /me emote
    internal static bool FastEmoteOnly(string text)
    {
        var t = (text ?? "").Trim();
        return t.Length == 0 || !FastLettersRx.IsMatch(t) || t.StartsWith("/me ", StringComparison.OrdinalIgnoreCase) || t.Equals("/me", StringComparison.OrdinalIgnoreCase)
            || (t.Length > 2 && t.StartsWith("*") && t.EndsWith("*") && !t.Substring(1, t.Length - 2).Contains('*'));
    }
    internal static bool FastNamesMe(string text) => !string.IsNullOrEmpty(text) && FastNameRx.IsMatch(text);
    internal static bool FastIsApology(string text) => !string.IsNullOrEmpty(text) && FastApologyRx.IsMatch(text);
    // pure: "stop chatting" / spam complaint
    internal static bool FastIsComplaint(string text) => !string.IsNullOrEmpty(text) && FastComplaintRx.IsMatch(text);
    // pure: a greeting for the room ("hi everyone", "HAPPY SUNDAY!") without her name
    internal static bool FastCrowdGreeting(string text) => !FastNamesMe(text) && FastCrowdGreetRx.IsMatch((text ?? "").Trim());
    // pure: a greeting that is a list of names ("hi Nele, Sheluxe, Gala"): 3+ capitalised names once greetings/fillers are removed
    internal static bool FastNameList(string text)
    {
        var t = Regex.Replace(text ?? "", $@"\b(?:{FastGreetWords}|{FastCrowdWords})\b|[,&!.~]|\\?o/|\\o", " ", RegexOptions.IgnoreCase);
        var toks = t.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        return toks.Length >= 3 && toks.All(x => char.IsUpper(x[0]) || x[0] == '@');
    }
    // pure: starts by addressing somebody else ("Nele, ...", "hi Nele", "@Sheluxe ...") and does not name her
    internal static bool FastAddressedToOther(string text)
    {
        if (FastNamesMe(text)) return false;
        var m = FastOtherRx.Match(text ?? "");
        if (!m.Success) return false;
        var n = m.Groups["n"].Value;
        if (FastNotNames.Contains(n)) return false;
        bool greet = m.Groups["g"].Success, punct = m.Groups["p"].Success, at = (text ?? "").TrimStart().StartsWith("@");
        return greet || at || (punct && char.IsUpper(n[0]));
    }

    // pure: lower-case letters/digits only, single spaces
    internal static string FastNorm(string s) => Regex.Replace(Regex.Replace((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " "), @"\s+", " ").Trim();
    // pure: the same line again (ignoring case, punctuation, emoji), or one that contains the other / shares >= 80 % of its words
    internal static bool FastNearIdentical(string a, string b)
    {
        string x = FastNorm(a), y = FastNorm(b);
        if (x.Length == 0 && y.Length == 0) return (a ?? "").Trim() == (b ?? "").Trim();
        if (x.Length == 0 || y.Length == 0) return false;
        if (x == y) return true;
        var (s, l) = x.Length <= y.Length ? (x, y) : (y, x);
        if (s.Length >= 12 && l.Contains(s)) return true;
        var tx = x.Split(' ').ToHashSet(); var ty = y.Split(' ').ToHashSet();
        return tx.Count >= 3 && ty.Count >= 3 && (double)tx.Intersect(ty).Count() / tx.Union(ty).Count() >= 0.8;
    }

    // Before the API call. Returns null = go on, else why the line is skipped (it then goes to the routine). complaint=true: the
    // line is a stop/spam request that may get ONE short reply (nearby silence has been started).
    internal static string FastGuardPre(FastCfg c, FastGuardState st, string speakerId, string text, bool im, int nearbyAvatars,
        bool routineRepliedToSpeaker, DateTimeOffset? lastOwnNearby, DateTimeOffset now, out bool complaint)
    {
        complaint = false;
        if (FastEmoteOnly(text)) return "emote/emoji-only line";
        lock (st.Gate)
        {
            if (!im && now < st.SilentUntil) return $"silent in nearby chat until {st.SilentUntil.ToLocalTime():h:mm tt} after a stop request";
            bool repliedRecently = routineRepliedToSpeaker || (st.RepliedTo.TryGetValue(speakerId, out var rt) && (now - rt).TotalSeconds <= c.GuardReplyWindowS);
            bool named = FastNamesMe(text);
            bool addressed = im || named || (repliedRecently && !FastAddressedToOther(text) && !FastCrowdGreeting(text));
            if (!addressed && !im && text.Contains('?') && nearbyAvatars <= c.GuardQuietAvatars && FastYouRx.IsMatch(text)) addressed = true;   // few people around: "are you ok?" is to her
            if (FastIsComplaint(text) && (addressed || im || (lastOwnNearby != null && (now - lastOwnNearby.Value).TotalSeconds <= 120)))
            {
                if (im)
                {
                    if (st.StopAcked.TryGetValue(speakerId, out var at) && (now - at).TotalMinutes < c.GuardStopSilenceMin) return "stop request already answered once";
                    st.StopAcked[speakerId] = now;
                }
                st.SilentUntil = now.AddMinutes(c.GuardStopSilenceMin);   // nearby silence starts now; this one line may still get a reply
                complaint = true;
                return null;
            }
            if (im) return null;
            if (FastNameList(text)) return "list of names";
            if (FastCrowdGreeting(text)) return "crowd greeting";
            if (!addressed) return FastAddressedToOther(text) ? "addressed to someone else" : "not addressed to me";
            return null;
        }
    }

    // Right before sending (David's lines never get here). Returns null = send (and the send is recorded), else why not.
    internal static string FastGuardFinal(FastCfg c, FastGuardState st, string speakerId, string reply, bool im, int nearbyAvatars,
        string lastOwnNearbyLine, DateTimeOffset now)
    {
        lock (st.Gate)
        {
            if (!im && FastNearIdentical(reply, lastOwnNearbyLine)) return "same as my last nearby line";
            st.Sent.RemoveAll(x => (now - x.t).TotalSeconds > c.GuardDedupeWindowS);
            if (st.Sent.Any(x => x.who == speakerId && FastNearIdentical(x.reply, reply))) return $"same line sent to them in the last {c.GuardDedupeWindowS / 60:0} min";
            bool apology = FastIsApology(reply);
            if (!im)
            {
                st.NearbySent.RemoveAll(t => (now - t).TotalSeconds > 60);
                int cap = nearbyAvatars > c.GuardBusyAvatars ? c.GuardBusyMaxPerMin : c.GuardMaxPerMin;
                if (st.NearbySent.Count >= cap) return $"nearby cap {cap}/min{(nearbyAvatars > c.GuardBusyAvatars ? $" (busy: {nearbyAvatars} avatars in range)" : "")}";
                if (apology && (now - st.LastApology).TotalMinutes < c.GuardApologyWindowMin) return "already apologised recently";
                st.NearbySent.Add(now);
                if (apology) st.LastApology = now;
            }
            st.Sent.Add((now, reply, speakerId));
            st.RepliedTo[speakerId] = now;
            return null;
        }
    }

    // avatars (not me) within chat range on this sim, for the busy-area cap
    static int FastNearbyAvatars(double rangeM)
    {
        try
        {
            var sim = client?.Network?.CurrentSim; if (sim == null) return 0;
            var me = client.Self.SimPosition; int n = 0;
            foreach (var a in sim.ObjectsAvatars.Values.ToArray())
            {
                if (a == null || a.ID == client.Self.AgentID || a.ID == LibreMetaverse.UUID.Zero) continue;
                try { if (a.ParentID != 0 && !sim.ObjectsPrimitives.ContainsKey(a.ParentID)) { n++; continue; } if (LibreMetaverse.Vector3.Distance(LibreMetaverse.PositionHelper.GetAvatarPosition(sim, a), me) <= rangeM) n++; } catch { n++; }
            }
            return n;
        }
        catch { return 0; }
    }
    static string FastLastOwnNearbyLine() { lock (fastHist) return fastHist.LastOrDefault(h => h.Mine && h.Scope == "nearby")?.Text; }
    static bool FastRoutineRepliedTo(string from, DateTimeOffset now)
    {
        try { return ChatMsgsOf(from).Any(m => m.Explicit && m.AnsweredAt != null && (now - m.AnsweredAt.Value).TotalSeconds <= 60); } catch { return false; }
    }
}
