// CofUnlink.cs (2026-10-21, David): 'cof unlink <name|link uuid>' removes ONE Current Outfit link (e.g. a stray folder link
// such as 'Jani tshirt' pointing into the Trash) and rebakes. Exactly one match is required. Also 'v pause on|off':
// a persistent switch that stops VAuto from re-attaching The V (run/v-auto-paused) while David checks something.
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    // pure (tested): links matching a name (case-insensitive, exact) or a link uuid
    internal static List<(UUID id, string name)> CofUnlinkMatches(IEnumerable<(UUID id, string name)> links, string spec)
    {
        spec = (spec ?? "").Trim();
        if (spec.Length == 0) return new();
        if (UUID.TryParse(spec, out var u)) return links.Where(l => l.id == u).ToList();
        return links.Where(l => string.Equals(l.name?.Trim(), spec, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    static async Task<string> CofUnlinkCmd(string spec)
    {
        using var cts = new CancellationTokenSource(60000); var ct = cts.Token;
        var cof = await CofFolder(ct); if (cof == null) return "cof unlink: Current Outfit folder not found";
        var links = (await ReadFolderRO(cof.UUID, ct, 30000)).OfType<InventoryItem>().Where(i => i.ParentUUID == cof.UUID && i.IsLink()).ToList();
        var m = CofUnlinkMatches(links.Select(l => (l.UUID, l.Name)), spec);
        if (m.Count == 0) return $"cof unlink: no Current Outfit link named '{spec}'";
        if (m.Count > 1) return $"cof unlink: {m.Count} links match '{spec}' ({string.Join(", ", m.Select(x => x.id))}); give the link uuid";
        var protectedIds = OutfitProtectedIds(); var l0 = links.First(l => l.UUID == m[0].id);
        if (protectedIds.Contains(l0.AssetUUID)) return $"cof unlink: '{l0.Name}' is protected; refused";
        try { await client.Inventory.RemoveItemsAsync(new[] { l0.UUID }, ct); }
        catch (Exception ex) { return "cof unlink failed: " + ex.GetBaseException().Message; }
        string bake; try { await client.Appearance.RequestSetAppearance(true); bake = "rebake requested"; } catch (Exception ex) { bake = "rebake failed: " + ex.GetBaseException().Message; }
        var msg = $"cof unlink: removed link '{l0.Name}' {l0.UUID} ({l0.AssetType} -> {l0.AssetUUID}); {bake}";
        Log("outfit", msg); return msg;
    }

    static string VPauseFile => Path.Combine(RunDirForVPause, "v-auto-paused");
    static string RunDirForVPause => "/home/box/viewers/textclient/run";
    internal static bool VAutoPaused() { try { return File.Exists(VPauseFile); } catch { return false; } }
    static string VPauseCmd(string arg)
    {
        if (arg == "on") { File.WriteAllText(VPauseFile, DateTime.Now.ToString("s")); return "The V auto-wear paused (stays off until 'v pause off')"; }
        if (arg == "off") { try { File.Delete(VPauseFile); } catch { } return "The V auto-wear resumed"; }
        return $"The V auto-wear is {(VAutoPaused() ? "PAUSED" : "on")}";
    }
}
