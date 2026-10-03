// OutfitSave.cs (2026-09-26 13:20, David: save the current look as outfit "Original"; find clothing HUDs).
// inv find <text>          READ-ONLY recursive inventory search (path, name, type, item id, desc, last attach point);
//                          time-boxed to 30 s per call, partial results resume on the next call (2026-10-03)
// outfit plan <name> [id,id,...]    dry run: links that 'outfit create' would make
// outfit create <name> [id,id,...]  new folder (type Outfit) under My Outfits + links to the ORIGINAL items of every COF link
//                                    (minus the Firestorm LSL Bridge and the COF folder link) plus the extra item ids (not worn).
// Never deletes, never wears, never edits the COF.
using System.Text;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static readonly List<(string path, InventoryBase node)> invIndex = new(); static DateTime invIndexTime = DateTime.MinValue;
    // 2026-10-03 (inv find hung for an hour): the index is built in time-boxed, RESUMABLE steps. Each 'inv find' reads folders
    // (4 at a time, each read capped by ReadFolderTimed) for at most InvFindBudget, then answers with what it has (marked
    // PARTIAL); the next 'inv find' continues where it stopped. A complete index is reused for 10 min.
    static readonly TimeSpan InvFindBudget = TimeSpan.FromSeconds(30);
    static readonly SemaphoreSlim invGate = new(1, 1);
    static Queue<(UUID id, string path, int tries)> invPending; static List<(string, InventoryBase)> invPartial; static int invFoldersRead, invFoldersSkipped;
    static bool invIndexComplete;

    static async Task<(int read, bool complete)> BuildInvIndex(TimeSpan budget, CancellationToken ct)
    {
        var root = client.Inventory.Store?.RootFolder; if (root == null) return (0, false);
        if (invPending == null || invPending.Count == 0)
        {
            invPending = new(); invPending.Enqueue((root.UUID, "", 0)); invPartial = new(); invFoldersRead = 0; invFoldersSkipped = 0;
        }
        var deadline = DateTime.UtcNow + budget; int read = 0;
        while (invPending.Count > 0 && !ct.IsCancellationRequested && invFoldersRead < 5000)
        {
            var left = deadline - DateTime.UtcNow; if (left <= TimeSpan.FromMilliseconds(500)) break;
            var wave = new List<(UUID id, string path, int tries)>();
            while (wave.Count < 4 && invPending.Count > 0) wave.Add(invPending.Dequeue());
            int ms = (int)Math.Min(20000, left.TotalMilliseconds);
            var res = await Task.WhenAll(wave.Select(async w => (w, r: await ReadFolderTimed(w.id, ct, ms))));
            foreach (var (w, r) in res)
            {
                if (!r.ok)
                {
                    if (w.tries < 1) invPending.Enqueue((w.id, w.path, w.tries + 1)); // one retry later
                    else { invFoldersSkipped++; Log("outfit", $"inv index: skipping folder {w.id} ({(w.path.Length == 0 ? "/" : w.path)}) after 2 failed reads"); }
                    continue;
                }
                read++; invFoldersRead++;
                foreach (var k in r.kids)
                {
                    if (k.ParentUUID != w.id) continue; // FetchInventoryDescendents2 may include link targets from elsewhere
                    if (k is InventoryFolder f) { invPartial.Add((w.path, f)); invPending.Enqueue((f.UUID, w.path + "/" + f.Name, 0)); }
                    else invPartial.Add((w.path, k));
                }
            }
        }
        bool complete = invPending.Count == 0;
        lock (invIndex) { invIndex.Clear(); invIndex.AddRange(invPartial); invIndexTime = DateTime.Now; invIndexComplete = complete; }
        return (read, complete);
    }

    static async Task<string> InvFind(string text)
    {
        if (!LoggedIn) return "not logged in";
        if (!await invGate.WaitAsync(TimeSpan.FromSeconds(5))) return "another 'inv find' is still reading the inventory; try again in ~30 s";
        int read = 0; bool building = false;
        try
        {
            using var cts = new CancellationTokenSource(InvFindBudget + TimeSpan.FromSeconds(5));
            if (!invIndexComplete || (DateTime.Now - invIndexTime).TotalMinutes > 10) { building = true; (read, _) = await BuildInvIndex(InvFindBudget, cts.Token); }
        }
        finally { invGate.Release(); }
        List<(string path, InventoryBase node)> snap; bool complete; lock (invIndex) { snap = invIndex.ToList(); complete = invIndexComplete; }
        var terms = text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hits = snap.Where(x => terms.Any(t => x.node.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || x.path.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
        var state = complete ? (building ? $"complete ({read} folders read now{(invFoldersSkipped > 0 ? $", {invFoldersSkipped} unreadable skipped" : "")})" : $"cached {invIndexTime:HH:mm:ss}")
                             : $"PARTIAL after {InvFindBudget.TotalSeconds:F0} s ({invFoldersRead} folders read, {invPending?.Count ?? 0} still to read; run 'inv find' again to continue)";
        var sb = new StringBuilder($"inventory index: {snap.Count} entries, {state}; {hits.Count} match '{text}':\n");
        foreach (var (path, n) in hits.Take(150))
        {
            if (n is InventoryFolder f) sb.AppendLine($"  [folder] {path}/{f.Name}  {f.UUID} type {f.PreferredType}");
            else if (n is InventoryItem i)
            {
                string pt = i.InventoryType == InventoryType.Object || i.InventoryType == InventoryType.Attachment ? $" lastpoint #{i.Flags & 0xFF}" : "";
                sb.AppendLine($"  {path} | '{i.Name}' {i.AssetType}/{i.InventoryType}{(i.IsLink() ? " LINK->" + i.AssetUUID : "")} item {i.UUID}{pt} desc '{i.Description}'");
            }
        }
        return sb.ToString().TrimEnd();
    }

    sealed record PlannedLink(UUID Target, string Name, InventoryType Inv, AssetType Asset, string Desc, string Why);

    static async Task<(List<PlannedLink> plan, List<string> problems, InventoryFolder myOutfits, List<InventoryBase> moKids)> PlanOutfit(string[] extraIds, CancellationToken ct)
    {
        var plan = new List<PlannedLink>(); var problems = new List<string>();
        var root = client.Inventory.Store?.RootFolder; if (root == null) { problems.Add("inventory root not known"); return (plan, problems, null, null); }
        var rootKids = await ReadFolderRO(root.UUID, ct);
        var cof = rootKids.OfType<InventoryFolder>().FirstOrDefault(f => f.PreferredType == FolderType.CurrentOutfit && f.ParentUUID == root.UUID);
        var mo = rootKids.OfType<InventoryFolder>().Where(f => f.PreferredType == FolderType.MyOutfits && f.ParentUUID == root.UUID).ToList();
        if (cof == null) problems.Add("Current Outfit folder not found");
        if (mo.Count != 1) problems.Add($"expected exactly one My Outfits folder in the root, found {mo.Count}");
        var myOutfits = mo.FirstOrDefault();
        var moKids = myOutfits == null ? new List<InventoryBase>() : (await ReadFolderRO(myOutfits.UUID, ct)).Where(k => k.ParentUUID == myOutfits.UUID).ToList();
        if (cof != null)
        {
            var links = (await ReadFolderRO(cof.UUID, ct)).OfType<InventoryItem>().Where(l => l.ParentUUID == cof.UUID).ToList();
            foreach (var l in links.OrderBy(l => l.Name))
            {
                if (l.AssetType == AssetType.LinkFolder) { problems.Add($"skip COF folder link '{l.Name}' (outfit marker, not an item)"); continue; }
                if (!l.IsLink()) { problems.Add($"skip COF entry '{l.Name}' that is not a link"); continue; }
                var t = await FetchItemRO(l.AssetUUID, ct);
                if (t == null) { problems.Add($"COF link '{l.Name}' -> {l.AssetUUID}: target not fetched, NOT planned"); continue; }
                if (t.IsLink()) { problems.Add($"COF link '{l.Name}' points to another link {t.UUID}: NOT planned (never link to a link)"); continue; }
                if (t.Name.StartsWith("#Firestorm LSL Bridge", StringComparison.OrdinalIgnoreCase)) { problems.Add($"excluded '{t.Name}' {t.UUID} (Firestorm LSL Bridge)"); continue; }
                plan.Add(new PlannedLink(t.UUID, t.Name, t.InventoryType, t.AssetType, l.Description ?? "", "worn (COF)"));
            }
        }
        foreach (var s in extraIds)
        {
            if (!UUID.TryParse(s, out var id)) { problems.Add($"bad extra id '{s}'"); continue; }
            if (plan.Any(p => p.Target == id)) continue;
            var t = await FetchItemRO(id, ct);
            if (t == null) { problems.Add($"extra item {id}: not fetched, NOT planned"); continue; }
            if (t.IsLink()) { problems.Add($"extra item {id} '{t.Name}' is a link: NOT planned"); continue; }
            plan.Add(new PlannedLink(t.UUID, t.Name, t.InventoryType, t.AssetType, "", "extra (not worn)"));
        }
        return (plan, problems, myOutfits, moKids);
    }

    static async Task<string> OutfitCmd(string[] a)
    {
        if (!LoggedIn) return "not logged in";
        if (a.Length < 2 || (a[0] != "plan" && a[0] != "create")) return "usage: outfit plan|create <name> [extra item ids, comma separated]";
        var name = a[1]; var extra = a.Length > 2 ? a[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>();
        using var cts = new CancellationTokenSource(180000); var ct = cts.Token;
        var (plan, problems, myOutfits, moKids) = await PlanOutfit(extra, ct);
        var sb = new StringBuilder();
        sb.AppendLine($"My Outfits: {(myOutfits == null ? "NOT FOUND" : $"'{myOutfits.Name}' {myOutfits.UUID} type {myOutfits.PreferredType}")}; now holds {moKids?.Count ?? 0}: {string.Join(", ", (moKids ?? new()).Select(k => $"'{k.Name}'"))}");
        sb.AppendLine($"planned links for outfit '{name}' ({plan.Count}):");
        foreach (var p in plan) sb.AppendLine($"  '{p.Name}' -> original item {p.Target} {p.Asset}/{p.Inv} desc '{p.Desc}' [{p.Why}]");
        foreach (var pr in problems) sb.AppendLine("  note: " + pr);
        if (a[0] == "plan") { Log("outfit", $"outfit plan '{name}': {plan.Count} links"); return sb.ToString().TrimEnd(); }

        if (myOutfits == null) return sb + "ABORT: no verified My Outfits folder";
        if (moKids.Any(k => k.Name == name)) return sb + $"ABORT: My Outfits already has an entry named '{name}' (nothing created)";
        if (plan.Count == 0) return sb + "ABORT: empty plan";
        var rootKids = await ReadFolderRO(client.Inventory.Store.RootFolder.UUID, ct);
        var fid = client.Inventory.CreateFolder(myOutfits.UUID, name, FolderType.Outfit);
        if (rootKids.Any(k => k.UUID == fid) || moKids.Any(k => k.UUID == fid)) return sb + $"ABORT: CreateFolder returned an EXISTING folder {fid}; nothing linked";
        Log("outfit", $"created outfit folder '{name}' {fid} under My Outfits {myOutfits.UUID}");
        sb.AppendLine($"created folder '{name}' {fid} (type Outfit) under My Outfits");
        await Task.Delay(3000, ct);
        foreach (var p in plan)
        {
            InventoryItem made = null; string err = "";
            try { using var lt = CancellationTokenSource.CreateLinkedTokenSource(ct); lt.CancelAfter(20000); made = await client.Inventory.CreateLinkAsync(fid, p.Target, p.Name, p.Desc, p.Inv, UUID.Random(), lt.Token); }
            catch (Exception ex) { err = ex.GetBaseException().Message; }
            sb.AppendLine(made != null ? $"  linked '{p.Name}' -> {p.Target} (link {made.UUID})" : $"  LINK FAILED '{p.Name}' -> {p.Target} {err}");
            Log("outfit", made != null ? $"link '{p.Name}' -> {p.Target} ok ({made.UUID})" : $"link '{p.Name}' -> {p.Target} FAILED {err}");
        }
        await Task.Delay(3000, ct);
        var back = (await ReadFolderRO(fid, ct)).Where(k => k.ParentUUID == fid).OfType<InventoryItem>().OrderBy(k => k.Name).ToList();
        sb.AppendLine($"read back '{name}' {fid}: {back.Count} entries");
        foreach (var k in back) sb.AppendLine($"  '{k.Name}' {k.AssetType}/{k.InventoryType} -> {k.AssetUUID} desc '{k.Description}'");
        var missing = plan.Where(p => !back.Any(k => k.AssetUUID == p.Target)).ToList();
        sb.AppendLine(missing.Count == 0 ? "all planned links present" : $"MISSING after read-back ({missing.Count}): {string.Join(", ", missing.Select(m => m.Name))}");
        return sb.ToString().TrimEnd();
    }
}
