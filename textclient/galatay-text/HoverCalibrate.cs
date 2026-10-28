// HoverCalibrate.cs (2026-10-26 David: "rely on visible confirmations, but that only works if your renderer is placing the ground at the correct
// place in relation to your avatar. You need a way to calibrate that.")
// The hover check's render places her from the sim's position, her body size, her hover and a per-posture BIAS (PoseHover.cs). The bias is the
// model error: it can't be derived (the sim's capsule, the viewer's own body-size formula and the AO animation's hip height all differ a few
// centimetres), so it is SET FROM WHAT DAVID SEES IN HIS VIEWER:
//   hover calibrate stand|sit|here <cm>   cm = what he sees right now at the hover in force: + floating above the ground, - sunk into it, 0 touching
//   hover calibrate show | reset stand|sit|here
// It renders her now at the hover in force (AgentPreferences, what his viewer applies), takes the render's gap g and sets
// bias' = bias + (g - cm/100) so the render's gap equals his (PoseBiasFromPhoto). `stand`/`sit` set the posture's bias (bias|android|stand / sit),
// `here` the bias of the exact pose she is in (e.g. one VAMOSITG sit). It never changes her hover and never stands or sits her.
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

internal sealed partial class Hover
{
    internal const double CalibMaxCm = 30;
    // pure (tested): parse the words after "hover calibrate" -> (action show|set|reset, target stand|sit|here, cm) or an error
    internal static (bool ok, string action, string target, double cm, string err) HoverCalibrateParse(string[] a)
    {
        const string usage = "usage: hover calibrate stand|sit|here <cm> (what you see now: + above the ground, - sunk) | hover calibrate show | hover calibrate reset stand|sit|here";
        if (a == null || a.Length == 0) return (false, null, null, 0, usage);
        var w = a.Select(x => x.ToLowerInvariant()).ToArray();
        if (w is ["show"]) return (true, "show", null, 0, null);
        if (w is ["reset", "stand" or "sit" or "here"]) return (true, "reset", w[1], 0, null);
        if (w.Length is 2 or 3 && w[0] is "stand" or "sit" or "here" && (w.Length == 2 || w[2] == "cm"))
        {
            var num = Regex.Replace(w[1], "cm$", "");
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var cm) || double.IsNaN(cm)) return (false, null, null, 0, usage);
            if (Math.Abs(cm) > CalibMaxCm) return (false, null, null, 0, $"{Program.I(cm)} cm is implausible (max +-{CalibMaxCm:0} cm): give what you see, in centimetres");
            return (true, "set", w[0], cm, null);
        }
        return (false, null, null, 0, usage);
    }
    // pure (tested): the bias that makes the render's gap equal the gap David sees (cm: + floating, - sunk)
    internal static double CalibratedBias(double biasUsed, double renderGap, double realGapCm) => PoseBiasFromPhoto(biasUsed, renderGap, realGapCm / 100.0);
    // pure (tested): which key a target writes; null + reason when the posture does not match or a per-pose bias would shadow it
    internal static (string key, string err) CalibrationKey(string target, bool android, string kind, string pose, IReadOnlyDictionary<string, double> nums)
    {
        if (kind is not ("stand" or "sit")) return (null, $"I am not in a plain stand or AO ground sit ({kind}): nothing to calibrate");
        if (target == "here") return (PoseBiasPoseKey(android, pose ?? kind), null);
        if (target != kind) return (null, $"I am {(kind == "stand" ? "standing" : "ground sitting")} now: calibrate {kind} (or here), not {target}");
        if (nums != null && kind == "sit" && nums.ContainsKey(PoseBiasPoseKey(android, pose ?? "")) && PoseBiasPoseKey(android, pose ?? "") != PoseBiasKey(android, kind))
            return (null, $"this sit ({pose}) has its own bias, which would shadow the sit bias: use `hover calibrate here <cm>` (or `hover calibrate reset here` first)");
        return (PoseBiasKey(android, kind), null);
    }
    // pure (tested): the file content after setting (or, value null, removing) one key; every other key is kept
    internal static Dictionary<string, double> PoseBiasMerge(IReadOnlyDictionary<string, double> raw, string key, double? value)
    {
        var d = new Dictionary<string, double>(raw ?? new Dictionary<string, double>(), StringComparer.OrdinalIgnoreCase);
        if (value is double v) d[key] = Math.Round(v, 4); else d.Remove(key);
        return d;
    }
    void PoseBiasSave(string key, double? value)
    {
        Dictionary<string, double> raw;
        try { raw = File.Exists(PoseHoverFile) ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(PoseHoverFile)) ?? new() : new(); } catch { raw = new(); }
        File.WriteAllText(PoseHoverFile, JsonSerializer.Serialize(PoseBiasMerge(raw, key, value), new JsonSerializerOptions { WriteIndented = true }));
    }

    internal async Task<string> HoverCalibrate(string[] a)
    {
        var (ok, action, target, cm, err) = HoverCalibrateParse(a);
        if (!ok) return err;
        bool android = h.AndroidFormNow(); var nums = PoseNums();
        if (action == "show")
            return nums.Count == 0 ? "no calibrated biases (defaults: stand " + Program.I(PoseBiasDefault("stand")) + ", sit " + Program.I(PoseBiasDefault("sit")) + ")"
                : string.Join("; ", nums.OrderBy(k => k.Key).Select(k => $"{k.Key} {Program.I(k.Value)}")) + $"; defaults for the rest: stand {Program.I(PoseBiasDefault("stand"))}, sit {Program.I(PoseBiasDefault("sit"))}";
        if (!h.LoggedIn) return "not logged in";
        var (kind, pose) = PoseNow();
        if (action == "reset")
        {
            var rk = target == "here" ? PoseBiasPoseKey(android, pose ?? kind) : PoseBiasKey(android, target);
            if (!nums.ContainsKey(rk)) return $"{rk} was not set (default {Program.I(PoseBiasDefault(target == "here" ? kind : target))} applies)";
            PoseBiasSave(rk, null); h.Log("posehover", $"calibration: {rk} reset to the default"); return $"{rk} reset to the default {Program.I(PoseBiasDefault(target == "here" ? kind : target))}";
        }
        var (key, kerr) = CalibrationKey(target, android, kind, pose, nums);
        if (key == null) return kerr;
        var (go, why) = HoverCheckReady(h.Client.Self.SittingOn != 0 ? "seat" : kind, h.Client.Self.Velocity.Length(), h.IdleLeashedNow(), h.Following, h.IdleBusy, 999, 999, true);
        if (!go) return "cannot calibrate now: " + why;
        if (!await poseGate.WaitAsync(0)) return "a hover check or calibration is already running";
        try
        {
            var ex = await h.SceneExport(new[] { "export", "4", "noenv" });
            var scene = ex.Split('\n').Last();
            if (!scene.EndsWith("scene.json")) return "calibration export failed: " + ex;
            var dir = Path.GetDirectoryName(scene)!;
            if (!PoseStill(kind, pose)) return "calibration aborted: the pose changed while exporting";
            var (eff, src) = await h.EffectiveHoverFresh();
            double size = h.SelfAv()?.Scale.Z ?? 0; if (size < 1) return "agent size unknown";
            if (eff is not double hov) return "cannot calibrate: the hover in force is unknown (no AgentPreferences answer, no POST this session)";
            double biasUsed = PoseBiasFor(nums, android, kind, pose);
            var (g1, img1) = await HoverCheckRender(dir, size, hov, biasUsed, kind);
            if (g1 is not double gap) return $"calibration render gave no gap ({img1})";
            double nb = CalibratedBias(biasUsed, gap, cm);
            PoseBiasSave(key, nb);
            var (g2, img2) = await HoverCheckRender(dir, size, hov, nb, kind);
            bool good = g2 is double gg && Math.Abs(gg - cm / 100.0) <= 0.005;
            var msg = $"calibrated {key}: at hover {Program.I(hov)} ({src}) the render said {gap:+0.000;-0.000} m, you see {cm / 100.0:+0.000;-0.000;0.000} m -> bias {Program.I(biasUsed)} -> {Program.I(nb)}; re-render gap {(g2 is double x ? x.ToString("+0.000;-0.000") : "?")} ({(good ? "matches" : "does NOT match")}); {img2}";
            h.Log("posehover", msg); return msg;
        }
        finally { poseGate.Release(); }
    }
}
