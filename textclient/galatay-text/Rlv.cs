// Rlv.cs (2026-10-19, David: "Add RLV support. We will start with undressing you and placing you on furniture.")
// Phase 1 RLV (RestrainedLove API) for Galatay's text client, our own implementation of the public RLV protocol:
//   - answers @version/@versionnew/@versionnum/@versionnumbl so worn RLV items (the N° 9 collar) see "RLV ready"
//   - accepts commands ONLY from her own worn attachments (llOwnerSay "@..."); no relay for world objects
//   - =force: sit:<uuid>, unsit, remoutfit[:layer], detach[:point|:name|:#RLV folder], detachall:<#RLV folder>
//   - y/n restrictions tracked per source object, @clear honoured, dropped when the source is no longer worn (60 s grace)
//   - queries: @getstatus, @getstatusall, @getoutfit, @getattach, @getinv, @getsitid
//   - enforced on her own commands: unsit (stand), sit, sendchat (say, MAYDAY always passes), detach=n locks the source
//   - hard rules: always-wear / keep-attached items (the N° 9 collar), body/head/hair and HUDs are never detached by RLV;
//     nothing in RLV moves L$; safeword MAYDAY always reaches chat and also clears every local restriction; 'rlv clear'
//     is the local emergency override.
//   - every received @ line is logged to /workspace/secondlife/rlv/rlv-log.md (to learn the collar/NX Remote protocol)
// rlv status | rlv clear | rlv log [n]
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal const string RlvVersionOld = "RestrainedLife viewer v3.4.3 (RLVa 2.4.2)";
    internal const string RlvVersionNew = "RestrainedLove viewer v3.4.3 (RLVa 2.4.2)";
    internal const string RlvVersionNum = "3040300";
    internal const string Safeword = "MAYDAY";
    static readonly string RlvLogFile = Env("GT_RLV_LOG", "/workspace/secondlife/rlv/rlv-log.md");

    internal sealed record RlvCmd(string Behav, string Option, string Param);

    // pure (tested): "@a:b=c,d=n,clear" -> commands; null when not an RLV line
    internal static List<RlvCmd> RlvParse(string msg)
    {
        if (string.IsNullOrEmpty(msg) || msg[0] != '@') return null;
        var list = new List<RlvCmd>();
        foreach (var raw in msg[1..].Split(','))
        {
            var s = raw.Trim(); if (s.Length == 0) continue;
            string param = ""; var eq = s.IndexOf('=');
            if (eq >= 0) { param = s[(eq + 1)..].Trim(); s = s[..eq]; }
            string opt = ""; var co = s.IndexOf(':');
            if (co >= 0) { opt = s[(co + 1)..].Trim(); s = s[..co]; }
            list.Add(new RlvCmd(s.Trim().ToLowerInvariant(), opt, param.ToLowerInvariant() is "y" or "n" or "rem" or "add" or "force" ? param.ToLowerInvariant() : param));
        }
        return list;
    }

    // pure (tested): version query reply, null when not a version query
    internal static string RlvVersionReply(string behav) => behav switch
    {
        "version" => RlvVersionOld, "versionnew" => RlvVersionNew, "versionnum" => RlvVersionNum, "versionnumbl" => RlvVersionNum, _ => null
    };

    internal static bool IsSafeword(string text) => Regex.IsMatch(text ?? "", @"^\W*\(*\s*MAYDAY\s*\)*\W*$", RegexOptions.IgnoreCase);

    // per-source restriction store (pure, tested)
    internal sealed class RlvStore
    {
        readonly object l = new();
        readonly Dictionary<UUID, (string name, HashSet<string> r)> src = new();
        static string Key(string b, string o) => o.Length == 0 ? b : b + ":" + o;
        public void Add(UUID s, string name, string b, string o) { lock (l) { if (!src.TryGetValue(s, out var e)) src[s] = e = (name, new()); e.r.Add(Key(b, o)); } }
        public void Remove(UUID s, string b, string o) { lock (l) if (src.TryGetValue(s, out var e)) { e.r.Remove(Key(b, o)); if (e.r.Count == 0) src.Remove(s); } }
        public int Clear(UUID s, string filter)
        {
            lock (l)
            {
                if (!src.TryGetValue(s, out var e)) return 0;
                int n = e.r.RemoveWhere(k => filter.Length == 0 || k.Contains(filter, StringComparison.OrdinalIgnoreCase));
                if (e.r.Count == 0) src.Remove(s); return n;
            }
        }
        public int ClearAll() { lock (l) { int n = src.Values.Sum(e => e.r.Count); src.Clear(); return n; } }
        public void Drop(UUID s) { lock (l) src.Remove(s); }
        public List<UUID> Sources() { lock (l) return src.Keys.ToList(); }
        public bool Has(string b, string opt = null)
        {
            lock (l) return src.Values.Any(e => e.r.Contains(b) || (opt != null && e.r.Contains(b + ":" + opt)));
        }
        public bool HasFrom(UUID s, string k) { lock (l) return src.TryGetValue(s, out var e) && e.r.Contains(k); }
        public string Status(UUID? s, string filter, string sep)
        {
            lock (l)
            {
                var keys = (s is UUID one ? (src.TryGetValue(one, out var e) ? e.r : new HashSet<string>()) : src.Values.SelectMany(x => x.r))
                    .Where(k => filter.Length == 0 || k.Contains(filter, StringComparison.OrdinalIgnoreCase)).Distinct().OrderBy(k => k, StringComparer.Ordinal);
                return string.Concat(keys.Select(k => sep + k));
            }
        }
        public string Describe() { lock (l) return src.Count == 0 ? "none" : string.Join("; ", src.Select(kv => $"'{kv.Value.name}' ({kv.Key}): {string.Join(",", kv.Value.r.OrderBy(x => x))}")); }
    }
    internal static readonly RlvStore rlv = new();
    static readonly ConcurrentDictionary<UUID, DateTime> rlvMissingSince = new();

    // RLV attachment point names, index = attachment point number (@getattach order)
    internal static readonly string[] RlvPoints = {
        "none", "chest", "skull", "left shoulder", "right shoulder", "left hand", "right hand", "left foot", "right foot", "spine",
        "pelvis", "mouth", "chin", "left ear", "right ear", "left eyeball", "right eyeball", "nose", "r upper arm", "r forearm",
        "l upper arm", "l forearm", "right hip", "r upper leg", "r lower leg", "left hip", "l upper leg", "l lower leg", "stomach",
        "left pec", "right pec", "center 2", "top right", "top", "top left", "center", "bottom left", "bottom", "bottom right", "neck",
        "avatar center", "left ring finger", "right ring finger", "tail base", "tail tip", "left wing", "right wing", "jaw",
        "alt left ear", "alt right ear", "alt left eye", "alt right eye", "tongue", "groin", "left hind foot", "right hind foot" };
    internal static int RlvPointIndex(string name)
    {
        var n = (name ?? "").Trim().ToLowerInvariant();
        for (int i = 1; i < RlvPoints.Length; i++) if (RlvPoints[i] == n) return i;
        return -1;
    }

    // RLV clothing layers in @getoutfit order
    internal static readonly (string name, WearableType t)[] RlvLayers = {
        ("gloves", WearableType.Gloves), ("jacket", WearableType.Jacket), ("pants", WearableType.Pants), ("shirt", WearableType.Shirt),
        ("shoes", WearableType.Shoes), ("skirt", WearableType.Skirt), ("socks", WearableType.Socks), ("underpants", WearableType.Underpants),
        ("undershirt", WearableType.Undershirt), ("skin", WearableType.Skin), ("eyes", WearableType.Eyes), ("hair", WearableType.Hair),
        ("shape", WearableType.Shape), ("alpha", WearableType.Alpha), ("tattoo", WearableType.Tattoo), ("physics", WearableType.Physics),
        ("universal", WearableType.Universal) };

    internal static string RlvOutfitString(ICollection<WearableType> worn, string layer)
    {
        if (layer.Length > 0) { var m = RlvLayers.FirstOrDefault(x => x.name == layer.ToLowerInvariant()); return m.name == null ? "" : worn.Contains(m.t) ? "1" : "0"; }
        return string.Concat(RlvLayers.Select(x => worn.Contains(x.t) ? '1' : '0'));
    }
    internal static string RlvAttachString(ICollection<int> points, string pt)
    {
        if (pt.Length > 0) { var i = RlvPointIndex(pt); return i < 0 ? "" : points.Contains(i) ? "1" : "0"; }
        return string.Concat(Enumerable.Range(0, RlvPoints.Length).Select(i => points.Contains(i) ? '1' : '0'));
    }
    // layers @remoutfit may remove (body parts never)
    internal static List<WearableType> RlvRemovableLayers(string layer) =>
        RlvLayers.Where(x => x.t is not (WearableType.Shape or WearableType.Skin or WearableType.Hair or WearableType.Eyes)
                             && (layer.Length == 0 || x.name == layer.ToLowerInvariant())).Select(x => x.t).ToList();

    internal sealed record RlvWorn(UUID Item, UUID Prim, string Name, int Point);
    // pure (tested): which worn attachments a @detach[:opt]=force takes off. Never: protected items (always-wear collar,
    // keep-attached, AO), detach-locked sources, body/head/hair, HUDs. opt = point name or exact object name ("" = all).
    internal static List<RlvWorn> RlvDetachPlan(IEnumerable<RlvWorn> worn, string opt, ISet<UUID> protectedItems, ISet<UUID> lockedPrims)
    {
        int pt = opt.Length > 0 ? RlvPointIndex(opt) : -1;
        return worn.Where(w => w.Item != UUID.Zero && !protectedItems.Contains(w.Item) && !lockedPrims.Contains(w.Prim)
                               && OutfitGroup(w.Name) == null && !(w.Point >= 31 && w.Point <= 38)
                               && (opt.Length == 0 || (pt >= 0 ? w.Point == pt : string.Equals(w.Name, opt, StringComparison.OrdinalIgnoreCase))))
                   .ToList();
    }

    static void RlvLog(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RlvLogFile));
            if (!File.Exists(RlvLogFile)) File.WriteAllText(RlvLogFile, "# RLV log (Rlv.cs): every @ command from my own worn attachments, and what I did\n\n");
            File.AppendAllText(RlvLogFile, $"- {DateTime.Now:yyyy-MM-dd HH:mm:ss} {line.Replace('\n', ' ')}\n");
        }
        catch { }
        Log("rlv", line);
    }

    // own worn attachment root for a prim id (child prims included); null when not hers
    static Primitive RlvOwnAttachmentRoot(UUID primId)
    {
        var sim = client?.Network?.CurrentSim; var me = client?.Self?.LocalID ?? 0;
        if (sim == null || me == 0) return null;
        var p = sim.ObjectsPrimitives.Values.FirstOrDefault(x => x != null && x.ID == primId);
        for (int k = 0; p != null && k < 4; k++)
        {
            if (p.ParentID == me) return p;
            var parent = p.ParentID; p = parent == 0 ? null : sim.ObjectsPrimitives.Values.FirstOrDefault(x => x != null && x.LocalID == parent);
        }
        return null;
    }

    static void RlvHook()
    {
        client.Self.ChatFromSimulator += (s, e) =>
        {
            if (e.Type != ChatType.OwnerSay || string.IsNullOrEmpty(e.Message) || e.Message[0] != '@') return;
            if (e.SourceType != ChatSourceType.Object || e.OwnerID != client.Self.AgentID) return;
            var (id, name, msg) = (e.SourceID, e.FromName, e.Message);
            _ = Task.Run(() => RlvHandle(id, name, msg));
        };
        _ = Task.Run(async () => { while (true) { await Task.Delay(15000); try { RlvSweep(); } catch { } } });
    }

    static async Task RlvHandle(UUID srcPrim, string srcName, string msg)
    {
        Primitive root = null;
        for (int i = 0; i < 20 && (root = RlvOwnAttachmentRoot(srcPrim)) == null; i++) await Task.Delay(500); // just-attached: prims may lag
        if (root == null) { RlvLog($"IGNORED (not one of my worn attachments) '{srcName}' ({srcPrim}): {msg}"); return; }
        var cmds = RlvParse(msg) ?? new();
        var res = new List<string>();
        foreach (var c in cmds)
        {
            string r;
            try { r = await RlvOne(srcPrim, srcName, c); } catch (Exception ex) { r = "error " + ex.GetBaseException().Message; }
            res.Add($"{c.Behav}{(c.Option.Length > 0 ? ":" + c.Option : "")}={c.Param} -> {r}");
        }
        RlvLog($"'{srcName}' ({srcPrim}, item {AttachItemId(root)}): `{msg}` | {string.Join(" | ", res)}");
    }

    static bool RlvChannel(string p, out int ch) => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out ch) && ch > 0;
    static void RlvReply(int ch, string text) => client.Self.Chat(text, ch, ChatType.Normal);
    static readonly AsyncLocal<bool> rlvForcing = new();

    static async Task<string> RlvOne(UUID src, string srcName, RlvCmd c)
    {
        var b = c.Behav; var o = c.Option; var p = c.Param;
        var v = RlvVersionReply(b);
        if (v != null) { if (!RlvChannel(p, out var ch)) return "bad channel"; RlvReply(ch, v); return $"replied '{v}' on {ch}"; }
        if (b == "clear") return $"cleared {rlv.Clear(src, p.Length > 0 ? p : o)}";
        if (p is "n" or "add") { rlv.Add(src, srcName, b, o); return "restricted"; }
        if (p is "y" or "rem") { rlv.Remove(src, b, o); return "lifted"; }
        if (p == "force") return await RlvForce(b, o);
        if (b.StartsWith("get") || b == "findfolder")
        {
            if (!RlvChannel(p, out var ch)) return "bad channel";
            string ans = b switch
            {
                "getstatus" => rlv.Status(src, o.Split(';')[0], o.Contains(';') ? o.Split(';')[1] : "/"),
                "getstatusall" => rlv.Status(null, o.Split(';')[0], o.Contains(';') ? o.Split(';')[1] : "/"),
                "getoutfit" => RlvOutfitString(SafeWearables().Select(w => w.WearableType).ToHashSet(), o),
                "getattach" => RlvAttachString(WornPrims().Select(x => (int)x.PrimData.AttachmentPoint).ToHashSet(), o),
                "getsitid" => client.Self.SittingOn == 0 ? UUID.Zero.ToString() : (Sim.ObjectsPrimitives.TryGetValue(client.Self.SittingOn, out var sp) ? sp.ID.ToString() : UUID.Zero.ToString()),
                "getinv" => await RlvGetInv(o),
                _ => null
            };
            if (ans == null) return "unsupported query (not answered)";
            RlvReply(ch, ans); return $"replied '{ans}' on {ch}";
        }
        if (p.Length == 0 || RlvChannel(p, out _)) return "noted (unsupported)";
        return "ignored";
    }

    static List<AppearanceManager.WearableData> SafeWearables() { try { return client.Appearance.GetWearables().ToList(); } catch { return new(); } }

    static async Task<string> RlvForce(string b, string o)
    {
        using var cts = new CancellationTokenSource(90000);
        switch (b)
        {
            case "sit":
                if (!UUID.TryParse(o, out var seat)) return "bad uuid";
                if (client.Self.SittingOn != 0 && rlv.Has("unsit")) return "refused: @unsit restricted and already seated";
                rlvForcing.Value = true; return await Exec("sit " + seat);
            case "unsit":
                rlvForcing.Value = true; return await Exec("stand");
            case "remoutfit":
            {
                var types = RlvRemovableLayers(o);
                if (types.Count == 0) return "nothing removable for that layer";
                return await WithWardrobe("rlv remoutfit", async () =>
                {
                    var items = new List<InventoryItem>();
                    foreach (var w in SafeWearables().Where(w => types.Contains(w.WearableType)).GroupBy(w => w.ItemID).Select(g => g.First()))
                    { var it = await FetchItemRO(w.ItemID, cts.Token); if (it != null) items.Add(it); }
                    if (items.Count == 0) return "no such layer worn";
                    client.Appearance.RemoveFromOutfit(items);
                    await CofLinksOffBatch(items.Select(i => i.UUID), "rlv remoutfit", cts.Token);
                    await RebakeNow("rlv remoutfit"); WardrobeUndressedNow();
                    return "removed " + string.Join(", ", items.Select(i => "'" + i.Name + "'"));
                }, cts.Token);
            }
            case "detach": case "remattach": case "detachall":
            {
                var worn = await RlvWornList();
                List<RlvWorn> plan;
                bool folder = b == "detachall" || (o.Length > 0 && RlvPointIndex(o) < 0 && !worn.Any(w => string.Equals(w.Name, o, StringComparison.OrdinalIgnoreCase)));
                if (folder)
                {
                    if (o.Length == 0) return "detachall needs a folder";
                    var ids = await RlvFolderItems(o, b == "detachall", cts.Token);
                    if (ids == null) return "no such #RLV folder";
                    plan = RlvDetachPlan(worn.Where(w => ids.Contains(w.Item)), "", OutfitProtectedIds(), RlvLockedPrims());
                }
                else plan = RlvDetachPlan(worn, o, OutfitProtectedIds(), RlvLockedPrims());
                if (plan.Count == 0) return "nothing to detach (protected, locked or not worn)";
                return await WithWardrobe("rlv detach", async () =>
                {
                    foreach (var w in plan) DetachItem(w.Item, "rlv detach");
                    await CofLinksOffBatch(plan.Select(w => w.Item), "rlv detach", cts.Token);
                    WardrobeUndressedNow();
                    return "detached " + string.Join(", ", plan.Select(w => "'" + w.Name + "'"));
                }, cts.Token);
            }
            default: return "unsupported force (not done)";
        }
    }

    static async Task<List<RlvWorn>> RlvWornList()
    {
        var roots = WornPrims(); try { await EnsureProperties(Sim, roots); } catch { }
        return roots.Select(r => new RlvWorn(AttachItemId(r), r.ID, r.Properties?.Name ?? "", (int)r.PrimData.AttachmentPoint)).ToList();
    }

    // prims that hold @detach=n on themselves (locked), and their items for outfit protection
    static HashSet<UUID> RlvLockedPrims() => rlv.Sources().Where(s => rlv.HasFrom(s, "detach")).ToHashSet();
    internal static IEnumerable<UUID> RlvLockedItems()
    {
        var locked = RlvLockedPrims(); if (locked.Count == 0) return Array.Empty<UUID>();
        return WornPrims().Where(p => locked.Contains(p.ID)).Select(AttachItemId).Where(i => i != UUID.Zero).ToList();
    }

    static async Task<InventoryFolder> RlvFolder(string path, CancellationToken ct)
    {
        var root = client.Inventory.Store?.RootFolder?.UUID ?? UUID.Zero; if (root == UUID.Zero) return null;
        var cur = (await ReadFolderRO(root, ct)).OfType<InventoryFolder>().FirstOrDefault(f => f.Name == "#RLV");
        foreach (var part in (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cur == null) return null;
            cur = (await ReadFolderRO(cur.UUID, ct)).OfType<InventoryFolder>().FirstOrDefault(f => string.Equals(f.Name, part, StringComparison.OrdinalIgnoreCase));
        }
        return cur;
    }
    static async Task<HashSet<UUID>> RlvFolderItems(string path, bool recursive, CancellationToken ct)
    {
        var f = await RlvFolder(path, ct); if (f == null) return null;
        var ids = new HashSet<UUID>(); var todo = new Queue<UUID>(); todo.Enqueue(f.UUID);
        while (todo.Count > 0)
            foreach (var k in await ReadFolderRO(todo.Dequeue(), ct))
                if (k is InventoryFolder sub) { if (recursive) todo.Enqueue(sub.UUID); }
                else if (k is InventoryItem it) { ids.Add(it.UUID); if (it.AssetUUID != UUID.Zero) ids.Add(it.AssetUUID); } // links point at the item
        return ids;
    }
    static async Task<string> RlvGetInv(string path)
    {
        using var cts = new CancellationTokenSource(30000);
        var f = await RlvFolder(path, cts.Token); if (f == null) return "";
        return string.Join(",", (await ReadFolderRO(f.UUID, cts.Token)).OfType<InventoryFolder>().Select(x => x.Name).Where(n => !n.StartsWith(".")));
    }

    // drop restrictions of sources no longer worn for 60 s (teleports/region changes briefly empty the object list)
    static void RlvSweep()
    {
        if (!LoggedIn) return;
        foreach (var s in rlv.Sources())
        {
            if (RlvOwnAttachmentRoot(s) != null) { rlvMissingSince.TryRemove(s, out _); continue; }
            var since = rlvMissingSince.GetOrAdd(s, DateTime.Now);
            if ((DateTime.Now - since).TotalSeconds >= 60) { rlv.Drop(s); rlvMissingSince.TryRemove(s, out _); RlvLog($"source {s} no longer worn: its restrictions dropped"); }
        }
    }

    // her own actions under restrictions (null = allowed). RLV forces bypass sit/unsit; MAYDAY always passes.
    internal static string RlvBlocks(string cmd, string text, bool forcing, Func<string, bool> has) => cmd switch
    {
        "stand" when !forcing && has("unsit") => "refused (RLV @unsit is on; say MAYDAY or use 'rlv clear' to release)",
        "sit" when !forcing && (has("sit") || has("unsit")) => "refused (RLV @sit/@unsit is on; say MAYDAY or use 'rlv clear' to release)",
        "say" or "shout" or "whisper" when has("sendchat") && !IsSafeword(text) => "refused (RLV @sendchat is on; MAYDAY still works)",
        _ => null
    };
    static string RlvGuard(string cmd, string rest) => RlvBlocks(cmd, rest, rlvForcing.Value, b => rlv.Has(b));

    static string RlvCmdText(string[] a)
    {
        switch (a.Length > 0 ? a[0] : "status")
        {
            case "status": return $"RLV on ({RlvVersionNew}); relay for world objects: off; restrictions: {rlv.Describe()}; log {RlvLogFile}";
            case "clear": { var n = rlv.ClearAll(); RlvLog($"LOCAL OVERRIDE 'rlv clear': {n} restriction(s) cleared"); return $"cleared {n} restriction(s) locally"; }
            case "log":
            {
                int n = a.Length > 1 && int.TryParse(a[1], out var x) ? Math.Clamp(x, 1, 200) : 30;
                try { return File.Exists(RlvLogFile) ? string.Join("\n", File.ReadLines(RlvLogFile).TakeLast(n)) : "no RLV log yet"; } catch (Exception ex) { return "error " + ex.Message; }
            }
            default: return "usage: rlv status | rlv clear | rlv log [n]";
        }
    }
}
