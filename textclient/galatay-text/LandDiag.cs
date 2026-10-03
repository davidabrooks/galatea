// LandDiag.cs (2026-10-02): why do landmark teleports to the BC sky platform land at the parcel landing point?
//   parcel [x y]            parcel at a point of the current region (default: own position): owner, group, teleport routing,
//                           landing point, and whether Galatay has the parcel group's "Ignore landing point" ability
//   landmark raw <name>     a landmark's item id, asset id and the raw asset text the server stored
// Finding (compared with Firestorm llagent.cpp doTeleportViaLandmark / lllandmarkactions.cpp createLandmarkHere):
//   both clients send the same CreateInventoryItem (null transaction, AT_LANDMARK, server writes the asset) and the same
//   TeleportLandmarkRequest(AgentID, SessionID, LandmarkID = asset id). The redirect is the server's parcel rule:
//   Teleport Routing = Landing Point is bypassed only by the owner, estate managers, or a parcel-group role with
//   ability bit 26 "Ignore landing point" (LibreMetaverse calls it GroupPowers.AllowLandmark).
using System.Text;
using LibreMetaverse;
using LibreMetaverse.Assets;

namespace GalatayText;

public static partial class Program
{
    const ulong IgnoreLandingPointPower = 1UL << 26; // role_actions.xml "land allow direct teleport" = "Ignore landing point"

    static async Task<Parcel> ParcelAt(Simulator sim, float x, float y, int timeoutMs = 10000)
    {
        int seq = 770000 + Random.Shared.Next(9999);
        var tcs = new TaskCompletionSource<Parcel>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(object s, ParcelPropertiesEventArgs e) { if (e.SequenceID == seq && e.Parcel != null) tcs.TrySetResult(e.Parcel); }
        client.Parcels.ParcelProperties += H;
        try
        {
            client.Parcels.RequestParcelProperties(sim, y + 0.5f, x + 0.5f, y - 0.5f, x - 0.5f, seq, false);
            if (await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) == tcs.Task) return tcs.Task.Result;
            return null;
        }
        finally { client.Parcels.ParcelProperties -= H; }
    }

    // One line: routing + who may bypass it; null if unknown
    static async Task<string> LandingDiag(Simulator sim, Vector3 at)
    {
        var p = await ParcelAt(sim, at.X, at.Y);
        if (p == null) return null;
        var sb = new StringBuilder($"parcel '{p.Name}' local id {p.LocalID} owner {p.OwnerID}{(p.IsGroupOwned ? " (group-owned)" : "")} group {p.GroupID}; " +
                                   $"teleport routing {p.Landing}; landing point {P3(p.UserLocation)}");
        if (p.Landing == LandingType.LandingPoint)
        {
            bool owner = p.OwnerID == client.Self.AgentID;
            var (groups, fresh) = await FetchCurrentGroups(8000);
            string grp;
            if (p.GroupID == UUID.Zero) grp = "parcel has no group";
            else if (!groups.TryGetValue(p.GroupID, out var g)) grp = $"Galatay is not in the parcel group {p.GroupID}";
            else
            {
                bool can = ((ulong)g.Powers & IgnoreLandingPointPower) != 0;
                grp = $"Galatay's powers in '{g.Name}' = 0x{(ulong)g.Powers:X}: 'Ignore landing point' {(can ? "YES" : "NO")}" +
                      $"{(client.Self.ActiveGroup == p.GroupID ? "" : " (group not active)")}{(fresh ? "" : " (cached)")}";
            }
            sb.Append($"; parcel owner: {(owner ? "yes" : "no")}; {grp}");
        }
        return sb.ToString();
    }

    static async Task<string> ParcelCmd(string[] a)
    {
        var sim = client.Network.CurrentSim;
        if (sim == null) return "not in a region";
        var at = client.Self.SimPosition;
        if (a.Length >= 2 && F(a[0], out var x) && F(a[1], out var y)) at = new Vector3(x, y, 0);
        return await LandingDiag(sim, at) ?? "no ParcelProperties reply within 10 s";
    }

    static async Task<string> LandmarkRaw(string key, CancellationToken ct)
    {
        var items = await LandmarkItems(ct);
        var it = items.FirstOrDefault(i => i.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                 ?? items.FirstOrDefault(i => i.Name.Contains(key, StringComparison.OrdinalIgnoreCase));
        if (it == null) return $"no landmark '{key}'";
        if (it.IsLink()) it = await FetchItemRO(it.AssetUUID, ct) ?? it;
        var lm = await LandmarkAsset(it, ct);
        if (lm == null) return $"'{it.Name}' item {it.UUID} asset {it.AssetUUID}: asset not readable";
        var text = lm.AssetData == null ? "" : Encoding.UTF8.GetString(lm.AssetData).TrimEnd('\0');
        return $"'{it.Name}' item {it.UUID} asset {it.AssetUUID} (asset reply id {lm.AssetID}) creator {it.CreatorID} perms owner=0x{(uint)it.Permissions.OwnerMask:X} " +
               $"next=0x{(uint)it.Permissions.NextOwnerMask:X}; {lm.AssetData?.Length ?? 0} bytes:\n{text.Replace("\n", "\\n\n")}";
    }
}
