// ActiveGroup (2026-10-09, David: no group tag shown). 'group activate <none|group name|uuid>' -> Groups.ActivateGroup
// (UUID.Zero = none), then reports the active group like 'group list'. The choice persists in the live routes file
// routes/_active-group.json ({"group":"none"} or {"group":"<name>","id":"<uuid>"}); read at every login, ~8 s after
// which she activates that group (or none) instead of whatever the server last had. Membership is never changed.
using System.Text.Json;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static string ActiveGroupPath => Path.Combine(RouteDir, "_active-group.json");

    /// <summary>Pure: resolve a requested group ('none', a UUID, or a name / unique name prefix, case-insensitive)
    /// against the current groups. Returns (id, name, error); id = UUID.Zero for none.</summary>
    internal static (UUID id, string name, string error) ResolveActiveGroup(string want, IReadOnlyDictionary<UUID, string> groups)
    {
        want = (want ?? "").Trim().Trim('\'', '"');
        if (want.Length == 0) return (UUID.Zero, null, "no group given");
        if (want.Equals("none", StringComparison.OrdinalIgnoreCase)) return (UUID.Zero, "none", null);
        if (UUID.TryParse(want, out var u))
        {
            if (u == UUID.Zero) return (UUID.Zero, "none", null);
            return groups.TryGetValue(u, out var n) ? (u, n, null) : (UUID.Zero, null, $"not a member of group {u}");
        }
        var exact = groups.Where(g => string.Equals(g.Value, want, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return (exact[0].Key, exact[0].Value, null);
        var pre = groups.Where(g => g.Value != null && g.Value.StartsWith(want, StringComparison.OrdinalIgnoreCase)).ToList();
        if (pre.Count == 1) return (pre[0].Key, pre[0].Value, null);
        if (pre.Count > 1) return (UUID.Zero, null, $"'{want}' matches {pre.Count} groups: {string.Join(", ", pre.Select(p => $"'{p.Value}'"))}");
        return (UUID.Zero, null, $"no current group named '{want}'");
    }

    /// <summary>Pure: parse _active-group.json. Returns the 'want' string for ResolveActiveGroup (id preferred over name), or null if unusable.</summary>
    internal static string ParseActiveGroupPref(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (r.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && UUID.TryParse(id.GetString(), out var u))
                return u == UUID.Zero ? "none" : u.ToString();
            if (r.TryGetProperty("group", out var g) && g.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(g.GetString())) return g.GetString().Trim();
            return null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Pure: the file body for a choice (id = UUID.Zero -> none).</summary>
    internal static string ActiveGroupPrefJson(UUID id, string name) =>
        id == UUID.Zero
            ? "{\n  \"group\": \"none\"\n}\n"
            : JsonSerializer.Serialize(new Dictionary<string, string> { ["group"] = name ?? "", ["id"] = id.ToString() }, new JsonSerializerOptions { WriteIndented = true }) + "\n";

    static string ActiveGroupLine(Dictionary<UUID, Group> groups)
    {
        var act = client.Self.ActiveGroup;
        return $"active group: {(act == UUID.Zero ? "none" : groups.TryGetValue(act, out var ag) ? $"'{ag.Name}' ({act}), active title '{ag.MemberTitle}'" : act.ToString())}";
    }

    static async Task<(bool ok, string msg)> ApplyActiveGroup(string want, string why)
    {
        var (groups, fresh) = await FetchCurrentGroups();
        var (id, name, err) = ResolveActiveGroup(want, groups.ToDictionary(g => g.Key, g => g.Value.Name));
        if (err != null) return (false, $"{err}{(fresh ? "" : " (group list not refreshed)")}; nothing sent");
        client.Groups.ActivateGroup(id);
        for (int i = 0; i < 16 && client.Self.ActiveGroup != id; i++) await Task.Delay(500);
        var line = ActiveGroupLine(groups);
        Log("group", $"activate '{name}' ({id}) [{why}] -> {line}");
        return (client.Self.ActiveGroup == id, line);
    }

    static async Task<string> GroupActivateCmd(string[] a)
    {
        var want = string.Join(" ", a.Skip(1));
        if (want.Trim().Length == 0) return "usage: group activate <none|group name|uuid>";
        var (groups, _) = await FetchCurrentGroups(8000);
        var (id, name, err) = ResolveActiveGroup(want, groups.ToDictionary(g => g.Key, g => g.Value.Name));
        if (err != null) return err + "; nothing sent";
        var (ok, line) = await ApplyActiveGroup(id == UUID.Zero ? "none" : id.ToString(), "command");
        string saved;
        try { File.WriteAllText(ActiveGroupPath, ActiveGroupPrefJson(id, name)); saved = $"saved to {ActiveGroupPath} (applied at each login)"; }
        catch (Exception e) { saved = $"NOT saved ({e.Message})"; }
        return $"{line}{(ok ? "" : " (server has not confirmed yet)")}\n{saved}";
    }

    /// <summary>At login: activate the saved group (or none) from routes/_active-group.json, if present.</summary>
    static async Task ActiveGroupAfterLogin()
    {
        try
        {
            if (!File.Exists(ActiveGroupPath)) return;
            var want = ParseActiveGroupPref(File.ReadAllText(ActiveGroupPath));
            if (want == null) { Log("group", $"{ActiveGroupPath}: unreadable; active group left as the server had it"); return; }
            await Task.Delay(8000); // let the server's own AgentDataUpdate land first so ours wins
            var (ok, msg) = await ApplyActiveGroup(want, "login");
            if (!ok) Log("group", $"login active group '{want}': {msg}");
        }
        catch (Exception e) { Log("group", $"login active group failed: {e.Message}"); }
    }
}
