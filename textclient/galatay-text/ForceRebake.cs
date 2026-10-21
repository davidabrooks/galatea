// ForceRebake.cs (2026-10-21, David): Firestorm/LL 'Rebake Textures' equivalent.
// LL viewer: handle_rebake_textures -> forceBakeAllTextures (local only) + requestServerAppearanceUpdate ->
// serverAppearanceUpdateCoro: POST {cof_version} to the UpdateAvatarAppearance cap; on failure with "expected",
// retry with the server's COF version (after re-requesting avatar textures). The viewer skips the POST when
// cof_version <= last received; a FORCE here first bumps the COF version (add + remove one harmless link) so the
// server really rebakes from the current COF, then POSTs, bypassing LMV's guard, and reports the raw response.
using LibreMetaverse;
using LibreMetaverse.StructuredData;

namespace GalatayText;

public static partial class Program
{
    // pure (tested): the COF version to retry with, or null when done/no retry
    internal static int? RebakeRetryVersion(OSDMap res, int sent)
    {
        if (res == null) return null;
        if (res.ContainsKey("success") && res["success"].AsBoolean()) return null;
        if (res.ContainsKey("expected")) { var e = res["expected"].AsInteger(); return e != sent ? e : null; }
        return null;
    }

    static async Task<string> ForceRebakeCmd()
    {
        using var cts = new CancellationTokenSource(90000); var ct = cts.Token;
        var cap = client.Network.CurrentSim?.Caps?.CapabilityURI("UpdateAvatarAppearance");
        if (cap == null) return "force rebake: no UpdateAvatarAppearance cap";
        var cof = await CofFolder(ct); if (cof == null) return "force rebake: Current Outfit folder not found";
        var sb = new System.Text.StringBuilder();
        // bump COF version: one temporary link to the collar (always worn) created then removed
        try
        {
            var made = await client.Inventory.CreateLinkAsync(cof.UUID, N9CollarItem, "rebake-bump", "", InventoryType.Object, UUID.Random(), ct);
            if (made != null) await client.Inventory.RemoveItemsAsync(new[] { made.UUID }, ct);
            sb.Append(made != null ? "COF bumped; " : "COF bump: link not created; ");
        }
        catch (Exception ex) { sb.Append("COF bump failed: " + ex.GetBaseException().Message + "; "); }
        await ReadFolderRO(cof.UUID, ct, 30000);
        int ver = (client.Inventory.Store[cof.UUID] as InventoryFolder)?.Version ?? cof.Version;
        for (int i = 0; i < 3; i++)
        {
            OSDMap res = null; string err = null;
            try
            {
                var (_, data) = await client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, new OSDMap { ["cof_version"] = ver }, ct);
                if (data != null) res = OSDParser.Deserialize(data) as OSDMap;
            }
            catch (Exception ex) { err = ex.GetBaseException().Message; }
            if (res == null) { sb.Append($"POST cof_version {ver}: no reply ({err ?? "empty"})"); break; }
            bool ok = res.ContainsKey("success") && res["success"].AsBoolean();
            var tex = (res["textures"] as OSDArray)?.Select(t => t.AsUUID()).ToArray();
            sb.Append($"POST cof_version {ver}: success={ok}" + (res.ContainsKey("error") ? $" error='{res["error"].AsString()}'" : "") +
                      (res.ContainsKey("expected") ? $" expected={res["expected"].AsInteger()}" : "") +
                      (res.ContainsKey("cof_version") ? $" server_cof={res["cof_version"].AsInteger()}" : "") +
                      (tex != null && tex.Length > 11 ? $" bakes head={tex[8]} upper={tex[9]} lower={tex[10]} eyes={tex[11]}" : "") + "; ");
            if (ok) { try { client.Appearance.UpdateLastReceivedCOFVersion(res["cof_version"].AsInteger()); } catch { } break; }
            var next = RebakeRetryVersion(res, ver); if (next == null) break;
            client.Avatars.RequestAvatarProperties(client.Self.AgentID); ver = next.Value;
        }
        var msg = "force rebake: " + sb.ToString().TrimEnd(' ', ';');
        Log("appearance", msg); return msg;
    }
}
