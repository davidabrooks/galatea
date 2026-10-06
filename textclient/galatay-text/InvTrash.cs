// InvTrash.cs (2026-10-05, David): 'inv trash <exact name|uuid>[, ...]' moves inventory ITEMS to Trash (never purges),
// with the classic MoveInventoryItem UDP message (the AIS route the library uses for folder moves answered 400 on SL,
// see MoveFolderUdp). Exact names only (case-insensitive); several items with the same name are listed with their uuids
// and refused until they are named by uuid. Skipped for name matches: Trash, Current Outfit, My Outfits (links), worn items.
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
        if (terms.Count == 0) return "usage: inv trash <exact item name|item uuid>[, <name|uuid>...]   (moves to Trash, never purges; same-name items must be named by uuid)";
        var trash = client.Inventory.FindFolderForType(FolderType.Trash);
        if (trash == UUID.Zero) return "Trash folder not found";
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
        return sb.ToString().TrimEnd();
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
        return $"inv trash selftest: {pass} PASS, {fail} FAIL\n" + sb.ToString().TrimEnd();
    }
}
