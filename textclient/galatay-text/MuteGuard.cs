// MuteGuard (added 2026-09-25): server-side mute list + local drop of everything from muted avatars.
// - mute / unmute / mutelist commands (UpdateMuteListEntry / RemoveMuteListEntry, MuteType.Resident, MuteListRequest)
// - local copy (mutes.json) loaded at startup, refreshed from the server at login and after each change
// - chat (incl. objects owned by a muted avatar), IMs, group/conference lines, teleport/friend/inventory offers
//   from muted avatars are dropped before logging, poll_events or the webhook; only "[muted-drop] <name>" is logged
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static readonly string MuteFile = Env("GT_MUTE_FILE", "/home/box/viewers/textclient/mutes.json");
    static readonly ConcurrentDictionary<UUID, string> muted = new();
    static readonly ConcurrentDictionary<UUID, DateTime> lastDropLog = new();
    static DateTime muteFetchedAt = DateTime.MinValue;
    static string muteFetchStatus = "not fetched yet";

    static void LoadLocalMutes()
    {
        try
        {
            if (!File.Exists(MuteFile)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(MuteFile)) ?? new();
            foreach (var kv in d) if (UUID.TryParse(kv.Key, out var id)) muted[id] = kv.Value;
        }
        catch (Exception ex) { Log("mute", "local mute file unreadable: " + ex.GetType().Name); }
    }
    static void SaveLocalMutes()
    {
        try
        {
            var tmp = MuteFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(muted.ToDictionary(k => k.Key.ToString(), v => v.Value), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, MuteFile, true);
        }
        catch (Exception ex) { Log("mute", "cannot save local mute file: " + ex.GetType().Name); }
    }

    public static bool IsMuted(UUID id) => id != UUID.Zero && muted.ContainsKey(id);

    // true = drop it. Logs one line without the message text (throttled to 1 line / 3 s per sender).
    static bool MutedDrop(UUID id, string name, string what)
    {
        if (!IsMuted(id)) return false;
        var now = DateTime.Now;
        if (!lastDropLog.TryGetValue(id, out var t) || (now - t).TotalSeconds >= 3)
        {
            lastDropLog[id] = now;
            Log("muted-drop", $"{(string.IsNullOrWhiteSpace(name) ? muted.GetValueOrDefault(id, id.ToString()) : name)} ({what})");
        }
        return true;
    }

    // fetch the server mute list; replaces the local copy with the server's resident entries when a reply arrives
    static async Task<bool> RefreshMutes(int timeoutMs = 15000)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(object s, EventArgs e) => tcs.TrySetResult(true);
        client.Self.MuteListUpdated += H;
        try
        {
            client.Self.RequestMuteList();
            var got = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) == tcs.Task;
            if (got)
            {
                await Task.Delay(300);
                var server = client.Self.MuteList.Values.Where(m => m.Type == MuteType.Resident && m.ID != UUID.Zero).ToList();
                foreach (var k in muted.Keys.Where(k => !server.Any(m => m.ID == k)).ToList()) muted.TryRemove(k, out _);
                foreach (var m in server) muted[m.ID] = m.Name;
                SaveLocalMutes();
                muteFetchedAt = DateTime.Now; muteFetchStatus = $"server list received {muteFetchedAt:HH:mm:ss} ({client.Self.MuteList.Count} entries, {server.Count} residents)";
            }
            else muteFetchStatus = $"no mute list reply within {timeoutMs / 1000} s at {DateTime.Now:HH:mm:ss} (server sends none when the list is empty or unchanged); kept local copy";
            Log("mute", muteFetchStatus);
            return got;
        }
        finally { client.Self.MuteListUpdated -= H; }
    }

    static async Task<string> LegacyName(UUID id)
    {
        var n = NameOf(id);
        if (n != id.ToString()) return n;
        client.Avatars.RequestAvatarName(id);
        for (int i = 0; i < 20; i++) { await Task.Delay(250); n = NameOf(id); if (n != id.ToString()) return n; }
        return null;
    }

    static async Task<string> MuteCmds(string cmd, string rest)
    {
        if (cmd == "mutelist")
        {
            await RefreshMutes();
            var sb = new StringBuilder($"{muteFetchStatus}\n");
            var all = client.Self.MuteList.Values.ToList();
            sb.AppendLine($"server mute list ({all.Count}):");
            foreach (var m in all) sb.AppendLine($"  {m.Type} {m.ID} '{m.Name}' flags {m.Flags}");
            sb.Append($"local drop list ({muted.Count}): " + string.Join(", ", muted.Select(kv => $"{kv.Value} {kv.Key}")));
            return sb.ToString();
        }
        if (string.IsNullOrWhiteSpace(rest)) return $"usage: {cmd} <uuid|First Last|username>";
        var id = await ResolveAvatar(rest);
        if (id == UUID.Zero) return $"could not resolve avatar '{rest}'";
        if (id == client.Self.AgentID) return "refusing to mute myself";
        if (cmd == "mute")
        {
            var name = await LegacyName(id) ?? (UUID.TryParse(rest.Trim(), out _) ? null : rest.Trim());
            if (string.IsNullOrWhiteSpace(name)) return $"could not look up the legacy name for {id}; try: mute \"First Last\"";
            muted[id] = name; SaveLocalMutes();            // drop immediately, even before the server confirms
            client.Self.UpdateMuteListEntry(MuteType.Resident, id, name);
            Log("mute", $"muted {name} ({id}) (server UpdateMuteListEntry sent, local drop active)");
            await Task.Delay(1500);
            await RefreshMutes();
            muted[id] = name; SaveLocalMutes();            // keep even if the refresh raced the server update
            var onServer = client.Self.MuteList.Values.Any(m => m.ID == id);
            return $"muted {name} ({id}); on server list after refresh: {(onServer ? "yes" : "not confirmed")}; local drop: active";
        }
        else // unmute
        {
            var entries = client.Self.MuteList.Values.Where(m => m.ID == id).ToList();
            var name = entries.FirstOrDefault()?.Name ?? muted.GetValueOrDefault(id) ?? await LegacyName(id) ?? "";
            client.Self.RemoveMuteListEntry(id, name);
            muted.TryRemove(id, out _); SaveLocalMutes();
            Log("mute", $"unmuted {name} ({id})");
            await Task.Delay(1500);
            await RefreshMutes();
            if (client.Self.MuteList.Values.Any(m => m.ID == id)) { muted[id] = name; SaveLocalMutes(); return $"unmute sent for {name} ({id}) but the server list still has it; local drop kept"; }
            return $"unmuted {name} ({id}); not on server list after refresh; local drop removed";
        }
    }
}
