// Look.cs (2026-10-04, David: let her see) - `look [self [front|back|left|right|<degrees>|all]|around|at <avatar|object>] [fast] [far]`
//   self + a side (2026-10-07, David: "you should be able to take a render at any angle"): only the full-body render, from
//   that side of her (degrees around her vertical axis, counter-clockwise from her front: 90 = her left, 180 = her back);
//   'all' = front, back, left and right; several as a comma list (back,left,45).
//   READ-ONLY in-world: a `scene export` (radius 32, the near scene; `far`: 96, backdrop beyond 30 m; 8 for self), then the box script vision/look.py meshes it and renders
//   on the CPU (Cycles, nice 10) and prints the image path(s). Waits up to GT_LOOK_WAIT_S (75 s, under the 85 s command
//   reply cap); a longer render keeps going and its paths land in the log as [look]. One look at a time (shared work dir).
using System.Diagnostics;
using System.Globalization;
using System.Text;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static readonly SemaphoreSlim lookGate = new(1, 1);
    static readonly string LookPy = Env("GT_LOOK_PY", "/workspace/galatea-sl-repo/vision/look.py");   // the main worktree (galatea-vr is a feature-branch clone)
    static readonly string LookPython = Env("GT_LOOK_PYTHON", "/home/box/tools/imgvenv/bin/python");

    static readonly string[] LookSides = { "front", "back", "left", "right" };
    // pure (test): 'look self <arg>' side list -> look.py --views value ("back,left,45": order kept, duplicates dropped), null = invalid
    internal static string LookSelfViews(string arg)
    {
        var v = (arg ?? "").Trim().ToLowerInvariant();
        if (v.Length == 0) return null;
        var parts = v == "all" ? LookSides.ToList() : v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var outp = new List<string>();
        foreach (var p in parts)
        {
            var q = p.EndsWith("deg") ? p[..^3] : p;
            if (LookSides.Contains(q)) outp.Add(q);
            else if (double.TryParse(q, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Math.Abs(d) <= 360 && !double.IsNaN(d))
                outp.Add(d.ToString("0.#", CultureInfo.InvariantCulture));
            else return null;
        }
        outp = outp.Distinct().ToList();
        return outp.Count is > 0 and <= 8 ? string.Join(",", outp) : null;
    }

    static async Task<string> LookCmd(string[] a)
    {
        const string usage = "usage: look [self [front|back|left|right|<degrees>|all]|around|at <avatar or object name>] [fast] [far]";
        bool fast = a.Contains("fast"), far = a.Contains("far"); var w = a.Where(x => x != "fast" && x != "far").ToArray();
        string mode = w.Length == 0 ? "view" : w[0] is "self" or "around" or "at" ? w[0] : null;
        string views = mode == "self" && w.Length > 1 ? LookSelfViews(string.Join(",", w[1..])) : null;
        if (mode == "self" && w.Length > 1 && views == null) return usage + "  (sides: front, back, left, right, all, or degrees -360..360 counter-clockwise from her front; comma list, max 8)";
        if (mode == null || (mode == "at" && w.Length < 2) || (mode == "around" && w.Length > 1)) return usage;
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
            float er = mode == "self" ? 8 : far ? 96 : 32;
            var tWait = sw.Elapsed.TotalSeconds;
            if (mode != "self") await EnsureNearbyAttachments(er);   // wait for nearby avatars' attachments (stand-ins for the rest)
            tWait = sw.Elapsed.TotalSeconds - tWait; var tEx = sw.Elapsed.TotalSeconds;
            var ex = await SceneExport(new[] { "export", er.ToString(CultureInfo.InvariantCulture) });   // far: backdrop beyond 30 m (look.py --far)
            tEx = sw.Elapsed.TotalSeconds - tEx;
            var scene = ex.Split('\n').Last();
            if (!scene.EndsWith("scene.json")) { lookGate.Release(); return "look: export failed: " + ex; }
            var psi = new ProcessStartInfo(LookPython) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(LookPy); psi.ArgumentList.Add(Path.GetDirectoryName(scene)!); psi.ArgumentList.Add(mode);
            if (mode == "at") psi.ArgumentList.Add(string.Join(" ", w[1..]));   // argv, never a shell: names can't inject
            if (fast) psi.ArgumentList.Add("--fast");
            if (far) psi.ArgumentList.Add("--far");
            if (views != null) psi.ArgumentList.Add("--views=" + views);
            // the client-side stages, so look.py's timing summary covers the whole look
            psi.Environment["GT_LOOK_PRE_S"] = string.Format(CultureInfo.InvariantCulture, "wait={0:F1},export={1:F1}", tWait, tEx);
            psi.Environment["GT_LOOK_EXPORT_S"] = lastExportStages;   // export sub-stages, e.g. prims=1.2(20696),bakes=3.4(...)
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
