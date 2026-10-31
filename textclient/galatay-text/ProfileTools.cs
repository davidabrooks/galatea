// ProfileTools (2026-10-31, David t2846u/t2848u): MCP surface for a new profile photo and deleting one pick.
// upload_texture -> PaidTextureUploadObj (one paid 2K upload per PaidMarker), set_profile_image -> SetProfileImageIdObj,
// list_picks / delete_pick: backup of every pick's details to /workspace/secondlife/picks-backup-*.json before a delete.
using System.Text.Json;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    // pure (tests): the MCP upload_texture guard; null = allowed
    internal static string UploadTextureRefusal(bool confirm, int maxL, string path) =>
        !confirm ? "refused: confirm=true is required (paid upload, needs David's OK for this exact image)"
        : maxL < 1 || maxL > MaxPaidUploadL ? $"refused: max_price must be 1..{MaxPaidUploadL}"
        : string.IsNullOrWhiteSpace(path) || !path.Trim().EndsWith(".j2c", StringComparison.OrdinalIgnoreCase) ? "refused: path must be a .j2c codestream"
        : null;

    static object PickObj(ProfilePick p) => new
    {
        pick_id = p.PickID.ToString(), name = p.Name, desc = p.Desc, sim = p.SimName, parcel_id = p.ParcelID.ToString(), parcel_name = p.User,
        original_name = p.OriginalName, snapshot_id = p.SnapshotID.ToString(), pos_global = new[] { p.PosGlobal.X, p.PosGlobal.Y, p.PosGlobal.Z },
        top_pick = p.TopPick, enabled = p.Enabled, sort_order = p.SortOrder
    };

    public static async Task<object> ListPicksObj()
    {
        if (!LoggedIn) return new { ok = false, result = "not logged in" };
        var picks = await FetchPicks(client.Self.AgentID);
        if (picks == null) return new { ok = false, result = "no AvatarPicksReply within 10 s (the server may send none when there are no picks)" };
        var list = new List<object>();
        foreach (var kv in picks)
        {
            var info = await FetchPickInfo(kv.Key);
            list.Add(info == null ? new { pick_id = kv.Key.ToString(), name = kv.Value, details = (object)null } : new { pick_id = kv.Key.ToString(), name = kv.Value, details = PickObj(info.Value) });
        }
        return new { ok = true, count = list.Count, picks = list };
    }

    public static async Task<object> DeletePickObj(string idText)
    {
        if (!LoggedIn) return new { ok = false, result = "not logged in" };
        if (!UUID.TryParse((idText ?? "").Trim(), out var id) || id == UUID.Zero) return new { ok = false, result = "explicit pick uuid required (see list_picks)" };
        var before = await FetchPicks(client.Self.AgentID);
        if (before == null) return new { ok = false, result = "could not read my pick list; nothing deleted" };
        if (!before.ContainsKey(id)) return new { ok = false, result = $"pick {id} is not in my pick list; nothing sent" };
        var info = await FetchPickInfo(id);
        if (info == null) return new { ok = false, result = $"no PickInfoReply for {id}; refusing to delete without a backup" };
        string backup = Path.Combine(Path.GetDirectoryName(LogPath)!, $"picks-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        try { File.WriteAllText(backup, JsonSerializer.Serialize(new { deleting = id.ToString(), pick = PickObj(info.Value), all_picks = before.ToDictionary(k => k.Key.ToString(), k => k.Value) }, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { return new { ok = false, result = "backup failed, nothing deleted: " + ex.Message }; }
        Log("pick", $"deleting pick {id} '{before[id]}' (backup {backup})");
        client.Self.PickDelete(id);
        await Task.Delay(2000);
        var after = await FetchPicks(client.Self.AgentID);
        bool gone = after == null || !after.ContainsKey(id);   // no reply = list now empty
        return new { ok = gone, deleted = id.ToString(), name = before[id], still_listed = after?.ContainsKey(id), remaining = after?.Values.ToList(), backup };
    }
}
