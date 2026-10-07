// InvTrash.cs (2026-10-05, David): 'inv trash <exact name|uuid>[, ...]' moves inventory ITEMS to Trash (never purges),
// with the classic MoveInventoryItem UDP message (the AIS route the library uses for folder moves answered 400 on SL,
// see MoveFolderUdp). Exact names only (case-insensitive); several items with the same name are listed with their uuids
// and refused until they are named by uuid. Skipped for name matches: Trash, Current Outfit, My Outfits (links), worn items.
// 2026-10-07 (David, Valentine dress): a FOLDER can be trashed by uuid only (never by name): plain or Outfit folders, not
// system folders, not inside Trash/Current Outfit, not a kept outfit (Bikini + daily list), and nothing worn inside it.
using System.Text;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal sealed record InvEntry(string Path, UUID Id, string Name, bool IsFolder, UUID Parent, string Kind);
    internal sealed record InvTrashDecision(string Term, string Outcome, List<InvEntry> Items); // Outcome: move | notfound | ambiguous | refused

    internal static List<string> ParseInvTrashTerms(string rest) =>
        (rest ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(n => n.Trim('"', '\'').Trim()).Where(n => n.Length > 0).ToList();

    static bool InvPathSkipped(string path) =>
        path.StartsWith("/Trash", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/Current Outfit", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/My Outfits", StringComparison.OrdinalIgnoreCase);

    // pure (selftest)
    internal static List<InvTrashDecision> PlanInvTrash(IEnumerable<InvEntry> index, IEnumerable<string> terms, ISet<UUID> protectedIds)
    {
        var all = index.ToList(); var res = new List<InvTrashDecision>();
        foreach (var t in terms.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (UUID.TryParse(t, out var id))
            {
                var e = all.FirstOrDefault(x => x.Id == id);
                if (e == null) res.Add(new(t, "notfound", new()));
                else if (e.IsFolder) res.Add(new(t, "refused", new() { e })); // items only (outfit folders: 'outfit trash')
                else if (protectedIds.Contains(e.Id)) res.Add(new(t, "refused", new() { e }));
                else if (e.Path.StartsWith("/Trash", StringComparison.OrdinalIgnoreCase)) res.Add(new(t, "refused", new() { e }));
                else res.Add(new(t, "move", new() { e }));
                continue;
            }
            var hits = all.Where(x => !x.IsFolder && x.Name.Equals(t, StringComparison.OrdinalIgnoreCase) && !InvPathSkipped(x.Path) && !protectedIds.Contains(x.Id)).ToList();
            res.Add(new(t, hits.Count == 0 ? "notfound" : hits.Count == 1 ? "move" : "ambiguous", hits));
        }
        return res;
    }

    internal sealed record FolderTrashInfo(UUID Id, string Name, FolderType Type, List<FolderType> AncestorTypes, List<(UUID id, string name)> Items);

    // pure (selftest): "move" or "refused: <why>" for one folder named by uuid. Items = every non-link item inside it (recursive).
    internal static string FolderTrashVerdict(FolderTrashInfo f, ISet<UUID> worn, ISet<string> keepOutfits)
    {
        if (f.Type != FolderType.None && f.Type != FolderType.Outfit) return $"refused: system folder ({f.Type})";
        if (f.AncestorTypes.Contains(FolderType.Trash)) return "refused: already in Trash";
        if (f.AncestorTypes.Contains(FolderType.CurrentOutfit)) return "refused: inside Current Outfit";
        if (f.AncestorTypes.Contains(FolderType.MyOutfits) && keepOutfits.Contains(f.Name.Trim())) return "refused: a kept outfit (Bikini / daily list)";
        var w = f.Items.Where(i => worn.Contains(i.id)).Select(i => $"'{i.name}'").ToList();
        return w.Count > 0 ? $"refused: worn item(s) inside: {string.Join(", ", w)}" : "move";
    }

    static async Task<string> TrashFolderByUuid(InventoryFolder f, UUID trash, ISet<UUID> worn, CancellationToken ct)
    {
        var anc = new List<FolderType>(); var parent = f.ParentUUID;
        for (int i = 0; i < 64 && parent != UUID.Zero && client.Inventory.Store.TryGetValue(parent, out var pb) && pb is InventoryFolder pf; i++)
        { anc.Add(pf.PreferredType); parent = pf.ParentUUID; }
        if (f.ParentUUID == UUID.Zero) return $"refused: '{f.Name}' {f.UUID} is the inventory root";
        var items = new List<(UUID, string)>(); var todo = new Queue<UUID>(); todo.Enqueue(f.UUID); int folders = 0;
        while (todo.Count > 0 && folders++ < 200)
        {
            var id = todo.Dequeue(); var r = await ReadFolderTimed(id, ct, 20000);
            if (!r.ok) return $"refused: could not read folder {id} inside '{f.Name}'; nothing moved";
            foreach (var k in r.kids.Where(k => k.ParentUUID == id))
                if (k is InventoryFolder sf) todo.Enqueue(sf.UUID); else if (k is InventoryItem ii && !ii.IsLink()) items.Add((ii.UUID, ii.Name ?? ""));
        }
        if (todo.Count > 0) return $"refused: '{f.Name}' has more than 200 folders inside; nothing moved";
        var keep = DailyOutfitAllow().Append("Bikini").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var v = FolderTrashVerdict(new(f.UUID, f.Name ?? "", f.PreferredType, anc, items), worn, keep);
        if (v != "move") return $"{v}: folder '{f.Name}' {f.UUID}";
        var from = f.ParentUUID;
        MoveFolderUdp(f, trash);
        Log("inv", $"moved folder '{f.Name}' {f.UUID} ({items.Count} items) from {from} -> Trash (UDP)");
        await Task.Delay(2000, ct);
        var inTrash = (await ReadFolderRO(trash, ct)).Any(k => k.UUID == f.UUID);
        var stillThere = (await ReadFolderRO(from, ct)).Any(k => k.UUID == f.UUID && k.ParentUUID == from);
        return $"{(inTrash && !stillThere ? "moved to Trash (verified)" : stillThere ? "MOVE NOT CONFIRMED (still in its parent)" : "moved (Trash listing not yet updated)")}: folder '{f.Name}' {f.UUID} ({items.Count} items); was in {from}";
    }

    static void MoveItemUdp(InventoryItem it, UUID folder)
    {
        var pk = new LibreMetaverse.Packets.MoveInventoryItemPacket
        {
            AgentData = { AgentID = client.Self.AgentID, SessionID = client.Self.SessionID, Stamp = false },
            InventoryData = new[] { new LibreMetaverse.Packets.MoveInventoryItemPacket.InventoryDataBlock { ItemID = it.UUID, FolderID = folder, NewName = Utils.StringToBytes(it.Name) } }
        };
        client.Network.SendPacket(pk);
        try { it.ParentUUID = folder; client.Inventory.Store.UpdateNodeFor(it); } catch { }
    }

    static async Task<string> InvTrashCmd(string rest)
    {
        if (!LoggedIn) return "not logged in";
        var terms = ParseInvTrashTerms(rest);
        if (terms.Count == 0) return "usage: inv trash <exact item name|item uuid|folder uuid>[, ...]   (moves to Trash, never purges; same-name items must be named by uuid; folders by uuid only)";
        var trash = client.Inventory.FindFolderForType(FolderType.Trash);
        if (trash == UUID.Zero) return "Trash folder not found";
        // folders named by uuid (from the login skeleton: no inventory scan needed)
        var fsb = new StringBuilder(); var folderTerms = new List<string>();
        foreach (var t in terms)
            if (UUID.TryParse(t, out var fid) && client.Inventory.Store.TryGetValue(fid, out var fb) && fb is InventoryFolder) folderTerms.Add(t);
        if (folderTerms.Count > 0)
        {
            var worn = OutfitProtectedIds();
            try { foreach (var p in WornPrims()) worn.Add(AttachItemId(p)); foreach (var w in client.Appearance.GetWearables()) worn.Add(w.ItemID); } catch { }
            using var fct = new CancellationTokenSource(75000);
            foreach (var t in folderTerms)
            {
                var f = (InventoryFolder)client.Inventory.Store[new UUID(t)];
                try { fsb.AppendLine(await TrashFolderByUuid(f, trash, worn, fct.Token)); }
                catch (Exception e) { fsb.AppendLine($"folder {t}: failed ({e.Message}); check 'inv ls {t}'"); }
            }
            terms = terms.Except(folderTerms).ToList();
            lock (invIndex) invIndexComplete = false;
            if (terms.Count == 0) return fsb.ToString().TrimEnd();
        }
        if (!await invGate.WaitAsync(TimeSpan.FromSeconds(5))) return "an inventory scan is running; try again in ~30 s";
        bool complete;
        try
        {
            // always a fresh index for a delete (moves since the last scan must not be acted on from a stale view);
            // a big inventory needs more than one 30 s budget, so continue the same scan up to 4 times
            invPending = null; complete = false;
            for (int pass = 0; pass < 4 && !complete; pass++)
            {
                using var cts = new CancellationTokenSource(InvFindBudget + TimeSpan.FromSeconds(5));
                (_, complete) = await BuildInvIndex(InvFindBudget, cts.Token);
            }
        }
        finally { invGate.Release(); }
        if (!complete) return "inventory index is PARTIAL (not every folder could be read in 2 min); nothing moved. Run 'inv trash' again to continue the scan.";
        List<(string path, InventoryBase node)> snap; lock (invIndex) snap = invIndex.ToList();
        var entries = snap.Select(x => new InvEntry(x.path, x.node.UUID, x.node.Name ?? "", x.node is InventoryFolder, x.node.ParentUUID,
                                                    x.node is InventoryItem ii ? $"{ii.AssetType}{(ii.IsLink() ? " link" : "")}" : "folder")).ToList();
        var prot = OutfitProtectedIds();
        try { foreach (var p in WornPrims()) prot.Add(AttachItemId(p)); foreach (var w in client.Appearance.GetWearables()) prot.Add(w.ItemID); } catch { }
        var plan = PlanInvTrash(entries, terms, prot);
        var sb = new StringBuilder(); var moved = new List<InvEntry>();
        string Row(InvEntry e) => $"'{e.Name}' {e.Kind} {e.Id} in {(e.Path.Length == 0 ? "/" : e.Path)}";
        using var ct2 = new CancellationTokenSource(60000);
        foreach (var d in plan)
        {
            switch (d.Outcome)
            {
                case "notfound": sb.AppendLine($"not found (exact name or uuid, outside Trash/Current Outfit/My Outfits): '{d.Term}'"); break;
                case "refused": sb.AppendLine($"refused: {Row(d.Items[0])} ({(d.Items[0].IsFolder ? "a folder" : d.Items[0].Path.StartsWith("/Trash", StringComparison.OrdinalIgnoreCase) ? "already in Trash" : "worn / protected")})"); break;
                case "ambiguous":
                    sb.AppendLine($"ambiguous: '{d.Term}' matches {d.Items.Count} items; nothing moved for it. Name the ones to trash by uuid:");
                    foreach (var e in d.Items) sb.AppendLine("  " + Row(e));
                    break;
                case "move":
                    var e1 = d.Items[0];
                    var it = await FetchItemRO(e1.Id, ct2.Token);
                    if (it == null) { sb.AppendLine($"could not fetch {Row(e1)}; not moved"); break; }
                    MoveItemUdp(it, trash); moved.Add(e1);
                    Log("inv", $"moved item '{e1.Name}' {e1.Id} from {e1.Path} -> Trash (UDP)");
                    break;
            }
        }
        if (moved.Count > 0)
        {
            await Task.Delay(2000, ct2.Token);
            var inTrash = (await ReadFolderRO(trash, ct2.Token)).Select(k => k.UUID).ToHashSet();
            foreach (var e in moved)
            {
                var stillThere = (await ReadFolderRO(e.Parent, ct2.Token)).Any(k => k.UUID == e.Id && k.ParentUUID == e.Parent);
                sb.AppendLine($"{(inTrash.Contains(e.Id) && !stillThere ? "moved to Trash (verified)" : stillThere ? "MOVE NOT CONFIRMED (still in its folder)" : "moved (Trash listing not yet updated)")}: {Row(e)}");
            }
            lock (invIndex) invIndexComplete = false; // next 'inv find' rescans
        }
        return (fsb.ToString() + sb.ToString()).TrimEnd();
    }

    internal static string InvTrashSelfTest()
    {
        var sb = new StringBuilder(); int pass = 0, fail = 0;
        void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        UUID U(int n) => new UUID($"00000000-0000-0000-0000-{n:D12}");
        var idx = new List<InvEntry>
        {
            new("/Landmarks", U(1), "BC Sky Platform", false, U(100), "Landmark"),
            new("/Landmarks/Old", U(2), "BC Sky Platform", false, U(101), "Landmark"),
            new("/Landmarks", U(3), "Peronaut Home", false, U(100), "Landmark"),
            new("/Trash", U(4), "Peronaut Home", false, U(102), "Landmark"),
            new("/My Outfits/Bikini", U(5), "DOUX - Oasis Hairstyle [M/Long]", false, U(103), "Object link"),
            new("/Hair", U(6), "DOUX - Oasis Hairstyle [M/Long]", false, U(104), "Object"),
            new("/Current Outfit", U(7), "Unique Thing", false, U(105), "Object link"),
            new("", U(8), "Landmarks", true, UUID.Zero, "folder"),
        };
        var prot = new HashSet<UUID> { U(6) };
        var p = PlanInvTrash(idx, ParseInvTrashTerms("BC Sky Platform, \"Peronaut Home\", Nope"), prot);
        C(p[0].Outcome == "ambiguous" && p[0].Items.Count == 2, "two same-name landmarks -> ambiguous, listed, nothing moved");
        C(p[1].Outcome == "move" && p[1].Items[0].Id == U(3), "exact name ignores the copy already in Trash");
        C(p[2].Outcome == "notfound", "no partial matching");
        var q = PlanInvTrash(idx, new[] { U(1).ToString(), U(2).ToString() }, prot);
        C(q.All(d => d.Outcome == "move"), "same-name items confirmed by uuid -> moved");
        C(PlanInvTrash(idx, new[] { "DOUX - Oasis Hairstyle [M/Long]" }, prot)[0].Outcome == "notfound", "worn hair and My Outfits links are never picked by name");
        C(PlanInvTrash(idx, new[] { U(6).ToString() }, prot)[0].Outcome == "refused", "worn item refused even by uuid");
        C(PlanInvTrash(idx, new[] { U(8).ToString() }, prot)[0].Outcome == "refused", "folders refused (items only)");
        C(PlanInvTrash(idx, new[] { U(4).ToString() }, prot)[0].Outcome == "refused", "already in Trash -> refused");
        C(PlanInvTrash(idx, new[] { "bc sky platform" }, prot)[0].Outcome == "ambiguous", "names are case-insensitive");
        C(PlanInvTrash(idx, new[] { "Unique Thing" }, prot)[0].Outcome == "notfound", "Current Outfit entries are not picked by name");
        // folders by uuid (2026-10-07: the '"Valentine' outfit + the dress folders)
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Bikini", "ARTi'S Strapless Top" };
        var worn = new HashSet<UUID> { U(20) };
        FolderTrashInfo F(string n, FolderType t, FolderType[] anc, params (UUID, string)[] its) => new(U(30), n, t, anc.ToList(), its.ToList());
        var root = new[] { FolderType.Root };
        C(FolderTrashVerdict(F("\"Valentine", FolderType.Outfit, new[] { FolderType.MyOutfits, FolderType.Root }), worn, keep) == "move", "outfit folder with a quoted name, links only -> move");
        C(FolderTrashVerdict(F("TETRA - Valentine Dress (Fatpack)", FolderType.None, new[] { FolderType.Inbox, FolderType.Root }, (U(21), "HUD")), worn, keep) == "move", "plain folder in Received Items, nothing worn -> move");
        C(FolderTrashVerdict(F("Bikini", FolderType.Outfit, new[] { FolderType.MyOutfits, FolderType.Root }), worn, keep).StartsWith("refused: a kept outfit"), "kept outfit refused");
        C(FolderTrashVerdict(F("Clothes", FolderType.None, root, (U(20), "jeans")), worn, keep).Contains("'jeans'"), "worn item inside -> refused, named");
        C(FolderTrashVerdict(F("Received Items", FolderType.Inbox, root), worn, keep).StartsWith("refused: system"), "system folder refused");
        C(FolderTrashVerdict(F("Old", FolderType.None, new[] { FolderType.Trash, FolderType.Root }), worn, keep) == "refused: already in Trash", "already in Trash refused");
        C(FolderTrashVerdict(F("x", FolderType.None, new[] { FolderType.CurrentOutfit, FolderType.Root }), worn, keep) == "refused: inside Current Outfit", "inside COF refused");
        return $"inv trash selftest: {pass} PASS, {fail} FAIL\n" + sb.ToString().TrimEnd();
    }
}
