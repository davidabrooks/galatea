// StopAnims.cs (2026-10-19, David: "stop dancing"): 'stopanims' stops every animation playing on me
// (AnimationStop for each signalled anim) and pauses the wander so nothing of mine restarts one.
// A worn AO in DANCES mode restarts its dance: turn that off on the HUD itself (touch-attachment ao ...).
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static async Task<string> StopAnimsCmd()
    {
        if (!LoggedIn) return "not logged in";
        Dictionary<UUID, (int seq, UUID src)> cur; lock (animLock) cur = new(ownAnims);
        var lines = new List<string>();
        foreach (var kv in cur) { client.Self.AnimationStop(kv.Key, true); lines.Add($"stopped {AnimName(kv.Key)} (from {SrcDesc(kv.Value.src)})"); }
        string w; try { w = await WanderCmds(new[] { "pause" }); } catch (Exception ex) { w = "wander pause failed: " + ex.Message; }
        await Task.Delay(4000);
        Dictionary<UUID, (int seq, UUID src)> after; lock (animLock) after = new(ownAnims);
        var msg = $"stopanims: sent stop for {cur.Count} animation(s); wander: {w.Split('\n')[0]}\n" + string.Join("\n", lines) +
                  $"\nplaying 4 s later ({after.Count}): " + string.Join("; ", after.Select(kv => $"{AnimName(kv.Key)} from {SrcDesc(kv.Value.src)}"));
        Log("anim", msg.Replace('\n', ' ')); return msg;
    }
}
