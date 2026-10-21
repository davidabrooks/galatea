// OutfitCollarLinks.cs (2026-10-21, David): the N° 9 Premium collar must be a LINK inside EVERY saved outfit folder
// (My Outfits/*), and 'outfit save' adds it to new outfits. 'outfits collar [check]' adds missing links / reports.
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    // pure (tested): does this outfit folder's contents already link the collar?
    internal static bool OutfitHasCollarLink(IEnumerable<(bool isLink, UUID target)> items) => items.Any(i => i.isLink && i.target == N9CollarItem);

    static async Task<string> OutfitsCollarCmd(bool checkOnly)
    {
        using var cts = new CancellationTokenSource(180000); var ct = cts.Token;
        var mo = await FindMyOutfits(ct); if (mo == null) return "My Outfits not found";
        var collar = await FetchItemRO(N9CollarItem, ct); if (collar == null) return "collar item not found";
        var sb = new System.Text.StringBuilder();
        foreach (var f in (await ReadFolderRO(mo.UUID, ct)).OfType<InventoryFolder>().Where(f => f.ParentUUID == mo.UUID).OrderBy(f => f.Name))
        {
            var kids = (await ReadFolderRO(f.UUID, ct)).OfType<InventoryItem>().Where(i => i.ParentUUID == f.UUID).ToList();
            bool has = OutfitHasCollarLink(kids.Select(i => (i.IsLink(), i.AssetUUID)));
            string st = has ? "yes" : "NO";
            if (!has && !checkOnly)
            {
                InventoryItem made = null; string err = "";
                try { made = await client.Inventory.CreateLinkAsync(f.UUID, collar.UUID, collar.Name, "", collar.InventoryType, UUID.Random(), ct); } catch (Exception ex) { err = ex.GetBaseException().Message; }
                st = made != null ? "added" : "FAILED " + err;
            }
            sb.AppendLine($"{f.Name}: {st}");
        }
        var msg = sb.ToString().TrimEnd(); Log("outfit", "outfits collar: " + msg.Replace('\n', ';')); return msg;
    }

    // pure (tested): 'outfit create' (outfit_save) must add the collar link when the plan lacks it
    internal static bool PlanNeedsCollar(IEnumerable<UUID> targets) => !targets.Contains(N9CollarItem);
}
