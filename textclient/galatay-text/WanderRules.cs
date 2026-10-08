// Per-region wander rules (2026-10-07, David): data-driven from routes/_wander-rules.json, keyed by region name.
// Home (Peronaut): sit anywhere (even by David), skip seats with anyone within 3 m, greetings on.
// levelDwellMin (2026-10-08): home wander minutes on one level (beach / upper) before switching (LevelDwell.cs).
// Buddha Center (Naberrie): quiet rule, no seat within 10 m of ANY avatar; greetings on (sessions still suppress via Quiet.cs).
using System.Text.Json.Nodes;

namespace GalatayText;

public record WanderRule(string Place, float SeatAvatarM, bool Greet, double LevelDwellMin = Program.DefaultLevelDwellMin);

public static partial class Program
{
    static readonly WanderRule DefaultWanderRule = new("default", 3f, true);
    static string WanderRulesFile => Path.Combine(RouteDir, "_wander-rules.json");
    static (DateTime mt, string json) wRulesCache;

    // pure: parse rules json and pick the entry for a region (case-insensitive), else "default", else built-in
    internal static WanderRule ParseWanderRule(string json, string region)
    {
        if (string.IsNullOrWhiteSpace(json)) return DefaultWanderRule;
        var o = JsonNode.Parse(json, documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        if (o == null) return DefaultWanderRule;
        JsonObject Find(string k) => k == null ? null : o.FirstOrDefault(kv => string.Equals(kv.Key, k.Trim(), StringComparison.OrdinalIgnoreCase)).Value as JsonObject;
        var e = Find(region) ?? Find("default");
        if (e == null) return DefaultWanderRule;
        return new WanderRule(
            (string)e["place"] ?? region ?? "default",
            e["seatAvatarM"] is JsonNode m ? (float)m.GetValue<double>() : DefaultWanderRule.SeatAvatarM,
            e["greet"] is JsonNode g ? g.GetValue<bool>() : DefaultWanderRule.Greet,
            e["levelDwellMin"] is JsonNode d && d.GetValue<double>() > 0 ? d.GetValue<double>() : DefaultWanderRule.LevelDwellMin);
    }

    // pure: is this seat allowed given the nearest same-level avatar distance?
    internal static bool SeatAllowedByRule(WanderRule r, float nearestAvatarM) => nearestAvatarM >= r.SeatAvatarM;

    static WanderRule CurrentWanderRule()
    {
        var region = client?.Network?.CurrentSim?.Name;
        try
        {
            var f = WanderRulesFile;
            if (!File.Exists(f)) return DefaultWanderRule;
            var mt = File.GetLastWriteTimeUtc(f);
            if (wRulesCache.mt != mt) wRulesCache = (mt, File.ReadAllText(f));
            return ParseWanderRule(wRulesCache.json, region);
        }
        catch (Exception ex) { WLog("wander rules: " + ex.Message); return DefaultWanderRule; }
    }
}
