// PoseRules.cs (2026-10-16, David): until a visual pose catalog exists, poses are classified BY NAME, in this ONE place.
//   - sexual words: a pose / menu name with one of them is adult (Masturbate, Jiller, Fist, Blowjobs, Sexy sit ...)
//   - adult menu names: Adult*, MF*, FFM*, xxx ... (menu names that only hold adult poses)
//   - adult seat names: Xplicit Makeup Table ... ; a seat is also adult when any menu seen on it had an adult name
//   - plain menu words: a submenu whose every word is one of these is a known plain one; on an adult seat any OTHER submenu
//     name is "unclear" and counts as adult (so are the poses under it)
//   - a pose inherits adult from every menu above it (SeatPose.cs PosePathIsSexual)
// Adult means: only in a human outfit, alone on our parcel, after changing to Naked; the outfit is put back afterwards
// (AdultRedress.cs). A future catalog replaces the defaults below by an optional JSON file (GT_POSE_RULES, default
// <routes>/_pose-rules.json): {"sexual_words": "<regex>", "adult_menu": "<regex>", "adult_seat": "<regex>",
// "plain_menu_words": [..] (replaces), "extra_plain_menu_words": [..] (adds), "adult_names": [..], "pg_names": [..]}
// Conversation poses (Talk, Chat ...; Reiley Net Chair PG: SINGLE* > Talk) are for talking to David from the paired seat: the solo
// picker skips them unless David sits next to her ("conversation_pose": "<regex>" replaces the default, "extra_conversation_names": [..] adds exact names).
// adult_names / pg_names are exact bare pose or menu names (case-insensitive): adult_names are always adult, pg_names are
// never adult through the sexual word list (a catalog's per-pose verdict wins over the guess by name).
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GalatayText;

internal sealed class PoseRuleSet
{
    public const string DefaultAdultMenu = @"(?<![a-z])(adults?|ffm|mmf|fmf|mfm|f\+?f\d*|m\+?f\d*|m\s*[/&]\s*f|f\s*[/&]\s*m|xxx|nsfw|erotic\w*|(?:blow|hand|foot|tit|rim)\s*jobs?|going\s*down|oral|mutual\s*fp)(?![a-z])";
    public const string DefaultSexualWords = @"(?<![a-z])(sexy|sex\w*|seduc\w*|sensual\w*|naughty|tease\w*|teasing|horny|lust\w*|kink\w*|nude|naked|strip\w*|boobs?|breasts?|tits?|ass|butt|blow|suck\w*|lick\w*|cowgirl|doggy|grind\w*|hump\w*|ride\s*me|rodeo|milking|diddl\w*|juicy|drenched|pleasure|passion|bang|queened|deliver\w*ass|69|cuni\w*|cunn\w*|bop|wet|moan\w*|orgasm\w*|climax\w*|spread\w*|bend\s*over|jobs?|bj|hj|choke|gag\w*|facial|scissor\w*|thrust\w*|pound\w*|finger(?:ing|ed)|touch(?:ing)?\s*(?:him|her|me))(?![a-z])|fuck|cunt|anal|clit|dick|cock|dildo|vibrat|throat|creamy|sticky|pussy|penis|vagina|jizz|cum(?![a-z]*ul)|fist|balls|masturb|jill(?:er|ing|s)\b|jerk|grope|wank|\bfapp?|boobies|foreplay|fondl|spank|bdsm|bondage|orgy|gangbang|handjob|footjob|rimjob|nipple|creampie|squirt|penetrat|missionary|pegging";
    public const string DefaultConversationPose = @"^(talk\w*|chat\w*|convers\w*)([\s_\-]*\d+)?$";
    public const string DefaultAdultSeat = @"x?plicit|adult|nsfw|xxx|erotic|kink|porn|brothel|\bsex";
    public static readonly string[] DefaultPlainMenuWords = { "solo", "single", "singles", "sit", "sits", "sitting", "stand", "standing", "preen", "picture", "pictures", "photo", "photos", "selfie", "relax", "relaxed", "relaxing", "chill", "chilling", "pose", "poses", "lounge", "couch", "pouf", "sofa", "chair", "bed", "sleep", "read", "reading", "phone", "drink", "drinks", "laptop", "pg", "clean", "casual", "idle", "lay", "lie", "laying", "f", "female", "girl", "girls", "lady", "ladies", "misc", "more", "other", "page", "zero", "g" };

    public Regex AdultMenu { get; private set; } = new(DefaultAdultMenu, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public Regex Sexual { get; private set; } = new(DefaultSexualWords, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    Regex conversation = new(DefaultConversationPose, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    HashSet<string> conversationNames = new(StringComparer.OrdinalIgnoreCase);
    Regex adultSeat = new(DefaultAdultSeat, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    HashSet<string> plain = new(DefaultPlainMenuWords, StringComparer.OrdinalIgnoreCase);
    HashSet<string> adultNames = new(StringComparer.OrdinalIgnoreCase), pgNames = new(StringComparer.OrdinalIgnoreCase);
    public string Source { get; private set; } = "built-in defaults";

    static string Bare(string raw) => (raw ?? "").Trim().TrimEnd('*').Trim();

    // the name (bare pose or menu name) is sexual by the word list, unless the catalog says it is plain; or the catalog says adult
    public bool IsSexualName(string bare)
    {
        bare = Bare(bare);
        if (adultNames.Contains(bare)) return true;
        if (pgNames.Contains(bare)) return false;
        return Sexual.IsMatch(bare);
    }
    public bool IsAdultMenuName(string bare) => adultNames.Contains(Bare(bare)) || (!pgNames.Contains(Bare(bare)) && AdultMenu.IsMatch(Bare(bare)));
    // a conversation pose / menu name (bare, no '*'): meant for talking to David from the paired seat
    public bool IsConversationName(string bare) { bare = Bare(bare); return bare.Length > 0 && (conversationNames.Contains(bare) || conversation.IsMatch(bare)); }
    public bool SeatNameAdult(string seatName) => !string.IsNullOrWhiteSpace(seatName) && adultSeat.IsMatch(seatName);
    // a submenu name made only of plain words (numbers ok)
    public bool SubmenuTrusted(string bareMenu)
    {
        if (pgNames.Contains(Bare(bareMenu))) return true;
        var words = Regex.Split(Bare(bareMenu).ToLowerInvariant(), @"[^a-z0-9]+").Where(w => w.Length > 0).ToList();
        return words.Count > 0 && words.All(w => plain.Contains(w) || w.All(char.IsDigit));
    }

    // build from the override JSON (null / empty = the defaults). Bad JSON or a bad regex throws.
    public static PoseRuleSet FromJson(string json, string source = "override")
    {
        var r = new PoseRuleSet();
        if (string.IsNullOrWhiteSpace(json)) return r;
        using var doc = JsonDocument.Parse(json); var o = doc.RootElement;
        const RegexOptions ro = RegexOptions.IgnoreCase | RegexOptions.Compiled;
        if (o.TryGetProperty("sexual_words", out var a) && a.ValueKind == JsonValueKind.String) r.Sexual = new Regex(a.GetString()!, ro);
        if (o.TryGetProperty("adult_menu", out var b) && b.ValueKind == JsonValueKind.String) r.AdultMenu = new Regex(b.GetString()!, ro);
        if (o.TryGetProperty("adult_seat", out var c) && c.ValueKind == JsonValueKind.String) r.adultSeat = new Regex(c.GetString()!, ro);
        IEnumerable<string> L(string k) => o.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : null;
        var pw = L("plain_menu_words"); if (pw != null) r.plain = new HashSet<string>(pw, StringComparer.OrdinalIgnoreCase);
        var xw = L("extra_plain_menu_words"); if (xw != null) foreach (var w in xw) r.plain.Add(w);
        if (o.TryGetProperty("conversation_pose", out var cv) && cv.ValueKind == JsonValueKind.String) r.conversation = new Regex(cv.GetString()!, ro);
        var cn = L("extra_conversation_names"); if (cn != null) foreach (var w in cn) r.conversationNames.Add(Bare(w));
        var an = L("adult_names"); if (an != null) foreach (var w in an) r.adultNames.Add(Bare(w));
        var pn = L("pg_names"); if (pn != null) foreach (var w in pn) r.pgNames.Add(Bare(w));
        r.Source = source; return r;
    }
}

internal static class PoseRules
{
    static PoseRuleSet current;
    static readonly object gate = new();
    public static string FilePath => Environment.GetEnvironmentVariable("GT_POSE_RULES") is { Length: > 0 } p ? p : Path.Combine(Environment.GetEnvironmentVariable("GT_ROUTE_DIR") ?? "/home/box/viewers/textclient/routes", "_pose-rules.json");
    public static PoseRuleSet Current
    {
        get
        {
            if (current != null) return current;
            lock (gate)
            {
                if (current != null) return current;
                try { var f = FilePath; current = File.Exists(f) ? PoseRuleSet.FromJson(File.ReadAllText(f), f) : new PoseRuleSet(); }
                catch (Exception ex) { current = new PoseRuleSet(); Program.Log("pose", $"pose rules file {FilePath} unusable ({ex.GetBaseException().Message}); using the built-in rules"); }
                return current;
            }
        }
    }
}
