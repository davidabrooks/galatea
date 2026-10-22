using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibreMetaverse;

// 2026-10-21: one-off path to reset the Maitreya body's alpha segment state by detaching and
// IMMEDIATELY re-attaching the body at the same point. The body is otherwise protected; this is the
// only code that takes it off, it only matches the body itself (not its HUD or add-ons), and it
// re-attaches within seconds. COF links are left untouched.
namespace GalatayText;

public static partial class Program
{
    internal static bool IsMaitreyaBodyName(string n) =>
        n != null && n.StartsWith("Maitreya Mesh Body - Lara", StringComparison.OrdinalIgnoreCase)
        && !n.Contains("HUD", StringComparison.OrdinalIgnoreCase) && !n.Contains("Add-on", StringComparison.OrdinalIgnoreCase);

    static async Task<string> BodyReattachCmd()
    {
        var sim = client.Network.CurrentSim; if (sim == null) return "not connected";
        var me = client.Self.LocalID;
        var body = sim.ObjectsPrimitives.Values.FirstOrDefault(p => p.ParentID == me && IsMaitreyaBodyName(p.Properties?.Name));
        if (body == null) return "Maitreya body not found among worn attachments";
        var itemId = AttachItemId(body); if (itemId == UUID.Zero) return "body item id unknown";
        var pt = body.PrimData.AttachmentPoint;
        using var cts = new CancellationTokenSource(30000);
        var it = await FetchItemRO(itemId, cts.Token); if (it == null) return $"body item {itemId} not returned by inventory fetch";
        Log("body", $"BODY REATTACH: detaching '{it.Name}' {itemId} @{pt} to reset alpha segments; re-attaching immediately");
        client.Appearance.Detach(itemId);
        await Task.Delay(1500);
        string got = "";
        for (int i = 0; i < 3; i++)
        {
            var r = await AttachVerified(new List<(InventoryItem, AttachmentPoint)> { (it, pt) }, cts.Token);
            got = r[it.UUID];
            if (got.StartsWith("attached", StringComparison.OrdinalIgnoreCase)) break;
            await Task.Delay(1000);
        }
        Log("body", $"BODY REATTACH: '{it.Name}' re-attach -> {got}");
        return $"body '{it.Name}' detached and re-attached at {pt}: {got}";
    }
}
