// InvMove.cs (2026-10-07, David: "organize your belongings with folders"): reversible inventory organizing.
//   inv mkdir <parent folder uuid> <name>        create a plain folder (an existing child of that name is reused)
//   inv move <item|folder uuid> <dest folder uuid>   move with the classic UDP Move*Inventory* messages (AIS folder moves
//                                                answered 400 on SL, see MoveFolderUdp); logged to inv-moves.log
//   inv move undo                                move the last not-yet-undone entry of the log back to its old parent
// Refused: system folders and the root as sources; anything in Trash / Current Outfit / My Outfits as source or as
// destination (Trash is 'inv trash'); a folder into itself or its own subfolder. Moving keeps item UUIDs, so outfit and
// COF links (which point at the item UUID) keep working.
using System.Text;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal static string InvMoveLog = "/workspace/secondlife/inv-moves.log";

    /// <summary>One inventory node as the verdicts need it: kind, preferred type and its ancestors' types + ids (nearest first).</summary>
    internal sealed record InvNodeInfo(UUID Id, string Name, bool IsFolder, FolderType Type, UUID Parent, List<(UUID id, FolderType type)> Ancestors);

    static bool InProtectedTree(InvNodeInfo n, bool includeSelf) =>
        (includeSelf && n.IsFolder && n.Type is FolderType.Trash or FolderType.CurrentOutfit or FolderType.MyOutfits)
        || n.Ancestors.Any(a => a.type is FolderType.Trash or FolderType.CurrentOutfit or FolderType.MyOutfits);

    /// <summary>Pure: "move", "already there" or "refused: why".</summary>
    internal static string InvMoveVerdict(InvNodeInfo src, InvNodeInfo dest)
    {
        if (src == null) return "refused: source not found";
        if (dest == null || !dest.IsFolder) return "refused: destination is not a folder";
        if (src.Parent == UUID.Zero && src.IsFolder) return "refused: the inventory root";
        if (src.IsFolder && src.Type != FolderType.None) return $"refused: system folder ({src.Type})";
        if (InProtectedTree(src, false)) return "refused: source is inside Trash / Current Outfit / My Outfits";
        if (dest.Type == FolderType.Trash || dest.Ancestors.Any(a => a.type == FolderType.Trash)) return "refused: destination is Trash (use 'inv trash')";
        if (InProtectedTree(dest, true)) return "refused: destination is Current Outfit / My Outfits";
        if (src.IsFolder && (dest.Id == src.Id || dest.Ancestors.Any(a => a.id == src.Id))) return "refused: a folder cannot go into itself";
        if (src.Parent == dest.Id) return "already there";
        return "move";
    }

    /// <summary>Pure: "create", "exists &lt;uuid&gt;" or "refused: why".</summary>
    internal static string InvMkdirVerdict(InvNodeInfo parent, string name, IEnumerable<(UUID id, string name, bool isFolder)> siblings)
    {
        if (string.IsNullOrWhiteSpace(name)) return "refused: empty name";
        if (parent == null || !parent.IsFolder) return "refused: parent is not a folder";
        if (InProtectedTree(parent, true)) return "refused: parent is Trash / Current Outfit / My Outfits";
        var ex = siblings.FirstOrDefault(s => s.isFolder && string.Equals(s.name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        return ex.id != UUID.Zero ? $"exists {ex.id}" : "create";
    }

    /// <summary>Pure: log lines "time\tMOVE\tid\tkind\tfrom\tto\tname" / "time\tUNDO\tid\t..." -> the last MOVE not undone yet.</summary>
    internal static (UUID id, bool isFolder, UUID from, UUID to, string name)? LastUndoableMove(IEnumerable<string> lines)
    {
        var stack = new List<(UUID, bool, UUID, UUID, string)>();
        foreach (var l in lines)
        {
            var f = l.Split('\t');
            if (f.Length >= 6 && f[1] == "MOVE" && UUID.TryParse(f[2], out var id) && UUID.TryParse(f[4], out var from) && UUID.TryParse(f[5], out var to))
                stack.Add((id, f[3] == "folder", from, to, f.Length > 6 ? f[6] : ""));
            else if (f.Length >= 3 && f[1] == "UNDO" && UUID.TryParse(f[2], out var uid))
            {
                var i = stack.FindLastIndex(s => s.Item1 == uid); if (i >= 0) stack.RemoveAt(i);
            }
        }
        return stack.Count == 0 ? null : stack[^1];
    }

    static InvNodeInfo NodeInfo(UUID id)
    {
        if (!client.Inventory.Store.TryGetValue(id, out var b) || b == null) return null;
        var anc = new List<(UUID, FolderType)>(); var parent = b.ParentUUID;
        for (int i = 0; i < 64 && parent != UUID.Zero && client.Inventory.Store.TryGetValue(parent, out var pb) && pb is InventoryFolder pf; i++)
        { anc.Add((pf.UUID, pf.PreferredType)); parent = pf.ParentUUID; }
        return new InvNodeInfo(b.UUID, b.Name ?? "", b is InventoryFolder, b is InventoryFolder f ? f.PreferredType : FolderType.None, b.ParentUUID, anc);
    }

    static async Task<InvNodeInfo> NodeInfoFetched(UUID id, CancellationToken ct)
    {
        var n = NodeInfo(id); if (n != null) return n;
        try { var it = await FetchItemRO(id, ct); if (it != null) return NodeInfo(id); } catch { }
        return null;
    }

    static async Task<string> InvMoveCmd(string[] a)
    {
        if (!LoggedIn) return "not logged in";
        using var cts = new CancellationTokenSource(60000); var ct = cts.Token;
        if (a.Length >= 1 && a[0].Equals("undo", StringComparison.OrdinalIgnoreCase))
        {
            var lines = File.Exists(InvMoveLog) ? File.ReadAllLines(InvMoveLog) : Array.Empty<string>();
            var last = LastUndoableMove(lines);
            if (last == null) return "nothing to undo (inv-moves.log)";
            var (uid, isF, from, to, nm) = last.Value;
            var r = await DoInvMove(uid, from, ct, "UNDO");
            return $"undo '{nm}' ({to} -> {from}): {r}";
        }
        if (a.Length < 2 || !UUID.TryParse(a[0], out var id) || !UUID.TryParse(a[1], out var dest)) return "usage: inv move <item|folder uuid> <dest folder uuid> | inv move undo";
        return await DoInvMove(id, dest, ct, "MOVE");
    }

    static async Task<string> DoInvMove(UUID id, UUID dest, CancellationToken ct, string tag)
    {
        var src = await NodeInfoFetched(id, ct); var dn = NodeInfo(dest);
        var v = InvMoveVerdict(src, dn);
        if (v != "move") return $"{v}: {(src == null ? id.ToString() : $"'{src.Name}' {id}")} -> {(dn == null ? dest.ToString() : $"'{dn.Name}' {dest}")}";
        var from = src.Parent;
        if (src.IsFolder) MoveFolderUdp((InventoryFolder)client.Inventory.Store[id], dest);
        else MoveItemUdp((InventoryItem)client.Inventory.Store[id], dest);
        try { File.AppendAllText(InvMoveLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{tag}\t{id}\t{(src.IsFolder ? "folder" : "item")}\t{from}\t{dest}\t{src.Name}\n"); } catch { }
        Log("inv", $"{tag} {(src.IsFolder ? "folder" : "item")} '{src.Name}' {id}: {from} -> {dest} (UDP)");
        await Task.Delay(1500, ct);
        bool inDest = (await ReadFolderRO(dest, ct)).Any(k => k.UUID == id);
        bool stillThere = (await ReadFolderRO(from, ct)).Any(k => k.UUID == id && k.ParentUUID == from);
        lock (invIndex) invIndexComplete = false;
        return $"{(inDest && !stillThere ? "moved (verified)" : stillThere ? "MOVE NOT CONFIRMED (still in its old folder)" : "moved (destination listing not yet updated)")}: '{src.Name}' {(src.IsFolder ? "folder" : "item")} {id} from {from} -> '{dn.Name}' {dest}";
    }

    static async Task<string> InvMkdirCmd(string rest)
    {
        if (!LoggedIn) return "not logged in";
        var t = rest.Trim(); var sp = t.IndexOf(' ');
        if (sp <= 0 || !UUID.TryParse(t[..sp], out var pid)) return "usage: inv mkdir <parent folder uuid> <name>";
        var name = t[(sp + 1)..].Trim().Trim('"');
        using var cts = new CancellationTokenSource(30000);
        var kids = (await ReadFolderRO(pid, cts.Token)).Where(k => k.ParentUUID == pid).Select(k => (k.UUID, k.Name ?? "", k is InventoryFolder)).ToList();
        var v = InvMkdirVerdict(NodeInfo(pid), name, kids);
        if (v.StartsWith("exists")) return $"folder '{name}' already exists: {v[7..]} (reused)";
        if (v != "create") return $"{v}: '{name}' in {pid}";
        var fid = client.Inventory.CreateFolder(pid, name, FolderType.None);
        Log("inv", $"mkdir '{name}' {fid} in {pid}");
        await Task.Delay(1500, cts.Token);
        bool ok = (await ReadFolderRO(pid, cts.Token)).Any(k => k.UUID == fid);
        return $"{(ok ? "created (verified)" : "created (listing not yet updated)")}: folder '{name}' {fid} in {pid}";
    }
}
