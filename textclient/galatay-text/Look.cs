// Look.cs (2026-10-04, David: let her see) - `look [self|around|at <avatar|object>] [fast]`
//   READ-ONLY in-world: a `scene export` (radius 96, backdrop beyond 30 m; 8 for self), then the box script vision/look.py meshes it and renders
//   on the CPU (Cycles, nice 10) and prints the image path(s). Waits up to GT_LOOK_WAIT_S (75 s, under the 85 s command
//   reply cap); a longer render keeps going and its paths land in the log as [look]. One look at a time (shared work dir).
using System.Diagnostics;
using System.Text;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static readonly SemaphoreSlim lookGate = new(1, 1);
    static readonly string LookPy = Env("GT_LOOK_PY", "/workspace/galatea-sl-repo/vision/look.py");   // the main worktree (galatea-vr is a feature-branch clone)
    static readonly string LookPython = Env("GT_LOOK_PYTHON", "/home/box/tools/imgvenv/bin/python");

    static async Task<string> LookCmd(string[] a)
    {
        const string usage = "usage: look [self|around|at <avatar or object name>] [fast]";
        bool fast = a.Contains("fast"); var w = a.Where(x => x != "fast").ToArray();
        string mode = w.Length == 0 ? "view" : w[0] is "self" or "around" or "at" ? w[0] : null;
        if (mode == null || (mode == "at") != (w.Length > 1) || (mode != "at" && w.Length > 1)) return usage;
        if (!File.Exists(LookPy)) return $"look: {LookPy} not found (set GT_LOOK_PY)";
        if (!await lookGate.WaitAsync(0)) return "look: another look is still rendering; try again shortly";
        var sw = Stopwatch.StartNew();
        try
        {
            if (mode == "at")   // root prim names for the target match: same property fetch `nearby` uses
                await EnsureProperties(Sim, Sim.ObjectsPrimitives.Values.Where(p => p != null && p.ParentID == 0 && p.PrimData.PCode == PCode.Prim
                    && Vector3.Distance(p.Position, client.Self.SimPosition) <= 32).ToList());
            if (mode == "at")   // she deliberately looks: a short head turn others can see (LookAt.cs; nothing in private mode)
            {
                var tn = string.Join(" ", w[1..]).ToLowerInvariant();
                var av = Sim.ObjectsAvatars.Values.FirstOrDefault(x => x != null && x.ID != client.Self.AgentID && (x.Name ?? "").ToLowerInvariant().Contains(tn));
                var ob = av == null ? Sim.ObjectsPrimitives.Values.FirstOrDefault(p => p != null && p.ParentID == 0 && (p.Properties?.Name ?? "").ToLowerInvariant().Contains(tn)
                    && Vector3.Distance(p.Position, client.Self.SimPosition) <= 32) : null;
                if (av != null) HeadTurnTo(av.ID, "look at"); else if (ob != null) HeadTurnToPoint(ob.Position, "look at");
            }
            var ex = await SceneExport(new[] { "export", mode == "self" ? "8" : "96" });   // 96 m: backdrop beyond 30 m (look.py)
            var scene = ex.Split('\n').Last();
            if (!scene.EndsWith("scene.json")) { lookGate.Release(); return "look: export failed: " + ex; }
            var psi = new ProcessStartInfo(LookPython) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(LookPy); psi.ArgumentList.Add(Path.GetDirectoryName(scene)!); psi.ArgumentList.Add(mode);
            if (mode == "at") psi.ArgumentList.Add(string.Join(" ", w[1..]));   // argv, never a shell: names can't inject
            if (fast) psi.ArgumentList.Add("--fast");
            var pr = Process.Start(psi)!; var outText = new StringBuilder(); var errText = new StringBuilder();
            pr.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outText) outText.AppendLine(e.Data); };
            pr.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errText) errText.AppendLine(e.Data); };
            pr.BeginOutputReadLine(); pr.BeginErrorReadLine();
            string Result() { lock (outText) lock (errText) return pr.ExitCode == 0 ? outText.ToString().Trim() : $"look failed (exit {pr.ExitCode}): {(errText.ToString() + outText).Trim()}"; }
            var done = pr.WaitForExitAsync().ContinueWith(_ => { var r = Result(); Log("look", $"{mode} {string.Join(" ", w.Skip(1))}: {r.Replace('\n', ' ')}"); lookGate.Release(); return r; });
            int wait = int.TryParse(Env("GT_LOOK_WAIT_S", "75"), out var ws) && ws > 0 ? ws : 75;
            if (await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(Math.Max(1, wait - sw.Elapsed.TotalSeconds)))) == done) return await done;
            return $"look: still rendering after {sw.Elapsed.TotalSeconds:F0} s (pid {pr.Id}); the image path(s) will be logged as [look] in the client log";
        }
        catch (Exception e) { lookGate.Release(); return "look failed: " + e.Message; }
    }
}
