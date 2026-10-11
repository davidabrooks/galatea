// FeetAuto (2026-10-11, David): Maitreya foot pose follows the worn footwear. Every 4 s she compares her worn
// attachments with routes/_feet-rules.json: a listed heeled shoe worn -> its pose (f3 for the Moca boots), none -> the
// default (f1, flat). When the wanted pose has been the same for two polls and differs from the last pose pressed
// (run/feet-state.txt), she attaches the LaraX body HUD, touches that button, and detaches it again (the HUD stays on if
// it was already worn). Because it watches what is worn rather than who changed it, outfit wear, undress/redress, the
// bikini/beach switch and the in-SL chat helper are all covered. 'feet' reports; 'feet apply' forces a press now.
// ponytail: 4 s poll + one HUD round-trip (~15 s); ceiling = feet lag a few seconds behind a shoe change.
using System.Text.Json;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal sealed record FeetRules(UUID Hud, string Default, Dictionary<UUID, (string name, string feet)> Footwear);

    static string FeetRulesPath => Path.Combine(RouteDir, "_feet-rules.json");
    static string FeetStatePath => Path.Combine(AppContext.BaseDirectory, "..", "run", "feet-state.txt");
    static readonly UUID MaitreyaHudItem = new("961d2d4b-4948-3bac-ab88-0a7ba3b99302");
    static readonly SemaphoreSlim feetGate = new(1, 1);

    /// <summary>Pure: parse _feet-rules.json text (bad/missing parts -> defaults: f1, the LaraX HUD, no footwear).</summary>
    internal static FeetRules ParseFeetRules(string json)
    {
        var fw = new Dictionary<UUID, (string, string)>(); string def = "f1"; var hud = MaitreyaHudItem;
        try
        {
            using var d = JsonDocument.Parse(json ?? "{}"); var r = d.RootElement;
            if (r.TryGetProperty("default", out var de) && IsFeetButton(de.GetString())) def = de.GetString();
            if (r.TryGetProperty("hud", out var he) && UUID.TryParse(he.GetString(), out var hu) && hu != UUID.Zero) hud = hu;
            if (r.TryGetProperty("footwear", out var fa) && fa.ValueKind == JsonValueKind.Array)
                foreach (var f in fa.EnumerateArray())
                    if (f.TryGetProperty("item", out var ie) && UUID.TryParse(ie.GetString(), out var iu) && f.TryGetProperty("feet", out var fe) && IsFeetButton(fe.GetString()))
                        fw[iu] = (f.TryGetProperty("name", out var ne) ? ne.GetString() : "?", fe.GetString());
        }
        catch { }
        return new FeetRules(hud, def, fw);
    }

    internal static bool IsFeetButton(string s) => s is "f1" or "f2" or "f3" or "f4" or "f5";

    /// <summary>Pure: the pose the worn items call for: the first listed shoe that is worn, else the default.</summary>
    internal static (string feet, string why) FeetWanted(IEnumerable<UUID> worn, FeetRules rules)
    {
        foreach (var u in worn ?? Array.Empty<UUID>())
            if (rules.Footwear.TryGetValue(u, out var s)) return (s.feet, s.name);
        return (rules.Default, "no listed heeled footwear");
    }

    /// <summary>Pure: press when two polls agree and the pose differs from the last one pressed (unknown -> press).</summary>
    internal static bool FeetShouldPress(string wanted, string previousPoll, string lastPressed) =>
        wanted != null && wanted == previousPoll && wanted != lastPressed;

    static string ReadFeetState() { try { return File.Exists(FeetStatePath) ? File.ReadAllText(FeetStatePath).Trim() : null; } catch { return null; } }

    static (string feet, string why, FeetRules rules) FeetNow()
    {
        var rules = ParseFeetRules(File.Exists(FeetRulesPath) ? File.ReadAllText(FeetRulesPath) : "{}");
        var (f, why) = FeetWanted(WornPrims().Select(AttachItemId).Where(u => u != UUID.Zero), rules);
        return (f, why, rules);
    }

    static async Task FeetLoop()
    {
        await Task.Delay(45000); // let login attachments arrive
        string prevPoll = null;
        while (true)
        {
            try
            {
                if (client?.Network?.Connected == true && WornPrims().Count > 3)
                {
                    var (f, why, rules) = FeetNow();
                    if (FeetShouldPress(f, prevPoll, ReadFeetState())) await PressFeet(f, why, rules);
                    prevPoll = f;
                }
            }
            catch (Exception ex) { Log("feet", "loop error: " + ex.GetBaseException().Message); }
            await Task.Delay(4000);
        }
    }

    static async Task<string> PressFeet(string feet, string why, FeetRules rules)
    {
        if (!await feetGate.WaitAsync(0)) return "feet: a press is already running";
        try
        {
            bool wasWorn = WornPrims().Any(p => AttachItemId(p) == rules.Hud);
            if (!wasWorn)
            {
                await AttachItem(rules.Hud, "");
                for (int i = 0; i < 30 && !WornPrims().Any(p => AttachItemId(p) == rules.Hud); i++) await Task.Delay(1000);
                if (!WornPrims().Any(p => AttachItemId(p) == rules.Hud)) { Log("feet", "body HUD did not attach in 30 s"); return "feet: body HUD did not attach"; }
                await Task.Delay(5000); // HUD script + link names
            }
            var r = await TouchAttachment($"{rules.Hud} {feet}");
            await Task.Delay(2500);
            if (!wasWorn) await DetachItemAsync(rules.Hud, "feet hud");
            bool ok = r != null && !r.StartsWith("no ", StringComparison.Ordinal) && !r.Contains(" matches ");
            if (ok) { Directory.CreateDirectory(Path.GetDirectoryName(FeetStatePath)); File.WriteAllText(FeetStatePath, feet); }
            Log("feet", $"{feet} ({why}): {r}");
            return $"feet {feet} ({why}): {r}";
        }
        finally { feetGate.Release(); }
    }

    static async Task<string> FeetCmd(string[] a)
    {
        var (f, why, rules) = FeetNow();
        if (a.Length > 1 && a[1] == "apply") return await PressFeet(f, why, rules);
        return $"feet: wanted {f} ({why}); last pressed {ReadFeetState() ?? "unknown"}; {rules.Footwear.Count} heeled item(s) listed";
    }
}
