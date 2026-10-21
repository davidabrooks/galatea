// MaitreyaShowAll.cs (2026-10-21, David): the Maitreya LaraX HUD only reacts to buttons on the page it is SHOWING.
// 'maitreya show all': touch the alpha page tab bar (the 'tabs' prim highest on the HUD = alpha page) to open the alpha
// page, press 'show' (Show all), then report which 'hide:N' buttons are still transparent (= hidden).
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    // pure (tested): the alpha page's tab bar = the 'tabs' prim with the highest local z
    internal static int AlphaTabIndex(IReadOnlyList<(string name, float z)> prims)
    {
        int best = -1; float bz = float.MinValue;
        for (int i = 0; i < prims.Count; i++) if (prims[i].name == "tabs" && prims[i].z > bz) { bz = prims[i].z; best = i; }
        return best;
    }
    // pure (tested): HUD 'hide:N' buttons with any transparent face (= that segment is hidden)
    internal static List<string> HiddenSegmentButtons(IEnumerable<(string name, IEnumerable<float> faceAlphas)> prims) =>
        prims.Where(p => (p.name ?? "").StartsWith("hide:") && p.faceAlphas.Any(a => a < 0.5f)).Select(p => p.name).OrderBy(n => n).ToList();

    static async Task<string> MaitreyaShowAllCmd()
    {
        var roots = WornPrims(); await EnsureProperties(Sim, roots);
        var hud = roots.FirstOrDefault(r => (r.Properties?.Name ?? "").StartsWith("Maitreya Mesh Body - Lara", StringComparison.OrdinalIgnoreCase) && (r.Properties?.Name ?? "").Contains("HUD", StringComparison.OrdinalIgnoreCase));
        if (hud == null) return "Maitreya HUD not worn";
        var prims = LinkPrims(hud); await EnsureProperties(Sim, prims);
        List<string> Hidden() => HiddenSegmentButtons(prims.Select(p => (p.Properties?.Name ?? "", FaceAlphas(p))));
        var before = Hidden();
        int ti = AlphaTabIndex(prims.Select(p => (p.Properties?.Name ?? "", p.Position.Z)).ToList());
        var show = prims.FirstOrDefault(p => p.Properties?.Name == "show");
        if (ti < 0 || show == null) return "alpha tab bar or 'show' button not found on the HUD";
        void Touch(Primitive p, Vector3 st, int face) { var none = AgentManager.TOUCH_INVALID_VECTOR; client.Self.Grab(p.LocalID, Vector3.Zero, st, st, face, none, none, none); Thread.Sleep(150); client.Self.DeGrab(p.LocalID, st, st, face, none, none, none); }
        Touch(prims[ti], new Vector3(0.1f, 0.5f, 0), 4);   // open the alpha page
        await Task.Delay(1500);
        Touch(show, new Vector3(0.5f, 0.5f, 0), 4);         // Show all
        await Task.Delay(2500);
        var after = Hidden();
        var msg = $"maitreya show all: hidden before [{string.Join(",", before)}] -> after [{string.Join(",", after)}]";
        Log("touch", msg); return msg;
    }
    static IEnumerable<float> FaceAlphas(Primitive p)
    {
        var te = p.Textures; if (te == null) yield break;
        bool any = false;
        for (int f = 0; f < 45; f++) { var fe = te.FaceTextures[f]; if (fe != null) { any = true; yield return fe.RGBA.A; } }
        if (!any && te.DefaultTexture != null) yield return te.DefaultTexture.RGBA.A;
    }
}
