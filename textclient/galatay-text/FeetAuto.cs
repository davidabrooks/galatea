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

    /// <summary>Pure: the tab bar of the page holding a button = the lowest 'tabs' prim above the button (pages stack in z), or -1.</summary>
    internal static int FeetPageTabIndex(IReadOnlyList<(string name, float z)> prims, float buttonZ)
    {
        int best = -1; float bz = float.MaxValue;
        for (int i = 0; i < prims.Count; i++) if (prims[i].name == "tabs" && prims[i].z > buttonZ && prims[i].z < bz && prims[i].z - buttonZ < 1f) { bz = prims[i].z; best = i; }
        return best;
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

    /// <summary>Pure: signature of the worn Maitreya/LaraX body = sorted "itemId/primCount" of its attachment roots (name-matched via BodyFamily);
    /// a new, re-attached or changed body (new item/attachment id, or its prims arriving/changing) gives a different string.</summary>
    internal static string FeetBodySignature(IEnumerable<(UUID item, string name, int childPrims)> worn) =>
        string.Join(",", (worn ?? Array.Empty<(UUID, string, int)>()).Where(w => BodyFamily(w.name) == "larax").Select(w => $"{w.item}/{w.childPrims}").OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>Pure: the body changed when a previous signature exists (not the first poll) and differs.</summary>
    internal static bool FeetBodyChanged(string previousSig, string sig) => previousSig != null && previousSig != sig;

    /// <summary>Pure decision per poll: a body change makes the last-pressed pose unknown (the body reset its feet to flat), then the normal rule applies.
    /// Returns whether to press and the last-pressed value to use (null = cleared).</summary>
    internal static (bool press, string lastPressed) FeetDecide(string wanted, string previousPoll, string lastPressed, bool bodyChanged)
    {
        if (bodyChanged) lastPressed = null;
        return (FeetShouldPress(wanted, previousPoll, lastPressed), lastPressed);
    }

    static string FeetBodySigNow()
    {
        var worn = WornPrims();
        return FeetBodySignature(worn.Select(p => (AttachItemId(p), p.Properties?.Name, Sim?.ObjectsPrimitives.Values.Count(c => c != null && c.ParentID == p.LocalID) ?? 0)));
    }

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
        string prevPoll = null, prevSig = null;
        while (true)
        {
            try
            {
                if (client?.Network?.Connected == true && WornPrims().Count > 3)
                {
                    var (f, why, rules) = FeetNow();
                    var sig = FeetBodySigNow(); bool bodyChanged = FeetBodyChanged(prevSig, sig); prevSig = sig;
                    var (press, last) = FeetDecide(f, prevPoll, ReadFeetState(), bodyChanged);
                    if (bodyChanged) { try { File.Delete(FeetStatePath); } catch { } Log("feet", "worn body changed: last pressed pose cleared"); }
                    if (press) await PressFeet(f, why, rules);
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
            // the LaraX HUD only reacts on the page it SHOWS (2026-10-21 11:38 bug: f3 pressed on a hidden page, feet stayed flat):
            // open the feet page by touching that page's own tab bar first (the 'tabs' prim just above the f-buttons)
            try
            {
                var hudRoot = WornPrims().FirstOrDefault(p => AttachItemId(p) == rules.Hud);
                if (hudRoot != null)
                {
                    var prims = LinkPrims(hudRoot); await EnsureProperties(Sim, prims);
                    var btn = prims.FirstOrDefault(p => p.Properties?.Name == feet);
                    int ti = btn == null ? -1 : FeetPageTabIndex(prims.Select(p => (p.Properties?.Name ?? "", p.Position.Z)).ToList(), btn.Position.Z);
                    if (ti >= 0)
                    {
                        var none = AgentManager.TOUCH_INVALID_VECTOR; var st = new Vector3(0.1f, 0.5f, 0);
                        client.Self.Grab(prims[ti].LocalID, Vector3.Zero, st, st, 4, none, none, none); await Task.Delay(150);
                        client.Self.DeGrab(prims[ti].LocalID, st, st, 4, none, none, none); await Task.Delay(1500);
                    }
                }
            }
            catch (Exception ex) { Log("feet", "feet page open failed: " + ex.GetBaseException().Message); }
            var r = await TouchAttachment($"{rules.Hud} {feet} 4 st=0.5,0.5");
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
