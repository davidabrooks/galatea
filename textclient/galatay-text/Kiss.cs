// Kiss.cs (2026-10-11, David: "kiss me" should be instant). 'kiss [name]' (default David): face him and touch the
// lips prim of his worn -Pretty Liars- Real Kisser, exactly what worked by hand at 17:10 (touching his kisser's lips
// prim; the root and the tiny 0.01 m prims do nothing). His prims arrive unnamed ("Object"), so the kisser is found
// by its shape: a 7-prim attachment linkset with a 0.023 m cube (the 'Pretty Liars' particle generator), a thin
// forehead prim (~0.010 x 0.059 x 0.035) and the lips prim (~0.023 x 0.035 x 0.026). Prim ids change with every
// relog/re-wear, so the lookup runs fresh on each call (~ms). No HUD needed.
// Wander (routes/_wander-rules.json "kiss": {chance, cooldownMin, maxM}): after a home leg, if David is standing
// within maxM on her level, with probability chance and at most once per cooldownMin, walk to ~1 m, face, kiss.
using System.Text.Json.Nodes;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal sealed record KissPrim(uint Local, uint Parent, Vector3 Scale);

    static bool Near(float a, float b, float tol) => Math.Abs(a - b) <= tol;
    static bool ScaleIs(Vector3 s, float x, float y, float z, float tol = 0.006f)
    {   // axis order may differ between kisser builds: compare sorted
        var a = new[] { s.X, s.Y, s.Z }.OrderBy(v => v).ToArray(); var b = new[] { x, y, z }.OrderBy(v => v).ToArray();
        return Near(a[0], b[0], tol) && Near(a[1], b[1], tol) && Near(a[2], b[2], tol);
    }

    // pure (tests): from one avatar's attachment roots + children, the local id of the kisser part to touch (0 = none).
    // part: "lips" | "cheek"
    internal static uint FindKisserPart(IReadOnlyList<KissPrim> roots, IReadOnlyList<KissPrim> children, string part = "lips")
    {
        foreach (var r in roots)
        {
            var kids = children.Where(c => c.Parent == r.Local).ToList();
            if (kids.Count < 4 || kids.Count > 8) continue;
            bool gen = kids.Any(k => ScaleIs(k.Scale, 0.0233f, 0.0233f, 0.0233f, 0.004f));
            bool forehead = kids.Any(k => ScaleIs(k.Scale, 0.0103f, 0.0598f, 0.0341f));
            if (!gen || !forehead) continue;
            if (part == "cheek")
            {
                var ch = kids.Where(k => k.Scale.X > 0.05f && k.Scale.Y > 0.05f && k.Scale.Z > 0.05f && k.Scale.X < 0.1f).OrderBy(k => k.Local).FirstOrDefault();
                if (ch != null) return ch.Local;
                continue;
            }
            var lips = kids.FirstOrDefault(k => ScaleIs(k.Scale, 0.0233f, 0.0351f, 0.0260f) && !ScaleIs(k.Scale, 0.0233f, 0.0233f, 0.0233f, 0.004f));
            if (lips != null) return lips.Local;
        }
        return 0;
    }

    static DateTime kissLastAt = DateTime.MinValue; static string kissLast = "-";

    // 'kiss [name] [lips|cheek]' -> one touch, ~instant
    static string KissCmd(string rest)
    {
        var a = (rest ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        string part = "lips";
        if (a.Count > 0 && (a[^1].Equals("cheek", StringComparison.OrdinalIgnoreCase) || a[^1].Equals("cheeks", StringComparison.OrdinalIgnoreCase) || a[^1].Equals("lips", StringComparison.OrdinalIgnoreCase)))
        { part = a[^1].StartsWith("cheek", StringComparison.OrdinalIgnoreCase) ? "cheek" : "lips"; a.RemoveAt(a.Count - 1); }
        var name = a.Count == 0 ? "David Nightingale" : string.Join(' ', a);
        if (name.Equals("status", StringComparison.OrdinalIgnoreCase)) return "kiss: last " + kissLast;
        var sim = Sim;
        var av = Avatars().FirstOrDefault(t => t.av.ID == DavidId && (name.Equals("david", StringComparison.OrdinalIgnoreCase) || name.Equals("me", StringComparison.OrdinalIgnoreCase))
            || t.av.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || t.av.Name.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase));
        if (av.av == null) return $"'{name}' is not in view";
        if (av.dist > 5.5f) return $"{av.av.Name} is {av.dist:F1} m away (kiss range 5 m)";
        var all = sim.ObjectsPrimitives.Values.Where(p => p != null).ToList();
        var roots = all.Where(p => p.ParentID == av.av.LocalID).Select(p => new KissPrim(p.LocalID, p.ParentID, p.Scale)).ToList();
        var rootIds = roots.Select(r => r.Local).ToHashSet();
        var kids = all.Where(p => rootIds.Contains(p.ParentID)).Select(p => new KissPrim(p.LocalID, p.ParentID, p.Scale)).ToList();
        var local = FindKisserPart(roots, kids, part);
        if (local == 0) return $"no Real Kisser found on {av.av.Name} ({roots.Count} attachments seen)";
        if (client.Self.SittingOn == 0) try { client.Self.Movement.TurnToward(av.pos); } catch { }
        client.Self.Touch(local);
        kissLastAt = DateTime.Now; kissLast = $"{DateTime.Now:HH:mm:ss} {av.av.Name} {part} (local {local})";
        Log("kiss", $"touched {av.av.Name}'s kisser {part} prim (local {local}, {av.dist:F1} m)");
        return $"kissed {av.av.Name} ({part}; his kisser announces it in nearby chat)";
    }

    // ---- wander kiss ----
    internal sealed record WanderKissRule(double Chance, double CooldownMin, float MaxM);
    internal static WanderKissRule ParseWanderKiss(string json)
    {
        var def = new WanderKissRule(0.25, 20, 5f);
        try
        {
            var o = JsonNode.Parse(json ?? "", documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
            if (o?["kiss"] is not JsonObject k) return def;
            double ch = k["chance"] is JsonNode c ? c.GetValue<double>() : def.Chance;
            double cd = k["cooldownMin"] is JsonNode d ? d.GetValue<double>() : def.CooldownMin;
            float m = k["maxM"] is JsonNode mm ? (float)mm.GetValue<double>() : def.MaxM;
            return new WanderKissRule(Math.Clamp(ch, 0, 1), Math.Max(20, cd), Math.Clamp(m, 1f, 5f));
        }
        catch { return def; }
    }

    // pure: should this pass try a kiss?
    internal static bool WanderKissDue(WanderKissRule r, float davidDist, float dz, bool davidSeated, DateTime now, DateTime last, double roll)
        => davidDist >= 0 && davidDist <= r.MaxM && Math.Abs(dz) <= 1.5f && !davidSeated
           && (now - last).TotalMinutes >= r.CooldownMin && roll < r.Chance;

    static async Task WanderKissMaybe(CancellationToken ct)
    {
        try
        {
            if (client.Self.SittingOn != 0 || wanderPause != null) return;
            var rule = ParseWanderKiss(File.Exists(WanderRulesFile) ? File.ReadAllText(WanderRulesFile) : null);
            var d = Avatars().FirstOrDefault(t => t.av.ID == DavidId);
            if (d.av == null) return;
            var me = client.Self.SimPosition;
            if (!WanderKissDue(rule, d.dist, d.pos.Z - me.Z, d.av.ParentID != 0, DateTime.Now, kissLastAt, wRnd.NextDouble())) return;
            WLog($"KISS: David standing {d.dist:F1} m away: walking over to kiss him");
            if (d.dist > 1.3f)
            {
                var dir = new Vector3(me.X - d.pos.X, me.Y - d.pos.Y, 0); if (dir.Length() < 0.01f) dir = new Vector3(1, 0, 0);
                dir = Vector3.Normalize(dir);
                var spot = new Vector3(d.pos.X + dir.X * 1.0f, d.pos.Y + dir.Y * 1.0f, me.Z);
                await WalkPath(new List<Vector3> { spot }, ct, lastTol: 0.5f);
            }
            if (wanderPause != null) return;
            WLog("KISS: " + KissCmd("David"));
            await Task.Delay(4000, ct);   // linger a moment for the kiss animation
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { WLog("KISS failed: " + ex.GetBaseException().Message); }
    }
}
