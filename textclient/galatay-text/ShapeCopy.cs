// ShapeCopy.cs (2026-10-11, David: an 'Android' copy of her shape with a smaller bust; never edit the saved Petite shape)
// inv copy <item uuid> <new name>   copy a wearable (shape, skin, clothing layer) next to the original under a new name;
//                                    a copied shape is added to routes/_editable-shapes.txt so 'shape set' may edit it
// shape wear <shape item uuid>       wear a shape (body part replace), e.g. the copy
// shape set [@"<shape name>"] <slider> <0-100>   edits the WORN shape only if it is the Petite shape or listed in _editable-shapes.txt;
//                                    the optional @name must match the worn shape (guard against editing the wrong one)
using System.Text;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static string EditableShapesFile => Path.Combine(RouteDir, "_editable-shapes.txt");

    internal static HashSet<string> ParseEditableShapes(string text) =>
        (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToHashSet(StringComparer.OrdinalIgnoreCase);

    static HashSet<string> EditableShapes() { try { return File.Exists(EditableShapesFile) ? ParseEditableShapes(File.ReadAllText(EditableShapesFile)) : new(StringComparer.OrdinalIgnoreCase); } catch { return new(StringComparer.OrdinalIgnoreCase); } }

    // null = may edit; otherwise the refusal text
    internal static string? ShapeEditRefusal(string worn, string? target, ISet<string> extra, string original)
    {
        worn = (worn ?? "").Trim();
        if (target != null && !string.Equals(worn, target.Trim(), StringComparison.OrdinalIgnoreCase))
            return $"refused: the worn shape is '{worn}', not '{target}' (wear it first: shape wear <item uuid>)";
        if (string.Equals(worn, original, StringComparison.OrdinalIgnoreCase) || extra.Contains(worn)) return null;
        return $"refused: the worn shape '{worn}' is not editable (only '{original}' or a shape listed in routes/_editable-shapes.txt, e.g. one made with 'inv copy')";
    }

    // '@"Petite - Android" Breast Size' -> ("Petite - Android", "Breast Size"); '@Name Breast Size' -> ("Name", "Breast Size")
    internal static (string? target, string key) SplitShapeTarget(string s)
    {
        s = (s ?? "").Trim();
        if (!s.StartsWith("@")) return (null, s.Trim('"'));
        s = s[1..];
        if (s.StartsWith("\""))
        { int e = s.IndexOf('"', 1); if (e < 0) return (s.Trim('"'), ""); return (s[1..e], s[(e + 1)..].Trim().Trim('"')); }
        int sp = s.IndexOf(' '); return sp < 0 ? (s, "") : (s[..sp], s[(sp + 1)..].Trim().Trim('"'));
    }

    // "<uuid> <name...>" -> (id, name); name keeps inner spaces, outer quotes dropped
    internal static (UUID id, string name, string? err) ParseInvCopyArgs(string rest)
    {
        var s = (rest ?? "").Trim(); int sp = s.IndexOf(' ');
        if (sp < 0) return (UUID.Zero, "", "usage: inv copy <item uuid> <new name>");
        if (!UUID.TryParse(s[..sp], out var id)) return (UUID.Zero, "", $"bad item uuid '{s[..sp]}'");
        var name = s[(sp + 1)..].Trim().Trim('"').Trim();
        if (name.Length == 0 || name.Length > 63) return (UUID.Zero, "", "new name must be 1..63 characters");
        return (id, name, null);
    }

    static async Task<string> InvCopyCmd(string rest)
    {
        var (id, name, err) = ParseInvCopyArgs(rest); if (err != null) return err;
        using var cts = new CancellationTokenSource(60000); var ct = cts.Token;
        var it = await FetchItemRO(id, ct);
        if (it == null) return $"item {id} not found";
        if (it.IsLink()) return $"'{it.Name}' is a link; copy the original item";
        if (it is not InventoryWearable w) return $"'{it.Name}' is not a wearable (inv copy is for shapes, skins and clothing layers)";
        if ((it.Permissions.OwnerMask & PermissionMask.Copy) == 0) return $"refused: '{it.Name}' is no-copy";
        var sibs = await ReadFolderRO(it.ParentUUID, ct);
        if (sibs.Any(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase))) return $"refused: '{name}' already exists in that folder";
        InventoryBase made;
        try { using var t = CancellationTokenSource.CreateLinkedTokenSource(ct); t.CancelAfter(30000); made = await client.Inventory.CopyItemAsync(it.UUID, it.ParentUUID, name, t.Token); }
        catch (Exception ex) { return "copy failed: " + ex.GetBaseException().Message; }
        if (made == null) return "copy failed: the server returned no item";
        string note = "";
        if (w.WearableType == WearableType.Shape)
        {
            try { File.AppendAllText(EditableShapesFile, name + "\n"); note = "; added to routes/_editable-shapes.txt (shape set may edit it)"; } catch (Exception ex) { note = "; could not update _editable-shapes.txt: " + ex.Message; }
        }
        Log("inv", $"copied '{it.Name}' {it.UUID} -> '{made.Name}' {made.UUID} in {it.ParentUUID}{note}");
        return $"copied '{it.Name}' -> '{made.Name}' item {made.UUID} (same folder){note}";
    }

    static Task<string> ShapeWearCmd(string arg) => BodyPartWearCmd(arg, WearableType.Shape);

    // wear a body part (shape/skin/eyes/hair base), replacing the one of the same type, and drop the old type's COF
    // link(s) so the Current Outfit (and an outfit saved from it) holds exactly one (2026-10-11: 'shape wear' left the
    // Petite shape's COF link next to the copy's)
    internal static async Task<string> BodyPartWearCmd(string arg, WearableType? want)
    {
        if (!UUID.TryParse((arg ?? "").Trim(), out var id)) return "usage: wear bodypart <item uuid> | shape wear <shape item uuid>";
        using var cts = new CancellationTokenSource(60000); var ct = cts.Token;
        var it = await FetchItemRO(id, ct);
        if (it is not InventoryWearable w || w.AssetType != AssetType.Bodypart) return $"item {id} is not a body part";
        if (want != null && w.WearableType != want) return $"'{it.Name}' is a {w.WearableType}, not a {want}";
        // COF first (2026-10-11: AddToOutfit alone sometimes made no COF link for a body part, leaving NO shape link):
        // link the new part, drop the old same-type links, then rebuild the appearance from the COF
        string note = "";
        var cof = await CofFolder(ct);
        if (cof == null) return "COF not found; nothing changed";
        var links = (await ReadFolderRO(cof.UUID, ct)).OfType<InventoryItem>().Where(l => l.ParentUUID == cof.UUID && l.IsLink()).ToList();
        if (!links.Any(l => l.AssetUUID == it.UUID))
        {
            InventoryItem made = null;
            try { using var lt = CancellationTokenSource.CreateLinkedTokenSource(ct); lt.CancelAfter(20000); made = await client.Inventory.CreateLinkAsync(cof.UUID, it.UUID, it.Name, "", it.InventoryType, UUID.Random(), lt.Token); }
            catch (Exception ex) { return $"COF link for '{it.Name}' failed: {ex.GetBaseException().Message}; nothing removed"; }
            if (made == null) return $"COF link for '{it.Name}' was not created; nothing removed";
            note += "; COF link added";
        }
        var stale = new List<InventoryItem>();
        foreach (var l in links.Where(l => l.AssetUUID != it.UUID))
            if (await FetchItemRO(l.AssetUUID, ct) is InventoryWearable tw && tw.AssetType == AssetType.Bodypart && tw.WearableType == w.WearableType) stale.Add(l);
        if (stale.Count > 0) { await client.Inventory.RemoveItemsAsync(stale.Select(l => l.UUID), ct); note += $"; removed {stale.Count} old {w.WearableType} COF link(s): {string.Join(", ", stale.Select(l => l.Name))}"; }
        client.Appearance.AddToOutfit(new List<InventoryItem> { it }, true);
        await Task.Delay(3000, ct);
        try { await client.Appearance.RequestSetAppearance(true); } catch (Exception ex) { note += "; rebake error " + ex.GetBaseException().Message; }
        await Task.Delay(3000, ct);
        var now = client.Appearance.GetWearables().FirstOrDefault(x => x.WearableType == w.WearableType);
        Log("wear", $"wear bodypart '{it.Name}' {it.UUID}; worn {w.WearableType} now {now?.ItemID}{note}");
        return (now?.ItemID == it.UUID ? $"wearing {w.WearableType} '{it.Name}'" : $"asked to wear '{it.Name}', worn {w.WearableType} is still {now?.ItemID}") + note;
    }
}
