using LibreMetaverse;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GalatayText;

// 2026-10-12 David: some furniture poses leave her hands flat/splayed (reading a magazine on the Mirage bed). After wander
// takes a pose, set a fitting Maitreya LaraX hand pose via the body HUD (HANDS/FEET tab, HAND POSES grid = prim 'poses',
// face 4, touch ST: 14 columns, top row L, bottom row R): holding props -> grip (column 12), otherwise relaxed (column 1).
// On standing: relaxed again. The body keeps playing the hand animation after the HUD is detached.
public static partial class Program
{
    internal static readonly UUID MaitreyaHud = new("961d2d4b-4948-3bac-ab88-0a7ba3b99302");
    internal const int HandGripCol = 12, HandRelaxedCol = 1, HandPointCol = 5, HandFistCol = 14;
    static readonly Regex HandGripWords = new(@"(?<![a-z])(read\w*|book|magazine|phone|talkphone|selfie|text\w*|laptop|tablet|cup|coffee|tea|drink\w*|mug|glass|wine|beer|hold\w*|carry\w*|brush\w*|comb\w*|mirror|guitar|ukulele|knit\w*|sew\w*|pen|write|writing|draw\w*|paint\w*|fish\w*|rod|umbrella|bottle|flower\w*|bouquet|camera|photo\w*|cards?|game\w*|controller|eat\w*|food|apple|ice\s*cream|cigar\w*|smok\w*)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex HandPointWords = new(@"(?<![a-z])(point\w*|explain\w*|show\w*)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex HandFistWords = new(@"(?<![a-z])(fist|punch\w*|box\w*|cheer\w*)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // pure: hand-pose column for a pose path (leaf weighs most; menus count too, e.g. "Reading*")
    internal static (int col, string name) HandPoseFor(System.Collections.Generic.IEnumerable<string> path)
    {
        var parts = (path ?? Enumerable.Empty<string>()).Select(p => (p ?? "").Replace('_', ' ')).ToList();
        if (parts.Any(p => HandGripWords.IsMatch(p))) return (HandGripCol, "grip");
        if (parts.Any(p => HandFistWords.IsMatch(p))) return (HandFistCol, "fist");
        if (parts.Any(p => HandPointWords.IsMatch(p))) return (HandPointCol, "point");
        return (HandRelaxedCol, "relaxed");
    }
    // pure: touch ST on the 'poses' grid
    internal static (float s, float t) HandPoseSt(int col, bool left) => ((col - 0.5f) / 14f, left ? 0.75f : 0.25f);

    static readonly SemaphoreSlim handGate = new(1, 1);
    static string handLast = "-";

    static async Task<string> SetHandPose(int col, string name, string why)
    {
        if (!LoggedIn) return "not logged in";
        if (!await handGate.WaitAsync(0)) return "hand pose: busy";
        try
        {
            using var fc = new CancellationTokenSource(20000); var item = await FetchItemRO(MaitreyaHud, fc.Token);
            if (item == null) return "hand pose: Maitreya HUD not in inventory";
            bool wasOn = WornPrims().Any(p => AttachItemId(p) == MaitreyaHud);
            if (!wasOn) client.Appearance.Attach(item, AttachmentPoint.Default, false);
            using var cts = new CancellationTokenSource(20000);
            var root = await WaitWornItem(MaitreyaHud, 15000, cts.Token);
            if (root == null) return "hand pose: HUD did not attach";
            await Task.Delay(2500);
            var prims = LinkPrims(root); await EnsureProperties(Sim, prims);
            var grid = prims.FirstOrDefault(p => p.Properties?.Name == "poses");
            string res;
            if (grid == null) res = "hand pose: 'poses' prim not found";
            else
            {
                foreach (var left in new[] { true, false })
                { var (s, t) = HandPoseSt(col, left); GrabAt(grid, 4, s, t); await Task.Delay(1200); }
                res = $"hand pose {name} (column {col}, both hands) for {why}";
            }
            if (!wasOn) { await Task.Delay(1500); client.Appearance.Detach(item); }
            handLast = $"{DateTime.Now:HH:mm:ss} {res}"; Log("hands", res); return res;
        }
        catch (Exception ex) { var m = "hand pose: " + ex.GetBaseException().Message; Log("hands", m); return m; }
        finally { handGate.Release(); }
    }

    static void HandPoseForPathAsync(System.Collections.Generic.IEnumerable<string> path, string why)
    {
        var (col, name) = HandPoseFor(path);
        _ = Task.Run(async () => { await Task.Delay(2000); await SetHandPose(col, name, why); HandsVerifyAfterPose(why); });   // HandBones.cs check
    }

    static async Task<string> HandsCmd(string[] a)
    {
        if (a.Length == 0 || a[0] == "status") return "hands: last " + handLast;
        if (a[0] == "check") return await HandsCheckCmd(string.Join(" ", a.Skip(1)));
        var m = a[0].ToLowerInvariant() switch { "grip" => HandGripCol, "relaxed" => HandRelaxedCol, "point" => HandPointCol, "fist" => HandFistCol, _ => int.TryParse(a[0], out var c) && c is >= 1 and <= 14 ? c : -1 };
        if (m < 0) return "usage: hands grip|relaxed|point|fist|<1-14>|status|check [avatar]";
        return await SetHandPose(m, a[0], "command");
    }
}
