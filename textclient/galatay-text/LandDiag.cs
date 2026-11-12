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

    // 2026-11-11 David: parcel_info — name, owner, area, and ParcelProperties flags (scripts, build, fly, push, damage, voice, access/ban, music/media).
    internal sealed record ParcelFlagInfo(
        string Scripts, bool Build, bool Fly, bool PushRestrict, bool Damage, bool Voice,
        bool AccessGroup, bool AccessList, bool BanList, bool MusicUrl, bool MediaUrl)
    {
        public bool AccessRestricted => AccessGroup || AccessList;
        public string Summary =>
            $"scripts={Scripts} build={(Build ? "yes" : "no")} fly={(Fly ? "yes" : "no")} push={(PushRestrict ? "restricted" : "ok")} " +
            $"damage={(Damage ? "yes" : "no")} voice={(Voice ? "yes" : "no")} " +
            $"access={(AccessRestricted ? (AccessGroup && AccessList ? "group+list" : AccessGroup ? "group" : "list") : "open")}" +
            $"{(BanList ? " ban-list" : "")} music={(MusicUrl ? "yes" : "no")} media={(MediaUrl ? "yes" : "no")}";
    }

    internal static ParcelFlagInfo DecodeParcelFlags(ParcelFlags flags, string musicUrl = null, string mediaUrl = null)
    {
        bool other = flags.HasFlag(ParcelFlags.AllowOtherScripts);
        bool group = flags.HasFlag(ParcelFlags.AllowGroupScripts);
        string scripts = !other && !group ? "no-scripts" : other && group ? "other+group" : other ? "other" : "group";
        return new(
            scripts,
            flags.HasFlag(ParcelFlags.CreateObjects),
            flags.HasFlag(ParcelFlags.AllowFly),
            flags.HasFlag(ParcelFlags.RestrictPushObject),
            flags.HasFlag(ParcelFlags.AllowDamage),
            flags.HasFlag(ParcelFlags.AllowVoiceChat),
            flags.HasFlag(ParcelFlags.UseAccessGroup),
            flags.HasFlag(ParcelFlags.UseAccessList),
            flags.HasFlag(ParcelFlags.UseBanList),
            !string.IsNullOrWhiteSpace(musicUrl),
            !string.IsNullOrWhiteSpace(mediaUrl));
    }

    internal static string ParcelInfoLine(string name, string owner, int area, int localId, ParcelFlagInfo f) =>
        $"parcel '{name}' owner {owner} area {area} m² local {localId}: {f.Summary}";

    public static async Task<object> ParcelInfoObj(float? x = null, float? y = null)
    {
        if (!LoggedIn) return new { ok = false, result = "not logged in" };
        var sim = client.Network.CurrentSim;
        if (sim == null) return new { ok = false, result = "not in a region" };
        var at = client.Self.SimPosition;
        if (x is float px && y is float py) at = new Vector3(px, py, 0);
        var p = await ParcelAt(sim, at.X, at.Y);
        if (p == null) return new { ok = false, result = "no ParcelProperties reply within 10 s" };
        var f = DecodeParcelFlags(p.Flags, p.MusicURL, p.Media.MediaURL);
        return new
        {
            ok = true,
            name = p.Name,
            owner = p.OwnerID.ToString(),
            group_owned = p.IsGroupOwned,
            group = p.GroupID.ToString(),
            area = p.Area,
            local_id = p.LocalID,
            region = sim.Name,
            scripts = f.Scripts,
            build = f.Build,
            fly = f.Fly,
            push_restrict = f.PushRestrict,
            damage = f.Damage,
            voice = f.Voice,
            access_group = f.AccessGroup,
            access_list = f.AccessList,
            ban_list = f.BanList,
            access_restricted = f.AccessRestricted,
            music_url = f.MusicUrl,
            media_url = f.MediaUrl,
            summary = ParcelInfoLine(p.Name, p.OwnerID.ToString(), p.Area, p.LocalID, f)
        };
    }

    static async Task<string> ParcelInfoCmd(string[] a)
    {
        float? x = null, y = null;
        if (a.Length >= 2 && F(a[0], out var px) && F(a[1], out var py)) { x = px; y = py; }
        var o = await ParcelInfoObj(x, y);
        var t = o.GetType();
        object v(string n) => t.GetProperty(n)?.GetValue(o);
        if (v("ok") is false) return v("result")?.ToString() ?? "failed";
        return v("summary")?.ToString() ?? "ok";
    }

    static async Task<string> ParcelCmd(string[] a)
    {
        if (a.Length > 0 && a[0].Equals("info", StringComparison.OrdinalIgnoreCase))
            return await ParcelInfoCmd(a.Skip(1).ToArray());
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
