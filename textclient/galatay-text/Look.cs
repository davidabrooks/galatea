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
            if (q == "side") q = "left";   // 2026-10-26: "side" = her left (90 deg), the level side view the seat / hover checks read
            if (q == "contact") q = "feet";
            if (LookSides.Contains(q) || q == "feet") outp.Add(q);   // feet = her left side, tight on her lowest contact (feet standing, hips sitting)
            else if (double.TryParse(q, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Math.Abs(d) <= 360 && !double.IsNaN(d))
                outp.Add(d.ToString("0.#", CultureInfo.InvariantCulture));
            else return null;
        }
        outp = outp.Distinct().ToList();
        return outp.Count is > 0 and <= 8 ? string.Join(",", outp) : null;
    }

    // pure (test): `photo hd [self|view|<avatar or object>] [landscape|portrait]` -> (look mode, target, orientation or "" = mode default), null = invalid
    internal static (string mode, string target, string orient)? PhotoHdArgs(string[] a)
    {
        if (a.Length == 0 || a[0].ToLowerInvariant() != "hd") return null;
        var w = a[1..].ToList(); string orient = "";
        if (w.Count > 0 && w[^1].ToLowerInvariant() is "landscape" or "portrait") { orient = w[^1].ToLowerInvariant(); w.RemoveAt(w.Count - 1); }
        if (w.Count == 0 || (w.Count == 1 && w[0].ToLowerInvariant() == "view")) return ("view", null, orient);
        if (w.Count == 1 && w[0].ToLowerInvariant() == "self") return ("self", null, orient);
        return ("at", string.Join(" ", w), orient);
    }

    // `photo hd ...` (2026-10-13, David): a 4K still for David, separate from her quick looks (look.py --hd: 3840x2160 or 2160x3840,
    // Cycles + denoise, full-res textures, soft shadows; PNG + JPG in /workspace/secondlife/photos). Minutes on the CPU, so it always
    // returns before it finishes and logs the paths as [look].
    // sky presets (look.py --sky=; vision/cloud-render/sky_presets.json, the viewer library's skies); "shared" = the parcel/region EEP
    internal static readonly string[] SkyPresets = { "shared", "default", "midday", "midday-legacy", "sunrise", "sunset", "midnight", "night", "foggy" };

    // pure (test): pull the sky choice out of `photo hd ...` args: `preset=<name>` / `sky=<name>` anywhere, or a bare preset
    // name as the last word (after the orientation is fine too). -> (remaining args, preset or null = shared), sky "?" = unknown preset=
    internal static (string[] rest, string sky) PhotoHdSky(string[] a)
    {
        var w = a.ToList(); string sky = null;
        for (int i = 0; i < w.Count; i++)
        {
            var t = w[i].ToLowerInvariant();
            if (t.StartsWith("preset=") || t.StartsWith("sky="))
            {
                var n = t[(t.IndexOf('=') + 1)..];
                sky = SkyPresets.Contains(n) ? n : "?"; w.RemoveAt(i); i--;
            }
        }
        if (sky == null)   // bare name: the last word, or the one before landscape/portrait (a name like "Sunset Resident" keeps its first word)
            for (int k = w.Count - 1; k >= Math.Max(1, w.Count - 2); k--)
            {
                var t = w[k].ToLowerInvariant();
                if (SkyPresets.Contains(t)) { sky = t; w.RemoveAt(k); break; }
                if (t is not ("landscape" or "portrait" or "far")) break;
            }
        return (w.ToArray(), sky == "shared" ? null : sky);
    }

    // pure (test): the render profile word (`ultra`, `highplus` / `high+`, or profile=<name>) anywhere in `photo hd ...`
    // -> (remaining args, profile or null = look.py's default ultra, David's own Firestorm Ultra since 2026-10-14; "?" = unknown profile=)
    internal static (string[] rest, string profile) PhotoHdProfile(string[] a)
    {
        var w = new List<string>(); string prof = null;
        foreach (var x in a)
        {
            var t = x.ToLowerInvariant(); if (t.StartsWith("profile=")) { t = t[8..]; prof = t is "ultra" or "highplus" or "high+" ? t : "?"; continue; }
            if (t is "ultra" or "highplus" or "high+") { prof = t; continue; }
            w.Add(x);
        }
        return (w.ToArray(), prof == "high+" ? "highplus" : prof);
    }

    // pure (test): photo hd export radius = the profile's Firestorm draw distance (High+ 128 m, Ultra 256 m), capped at 96 m
    // (scene-mesher's far LOD beyond 30 m; the 2026-10-13 20:05 HD at 32 m dropped the neighbour's house David's view shows)
    // 2026-10-13 (David): the profile's draw distance (High+ 128, Ultra 256) when the box has the memory for it: a 96 m
    // HD export peaked ~7 GB in Blender (OOM-killed once beside the client); 128 m needs ~10 GB free, 256 m ~14 GB
    // pure (test): `photo hd ... nossr nomirrors nodof nosmaa nosharpen noaces noimpostors` -> (remaining args, look.py --no= list)
    internal static readonly string[] PhotoHdOptOutNames = { "ssr", "mirrors", "dof", "smaa", "sharpen", "aces", "impostors" };
    internal static (string[] rest, string no) PhotoHdOptOuts(string[] a)
    {
        var w = new List<string>(); var no = new List<string>();
        foreach (var x in a)
        {
            var t = x.ToLowerInvariant();
            if (t.StartsWith("no") && PhotoHdOptOutNames.Contains(t[2..])) { if (!no.Contains(t[2..])) no.Add(t[2..]); continue; }
            w.Add(x);
        }
        return (w.ToArray(), no.Count == 0 ? null : string.Join(",", no));
    }

    // pure (test): `photo hd ... fov <deg>` / `fov=<deg>` (2026-10-14, David): vertical field of view, 10..120 deg; "?" = bad value
    internal static (string[] rest, string fov) PhotoHdFov(string[] a)
    {
        var w = new List<string>(); string fov = null;
        for (int i = 0; i < a.Length; i++)
        {
            var t = a[i].ToLowerInvariant(); string v = null;
            if (t.StartsWith("fov=")) v = t[4..]; else if (t == "fov") v = i + 1 < a.Length ? a[++i] : "";
            if (v == null) { w.Add(a[i]); continue; }
            fov = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 10 && d <= 120 ? d.ToString(CultureInfo.InvariantCulture) : "?";
        }
        return (w.ToArray(), fov);
    }

    internal static float PhotoHdRadius(string profile, double freeGb = -1)
    {
        float want = profile == "highplus" ? 128f : 256f;   // null = the default (ultra)
        if (freeGb < 0) freeGb = MemAvailableGb();
        if (want >= 256 && freeGb >= 14) return 256f;
        if (want >= 128 && freeGb >= 10) return 128f;
        return 96f;
    }
    static double MemAvailableGb()
    {
        try
        {
            var l = File.ReadLines("/proc/meminfo").FirstOrDefault(x => x.StartsWith("MemAvailable:"));
            return l == null ? 0 : double.Parse(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) / 1048576.0;
        }
        catch { return 0; }
    }

    static Task<string> PhotoCmd(string[] a)
    {
        var (a2, prof) = PhotoHdProfile(a); a = a2;
        var (a3, no) = PhotoHdOptOuts(a); a = a3;
        var (a4, fov) = PhotoHdFov(a); a = a4;
        const string usage = "usage: photo hd [self|view|<avatar or object name>] [landscape|portrait] [sunset|midday|midday-legacy|sunrise|midnight|night|foggy|default|shared | preset=<name>] [ultra (default)|highplus] [nossr] [nomirrors] [nodof] [nosmaa] [nosharpen] [noaces] [noimpostors] [fov <deg> (default 60, Firestorm)]  (4K PNG + JPG in /workspace/secondlife/photos; self defaults to portrait; default sky = the shared parcel/region environment)";
        var (rest, sky) = PhotoHdSky(a);
        if (sky == "?" || prof == "?" || fov == "?") return Task.FromResult(usage);
        var p = PhotoHdArgs(rest);
        if (p == null) return Task.FromResult(usage);
        var la = p.Value.mode == "view" ? Array.Empty<string>() : p.Value.mode == "self" ? new[] { "self" } : new[] { "at", p.Value.target };
        return LookCmd(la, p.Value.orient.Length > 0 ? "--hd=" + p.Value.orient : "--hd", sky, prof, no, fov);
    }

    // the hover in force (AgentPreferences), read fresh: a look after a fix must show the fix (2026-10-26: the render used our own appearance value, which stayed 0)
    static async Task<Dictionary<string, string>> LookHoverEnv()
    {
        if (client.Self.SittingOn != 0) return Hover.SeatLookEnv();   // on furniture: hips against the seat, the sim's seated placement, no hover model
        double size = SelfAv()?.Scale.Z ?? 0; if (size < 1) return new();
        var (kind, pose) = hoverSvc.PoseNow(); bool android = AndroidFormNow(); var nums = hoverSvc.PoseNums();
        var (hov, _) = await EffectiveHoverFresh();
        var env = Hover.HoverCheckEnv(size, hov ?? PinnedHover(), Hover.PoseBiasFor(nums, android, kind, pose ?? ""), null, SimHoverNow());
        foreach (var kv in Hover.ContactEnv(kind)) env[kv.Key] = kv.Value;
        env["GT_HOVER_CAL"] = Hover.BiasCalibrated(nums, android, kind, pose) ? "calibrated" : "default";
        return env;
    }

    static async Task<string> LookCmd(string[] a, string hd = null, string sky = null, string profile = null, string no = null, string fov = null)
    {
        const string usage = "usage: look [self [front|back|left|right|side|<degrees>|all]|around|at <avatar or object name>] [fast] [far]  (self also: views feet = tight close-up on her lowest floor contact (feet / hips / shins), zoom=feet = every view that tight; preset=<hover-ground|hover-seat|photo-fast|outfit|photo-hd> scope=<full|nearby|seat|none> floor=<auto|on|off> avatars=off hide-attach=<points> textures=off materials=off small scale=<n> size=WxH samples=<n> jpeg=<n> exposure=<stops> cam-margin=<n> cam-lens=<mm> cam-fov=<deg> cam-height=<m>; docs/render-presets.md)";
        // render options (look_scope.py) are only read for `look self` (or a bare preset=...): a target name can't swallow them
        var lookOpts = new List<string>();
        if (a.Length > 0 && (a[0] == "self" || a.Any(x => x.StartsWith("preset=", StringComparison.Ordinal)) && a[0] is not ("around" or "at")))
        {
            var (aRest, oList, pre) = LookOptions(a); lookOpts = oList; a = aRest.Length == 0 ? new[] { "self" } : aRest;
            if (pre == "photo-hd") return await PhotoCmd(new[] { "hd", "self" });
        }
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
            float er = hd != null ? PhotoHdRadius(profile) : mode == "self" ? 8 : far ? 96 : 32;   // photo hd: always her surroundings, `self` too
            var tWait = sw.Elapsed.TotalSeconds;
            if (mode != "self" || hd != null) await EnsureNearbyAttachments(er, hd != null);   // wait for nearby avatars' attachments (stand-ins for the rest)
            tWait = sw.Elapsed.TotalSeconds - tWait; var tEx = sw.Elapsed.TotalSeconds;
            var exArgs = new List<string> { "export", er.ToString(CultureInfo.InvariantCulture) };
            if (hd == null) exArgs.Add("noenv");   // her own looks: fixed neutral light, no environment fetch (David 2026-10-13)
            var ex = await SceneExport(exArgs.ToArray());   // far: backdrop beyond 30 m (look.py --far)
            tEx = sw.Elapsed.TotalSeconds - tEx;
            var scene = ex.Split('\n').Last();
            if (!scene.EndsWith("scene.json")) { lookGate.Release(); return "look: export failed: " + ex; }
            var psi = new ProcessStartInfo(LookPython) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(LookPy); psi.ArgumentList.Add(Path.GetDirectoryName(scene)!); psi.ArgumentList.Add(mode);
            if (mode == "at") psi.ArgumentList.Add(string.Join(" ", w[1..]));   // argv, never a shell: names can't inject
            if (fast) psi.ArgumentList.Add("--fast");
            if (far) psi.ArgumentList.Add("--far");
            if (views != null) psi.ArgumentList.Add("--views=" + views);
            if (mode == "self" && hd == null) foreach (var o in lookOpts) psi.ArgumentList.Add(o);
            if (hd != null) psi.ArgumentList.Add(hd);
            if (hd != null && sky != null) psi.ArgumentList.Add("--sky=" + sky);
            if (hd != null && profile != null) psi.ArgumentList.Add("--profile=" + profile);
            if (hd != null && no != null) psi.ArgumentList.Add("--no=" + no);
            if (hd != null && fov != null) psi.ArgumentList.Add("--fov=" + fov);
            // `look self` draws her on the real floor: her real hover, pose bias and body size so the render's gap is her real gap (PoseHover.cs
            // HoverCheckEnv, same as the automatic check). Sit vs stand is what the AO really plays (AoSit.cs), not the sim's state.
            if (mode == "self" && hd == null) try { foreach (var kv in await LookHoverEnv()) psi.Environment[kv.Key] = kv.Value; } catch (Exception hx) { Log("look", "hover env skipped: " + hx.GetBaseException().Message); }
            // the client-side stages, so look.py's timing summary covers the whole look
            psi.Environment["GT_LOOK_PRE_S"] = string.Format(CultureInfo.InvariantCulture, "wait={0:F1},export={1:F1}", tWait, tEx);
            psi.Environment["GT_LOOK_EXPORT_S"] = lastExportStages;   // export sub-stages, e.g. prims=1.2(20696),bakes=3.4(...)
            var pr = Process.Start(psi)!; var outText = new StringBuilder(); var errText = new StringBuilder();
            pr.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outText) outText.AppendLine(e.Data); };
            pr.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errText) errText.AppendLine(e.Data); };
            pr.BeginOutputReadLine(); pr.BeginErrorReadLine();
            string Result() { lock (outText) lock (errText) return pr.ExitCode == 0 ? outText.ToString().Trim() : $"look failed (exit {pr.ExitCode}): {(errText.ToString() + outText).Trim()}"; }
            var done = pr.WaitForExitAsync().ContinueWith(_ => { var r = Result(); Log("look", $"{mode} {string.Join(" ", w.Skip(1))}: {r.Replace('\n', ' ')}"); lookGate.Release(); return r; });
            int wait = hd != null ? 5 : int.TryParse(Env("GT_LOOK_WAIT_S", "75"), out var ws) && ws > 0 ? ws : 75;
            if (await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(Math.Max(1, wait - sw.Elapsed.TotalSeconds)))) == done) return await done;
            return $"look: still rendering after {sw.Elapsed.TotalSeconds:F0} s (pid {pr.Id}); the image path(s) will be logged as [look] in the client log";
        }
        catch (Exception e) { lookGate.Release(); return "look failed: " + e.Message; }
    }
}
