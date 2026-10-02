// AO guard (added 2026-09-25 23:35, David: "I just saw you walking without using your AO, don't do that").
// No walking (wander legs, seat walks, chat approach, route walk, goto_place, walk_path/goto_avatar/sit_near, moveto, walk)
// unless the worn AO's overrides are confirmed active: the AO HUD is worn (2026-09-29 18:25, David: the AO is now the
// Vista "VISTA ANIMATIONS *HUD 6.3*MARTHA STS BENTO AO-V1.7" at HUD Center; AWP is retired and must NEVER be re-attached)
// is worn AND an override animation (sim state anim, source none, not a built-in) is playing AND no default STAND/WALK is playing.
// Before a walk: wait up to 25 s (the seat-off rule re-wears the AO after standing); if still inactive -> restore once
// (detach + re-attach the HUD) and wait 25 s; if still inactive the walk is refused and she stays put.
// Mid-walk: inactive for >= 1.5 s -> stop, restore once, continue only if confirmed, else stop and stay put.
// The wander goes to pause "ao" (needs 'wander resume', which re-checks). 'ao status' shows the state; status has ao=ON|off.
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    // Martha AO (2026-09-29): item id from GT_AO_ITEM or ao-item.txt, else learned from the worn attachment's name
    // (then saved to ao-item.txt). Point = HUD Center (35). The retired AWP HUD id is refused everywhere.
    public static readonly UUID RetiredAwpAo = new("01c7b845-1ab3-3a52-b58a-2b65d0c5eeb8");
    const string AoNameMatch = "MARTHA STS BENTO AO";
    static readonly string AoItemFile = Env("GT_AO_ITEM_FILE", "/home/box/viewers/textclient/ao-item.txt");
    const int AoPoint = 35; // HUDCenter
    static UUID aoItemCached = UUID.Zero;
    static UUID AoItem
    {
        get
        {
            if (aoItemCached == UUID.Zero)
            {
                var e = Environment.GetEnvironmentVariable("GT_AO_ITEM");
                if (!string.IsNullOrEmpty(e) && UUID.TryParse(e.Trim(), out var u1) && u1 != RetiredAwpAo) aoItemCached = u1;
                else try { if (File.Exists(AoItemFile) && UUID.TryParse(File.ReadAllText(AoItemFile).Split('#')[0].Trim(), out var u2) && u2 != RetiredAwpAo) aoItemCached = u2; } catch { }
            }
            try
            {
                foreach (var p in WornPrims())
                {
                    var n = p.Properties?.Name ?? "";
                    if (n.Contains(AoNameMatch, StringComparison.OrdinalIgnoreCase))
                    {
                        var it = AttachItemId(p);
                        if (it != UUID.Zero && it != RetiredAwpAo && it != aoItemCached)
                        {
                            aoItemCached = it;
                            try { File.WriteAllText(AoItemFile, $"{it}  # {n} (learned {DateTime.Now:yyyy-MM-dd HH:mm})\n"); } catch { }
                            Log("ao-guard", $"AO item learned from worn '{n}': {it}");
                        }
                        break;
                    }
                }
            }
            catch { }
            return aoItemCached;
        }
    }
    static bool AoHudWorn()
    {
        try
        {
            var id = AoItem; var worn = WornByItem();
            if (id != UUID.Zero && worn.ContainsKey(id)) return true;
            return WornPrims().Any(p => (p.Properties?.Name ?? "").Contains(AoNameMatch, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }
    static readonly HashSet<UUID> AoDefaultLoco = new() { Animations.STAND, Animations.STAND_1, Animations.STAND_2, Animations.STAND_3, Animations.STAND_4, Animations.WALK, Animations.FEMALE_WALK };
    static string aoLastEvent = "-";
    static void AoLog(string m) { aoLastEvent = $"{DateTime.Now:HH:mm:ss} {m}"; Log("ao-guard", m); }

    // pure (unit-tested)
    // Martha (Vista) plays its stands/walks from the HUD itself (source = the HUD object) on top of the default
    // STAND/WALK, so an animation sourced from the worn AO HUD counts as the AO running (2026-09-29).
    static (bool active, string why) AoStateFrom(IEnumerable<(UUID id, UUID src)> anims, bool hudWorn, bool seated, Func<UUID, bool> isBuiltin, Func<UUID, string> name, ICollection<UUID> hudSrcs = null)
    {
        if (seated) return (false, "seated (the AO is off while seated by the seat rule; checked after standing)");
        if (!hudWorn) return (false, "AO HUD not worn");
        var list = anims.ToList();
        if (hudSrcs != null && hudSrcs.Count > 0)
        {
            var fromHud = list.Where(a => hudSrcs.Contains(a.src) && !isBuiltin(a.id)).ToList();
            if (fromHud.Count > 0) return (true, "Martha AO animation playing: " + string.Join(",", fromHud.Select(o => o.id.ToString()[..8])));
        }
        var defs = list.Where(a => AoDefaultLoco.Contains(a.id)).ToList();
        if (defs.Count > 0) return (false, "default " + string.Join(",", defs.Select(d => name(d.id))) + " playing (AO overrides not active)");
        var ovr = list.Where(a => a.src == UUID.Zero && !isBuiltin(a.id)).ToList();
        if (ovr.Count == 0) return (false, "no AO override animation playing");
        return (true, "AO override playing: " + string.Join(",", ovr.Select(o => o.id.ToString()[..8])));
    }
    static (bool active, string why) AoStateNow()
    {
        if (!LoggedIn) return (false, "not logged in");
        Dictionary<UUID, (int seq, UUID src)> cur; lock (animLock) cur = ownAnims;
        bool worn = AoHudWorn();
        var srcs = new HashSet<UUID>();
        try { foreach (var p in WornPrims()) if ((p.Properties?.Name ?? "").Contains(AoNameMatch, StringComparison.OrdinalIgnoreCase) || (AoItem != UUID.Zero && AttachItemId(p) == AoItem)) srcs.Add(p.ID); } catch { }
        return AoStateFrom(cur.Select(kv => (kv.Key, kv.Value.src)), worn, client.Self.SittingOn != 0, id => BuiltinAnims.ContainsKey(id), AnimName, srcs);
    }
    static async Task<bool> AoWaitActive(int ms, CancellationToken ct)
    {
        var until = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < until) { if (AoStateNow().active) return true; await Task.Delay(500, ct); }
        return AoStateNow().active;
    }
    // Martha keeps its power/settings only while worn, so a WORN Martha is never detached/re-attached here;
    // restore = attach Martha at HUD Center only if it is missing. Never attaches AWP or any other HUD.
    static async Task<bool> AoRestore(string why, CancellationToken ct)
    {
        client.Self.AutoPilotCancel();
        if (client.Self.SittingOn != 0) { AoLog("restore skipped: she is seated"); return false; }
        if (AoHudWorn())
        {
            AoLog($"restore ({why}): Martha HUD is worn; not detaching it (would lose power state); waiting 25 s more");
        }
        else
        {
            var id = AoItem;
            if (id == UUID.Zero || id == RetiredAwpAo) { AoLog($"restore ({why}): Martha item id unknown; not attaching anything"); return false; }
            AoLog($"restoring the AO ({why}): attach Martha {id} at HUD Center");
            var r = await AttachItem(id, AoPoint.ToString());
            AoLog("attach: " + r);
        }
        bool ok = await AoWaitActive(25000, ct);
        AoLog(ok ? "AO restored: " + AoStateNow().why : "AO NOT restored after 25 s: " + AoStateNow().why + " - staying put");
        return ok;
    }
    // call after standing, before any walk; null = OK to walk, else the refusal reason
    static async Task<string> AoGuardBeforeWalk(string label, CancellationToken ct)
    {
        if (client.Self.SittingOn != 0) return "she is seated";
        if (AoStateNow().active) return null;
        AoLog($"{label}: waiting for the AO before walking ({AoStateNow().why})");
        if (await AoWaitActive(25000, ct)) { AoLog($"{label}: AO active, walking ({AoStateNow().why})"); return null; }
        if (await AoRestore($"{label}: not active after 25 s", ct)) return null;
        client.Self.AutoPilotCancel();
        var m = $"AO not active ({AoStateNow().why}); not walking, staying put";
        AoLog($"{label}: REFUSED - {m}");
        return m;
    }
    static string AoFlag() { var (a, _) = AoStateNow(); return client.Self.SittingOn != 0 ? "seated" : a ? "ON" : "OFF"; }
    static string AoStatus() { var (a, why) = AoStateNow(); return $"AO guard: {(a ? "ACTIVE" : "not active")} - {why}; last guard event: {aoLastEvent}"; }

    static string AoSelfTest()
    {
        var lines = new List<string>(); int pass = 0, fail = 0;
        bool B(UUID id) => id == Animations.STAND || id == Animations.WALK || id == Animations.FEMALE_WALK || id == Animations.STRIDE || id == Animations.STAND_1;
        string N(UUID id) => id == Animations.STAND ? "STAND" : id == Animations.WALK ? "WALK" : id == Animations.FEMALE_WALK ? "FEMALE_WALK" : id.ToString()[..8];
        var aoStand = new UUID("bc4ad1e9-10fd-c04d-f618-c6bcaa4eb442"); var aoWalk = new UUID("dc74015d-4124-3455-ca6c-a70a46fdd65d");
        var hudAnim = new UUID("dccb493a-93f0-0903-041e-6a7469846d11"); var hudSrc = UUID.Random();
        void T(string name, List<(UUID, UUID)> anims, bool worn, bool seated, bool expect)
        {
            var (a, why) = AoStateFrom(anims, worn, seated, B, N); bool ok = a == expect; if (ok) pass++; else fail++;
            lines.Add($"{(ok ? "PASS" : "FAIL")} {name} -> {(a ? "active" : "NOT active")} ({why})");
        }
        T("AO stand playing (HUD worn)", new() { (aoStand, UUID.Zero), (hudAnim, hudSrc) }, true, false, true);
        T("AO walk + STRIDE while walking", new() { (aoWalk, UUID.Zero), (Animations.STRIDE, UUID.Zero) }, true, false, true);
        T("default STAND (AO toggled off, 23:25:20 case)", new() { (Animations.STAND, UUID.Zero), (hudAnim, hudSrc) }, true, false, false);
        T("default WALK while walking", new() { (Animations.WALK, UUID.Zero) }, true, false, false);
        T("override + default FEMALE_WALK together", new() { (aoStand, UUID.Zero), (Animations.FEMALE_WALK, UUID.Zero) }, true, false, false);
        T("HUD not worn", new() { (aoStand, UUID.Zero) }, false, false, false);
        T("only attachment/hand anims, no override", new() { (hudAnim, hudSrc) }, true, false, false);
        T("seated -> not active (checked after standing)", new() { (aoStand, UUID.Zero) }, true, true, false);
        {
            var mh = UUID.Random(); var ms = new UUID("1feb7de9-42e5-4b9d-d19c-4397534fa406");
            var (a1, w1) = AoStateFrom(new List<(UUID, UUID)> { (Animations.STAND, UUID.Zero), (ms, mh) }, true, false, B, N, new HashSet<UUID> { mh });
            if (a1) pass++; else fail++; lines.Add($"{(a1 ? "PASS" : "FAIL")} Martha stand from HUD over default STAND -> {(a1 ? "active" : "NOT active")} ({w1})");
            var (a2, w2) = AoStateFrom(new List<(UUID, UUID)> { (Animations.STAND, UUID.Zero) }, true, false, B, N, new HashSet<UUID> { mh });
            if (!a2) pass++; else fail++; lines.Add($"{(!a2 ? "PASS" : "FAIL")} Martha worn but powered off (default STAND only) -> {(a2 ? "active" : "NOT active")} ({w2})");
        }
        return $"AO guard selftest: {pass} pass, {fail} fail (offline)\n" + string.Join("\n", lines);
    }
}
