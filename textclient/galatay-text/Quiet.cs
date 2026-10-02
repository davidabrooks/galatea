// Quiet mode / session detector (added 2026-09-25): Buddha Center rule "no nearby text or voice chat during meditation
// sessions and lectures". A group is IN SESSION in an area when >= 3 avatars (not Galatay; David counts) are seated there
// (on an object, or ground-sitting = SIT_GROUND / SIT_GROUND_CONSTRAINED in their animation list, parent 0).
// Areas (Naberrie only, ground level):
//   zendo    = InZendo(p): |x-80.5|+|y-142| <= 18.6 (footprint 16.55 + 2 m margin, same diamond the route bounds use), 40 <= z <= 80
//   deerpark = circle r 12 m around (139.0, 96.5), 32 <= z <= 62. The centre is the middle of the 33 meditation pillows
//              (x 133.5-142.6, y 93.6-100.4, z ~42.5; all within 6 m of it); the path end node 'deerpark' (145.7,93.7) is 7.3 m away.
// Evaluated every 10 s. Hysteresis: enter IMMEDIATELY at >= 3 seated; leave only after < 3 for 120 s continuously.
// Quiet mode = any area in session. While quiet: no auto-greetings anywhere; wander keeps >= 15 m from an in-session area
// (Deer Park keep-out = centre radius 27 m, zendo keep-out = L1 <= 39.8 i.e. >= 15 m Euclidean from the diamond);
// deerpark legs are cut short at the keep-out and turn around; the zendo spur is skipped; no sit spots in a keep-out;
// the chat approach is suppressed (logged) if the speaker is inside an in-session area or the approach spot is inside the
// Deer Park keep-out. Manual say/shout/whisper/chan 0 are refused while quiet (IM is fine) unless 'quiet override <min>'.
// Commands: quiet status | quiet selftest | quiet override <minutes 1-30> | quiet override off
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    const int QThreshold = 3;
    static readonly TimeSpan QLeaveAfter = TimeSpan.FromSeconds(120);
    const int QEvalSeconds = 10;
    static readonly Vector3 DeerParkCentre = new(139.0f, 96.5f, 42.5f);
    const float DeerParkRadius = 12f, QKeepOut = 15f;
    const float ZendoKeepOutL1 = 18.6f + 21.2f; // 15 m * sqrt(2) on the diagonal faces -> >= 15 m Euclidean from the diamond
    static readonly string QuietStateFile = Env("GT_QUIET_STATE", "/home/box/viewers/textclient/run/quiet-state.json");
    static readonly string[] QAreaNames = { "zendo", "deerpark" };

    sealed class QArea
    {
        public string Name; public bool In; public DateTime Since; public DateTime? BelowSince; public int Count; public List<string> Names = new();
        public List<Vector3> Seated = new(); public int PeakCount; public DateTime LastEval;
    }
    sealed record QAv(UUID id, string name, Vector3 pos, bool seated, bool posKnown);

    static readonly Dictionary<string, QArea> qAreas = QAreaNames.ToDictionary(n => n, n => new QArea { Name = n });
    static readonly ConcurrentDictionary<UUID, bool> qGroundSit = new();
    static DateTime qOverrideUntil = DateTime.MinValue;
    static int qUnknownSeated; static DateTime qLastEval = DateTime.MinValue;
    static readonly Dictionary<UUID, DateTime> qSuppressedLogged = new();
    static bool QuietOn { get { lock (qAreas) return qAreas.Values.Any(a => a.In); } }
    static bool QIn(string area) { lock (qAreas) return qAreas.TryGetValue(area, out var a) && a.In; }
    static void QLog(string m) => Log("quiet", m);

    // ---- pure helpers (unit-tested) --------------------------------------------------------------------
    static string QAreaOf(Vector3 p)
    {
        if (InZendo(p) && p.Z >= 40f && p.Z <= 80f) return "zendo";
        if (HDist(p, DeerParkCentre) <= DeerParkRadius && p.Z >= 32f && p.Z <= 62f) return "deerpark";
        return null;
    }
    static bool QInKeepOut(string area, Vector3 p) => area switch
    {
        "deerpark" => HDist(p, DeerParkCentre) <= DeerParkRadius + QKeepOut && p.Z < 80f,
        "zendo" => ZendoDiamond(p) <= ZendoKeepOutL1 && p.Z < 90f,
        _ => false,
    };
    // seated counts per area; self excluded
    static Dictionary<string, (int n, List<string> names, List<Vector3> pos)> QCount(IEnumerable<QAv> avs, UUID self, out int unknownSeated)
    {
        var r = QAreaNames.ToDictionary(n => n, n => (0, new List<string>(), new List<Vector3>()));
        unknownSeated = 0;
        foreach (var a in avs)
        {
            if (a.id == self || !a.seated) continue;
            if (!a.posKnown) { unknownSeated++; continue; }
            var ar = QAreaOf(a.pos); if (ar == null) continue;
            var t = r[ar]; t.Item1++; t.Item2.Add(a.name); t.Item3.Add(a.pos); r[ar] = t;
        }
        return r;
    }
    // hysteresis step: returns "enter", "leave" or null
    static string QStep(QArea a, int count, DateTime now)
    {
        a.Count = count; a.LastEval = now;
        if (count >= QThreshold)
        {
            a.BelowSince = null;
            if (!a.In) { a.In = true; a.Since = now; a.PeakCount = count; return "enter"; }
            a.PeakCount = Math.Max(a.PeakCount, count);
            return null;
        }
        if (!a.In) { a.BelowSince = null; return null; }
        a.BelowSince ??= now;
        if (now - a.BelowSince.Value >= QLeaveAfter) { a.In = false; a.Since = now; a.BelowSince = null; return "leave"; }
        return null;
    }
    // first in-session keep-out the point lies in, or null. 'inSession' / 'seatedIn' are injected for tests.
    static string QBlocked(Vector3 p, Func<string, bool> inSession, Func<string, List<Vector3>> seatedIn, bool deerOnly)
    {
        if (inSession("deerpark") && (QInKeepOut("deerpark", p) || seatedIn("deerpark").Any(s => HDist(s, p) < QKeepOut))) return "deerpark";
        if (!deerOnly && inSession("zendo") && QInKeepOut("zendo", p)) return "zendo";
        return null;
    }
    // cut a deerpark leg so it ends before the Deer Park keep-out; returns (poly or null if nothing to walk, cut?, where)
    static (Poly poly, bool cut, float at) QTruncate(Poly poly, Func<Vector3, bool> blocked)
    {
        for (float s = 0; s <= poly.Len; s += 0.5f)
        {
            if (!blocked(poly.At(s))) continue;
            float end = Math.Max(0, s - 0.5f);
            if (end < 3f) return (null, true, end);
            var tp = new List<Vector3>();
            for (int i = 0; i < poly.P.Count && poly.C[i] < end; i++) tp.Add(poly.P[i]);
            tp.Add(poly.At(end));
            return (new Poly(tp), true, end);
        }
        return (poly, false, poly.Len);
    }

    // ---- live wiring --------------------------------------------------------------------------------------
    static void HookQuiet()
    {
        qGroundSit.Clear();
        client.Avatars.AvatarAnimation += (s, e) =>
        {
            try
            {
                if (e.AvatarID == client.Self.AgentID) return;
                bool gs = e.Animations.Any(x => x.AnimationID == Animations.SIT_GROUND || x.AnimationID == Animations.SIT_GROUND_staticRAINED);
                qGroundSit[e.AvatarID] = gs;
            }
            catch { }
        };
    }
    static List<QAv> QLiveAvatars()
    {
        var sim = client.Network.CurrentSim; var res = new List<QAv>();
        if (sim == null) return res;
        foreach (var a in sim.ObjectsAvatars.Values)
        {
            if (a == null || a.ID == client.Self.AgentID) continue;
            bool onObj = a.ParentID != 0;
            bool known = !onObj || sim.ObjectsPrimitives.ContainsKey(a.ParentID);
            bool ground = !onObj && qGroundSit.TryGetValue(a.ID, out var g) && g;
            var pos = known ? PositionHelper.GetAvatarPosition(sim, a) : Vector3.Zero;
            res.Add(new QAv(a.ID, a.Name + (ground ? " (ground-sit)" : ""), pos, onObj || ground, known));
        }
        return res;
    }
    static void QuietEval()
    {
        if (!LoggedIn || !InNaberrie) return;
        var now = DateTime.Now;
        var counts = QCount(QLiveAvatars(), client.Self.AgentID, out var unk);
        var transitions = new List<(string area, string tr, int n, List<string> names)>();
        bool wasOn;
        lock (qAreas)
        {
            wasOn = qAreas.Values.Any(a => a.In);
            qUnknownSeated = unk; qLastEval = now;
            foreach (var (name, c) in counts)
            {
                var a = qAreas[name];
                a.Names = c.names; a.Seated = c.pos;
                var tr = QStep(a, c.n, now);
                if (tr != null) transitions.Add((name, tr, c.n, c.names));
            }
        }
        foreach (var (area, tr, n, names) in transitions)
        {
            if (tr == "enter") QLog($"SESSION START {area}: {n} seated ({string.Join(", ", names)}) -> quiet mode ON (greetings off, wander keeps >= 15 m away)");
            else QLog($"SESSION END {area}: below {QThreshold} seated for {QLeaveAfter.TotalSeconds:F0} s (now {n}{(n > 0 ? ": " + string.Join(", ", names) : "")})");
            if (tr == "enter") QReplanIfNeeded(area);
        }
        bool isOn = QuietOn;
        if (isOn != wasOn) QLog(isOn ? $"QUIET MODE ON ({QWhy()})" : "QUIET MODE OFF (no area in session); greetings allowed again");
        if (!isOn) lock (qSuppressedLogged) qSuppressedLogged.Clear();
        QSaveState();
    }
    static async Task QuietLoop()
    {
        QLog($"session detector running (every {QEvalSeconds} s; threshold {QThreshold} seated; leave after {QLeaveAfter.TotalSeconds:F0} s below)");
        while (!cts.IsCancellationRequested)
        {
            try { QuietEval(); } catch (Exception ex) { QLog("eval error: " + ex.GetBaseException().Message); }
            try { await Task.Delay(QEvalSeconds * 1000, cts.Token); } catch (OperationCanceledException) { break; }
        }
    }
    // a session just started: if the current wander leg/seat walk heads into its keep-out, cancel the leg so the loop replans
    static volatile bool wLegHasGoal; static Vector3 wLegGoal;
    static void QReplanIfNeeded(string area)
    {
        if (!WanderOn || wanderPause != null || !wLegHasGoal) return;
        bool hit = area == "zendo" ? (wTarget == "zendo" && wanderPhase.Contains("zendo")) || QInKeepOut("zendo", wLegGoal) && wanderPhase.StartsWith("walking to seat")
                                   : QInKeepOut("deerpark", wLegGoal);
        if (!hit) return;
        WLog($"QUIET: {area} went into session while {wanderPhase}: cancelling this leg to replan (keep >= 15 m away)");
        legCts?.Cancel(); try { client.Self.AutoPilotCancel(); } catch { }
    }
    static string QWhy()
    {
        lock (qAreas)
        {
            var on = qAreas.Values.Where(a => a.In).Select(a => $"{a.Name} in session: {a.Count} seated now{(a.BelowSince != null ? $", below threshold {(DateTime.Now - a.BelowSince.Value).TotalSeconds:F0}/{QLeaveAfter.TotalSeconds:F0} s" : "")}").ToList();
            return on.Count == 0 ? "no area has >= 3 seated" : string.Join("; ", on);
        }
    }
    static string QuietFlag()
    {
        lock (qAreas)
        {
            var on = qAreas.Values.Where(a => a.In).Select(a => $"{a.Name}:{a.Count}").ToList();
            return on.Count == 0 ? "off" : "ON(" + string.Join(",", on) + ")";
        }
    }
    static string QuietShort() => QuietOn ? $"quiet ON ({QWhy()})" : "quiet off";
    static void QSaveState()
    {
        try
        {
            var o = new JsonObject { ["quiet"] = QuietOn, ["updated"] = DateTime.Now.ToString("o", CultureInfo.InvariantCulture), ["why"] = QWhy() };
            lock (qAreas)
                foreach (var a in qAreas.Values)
                    o[a.Name] = new JsonObject { ["in_session"] = a.In, ["seated"] = a.Count, ["names"] = new JsonArray(a.Names.Select(n => (JsonNode)n).ToArray()),
                        ["since"] = a.Since == default ? null : a.Since.ToString("o", CultureInfo.InvariantCulture),
                        ["below_threshold_s"] = a.BelowSince == null ? null : (int)(DateTime.Now - a.BelowSince.Value).TotalSeconds };
            var tmp = QuietStateFile + ".tmp"; File.WriteAllText(tmp, o.ToJsonString()); File.Move(tmp, QuietStateFile, true);
        }
        catch { }
    }
    // manual nearby chat guard (say/shout/whisper/chan 0)
    static string QuietChatGuard()
    {
        if (!QuietOn || DateTime.Now < qOverrideUntil) return null;
        return $"refused: quiet mode ON ({QWhy()}); Buddha Center rule: no nearby chat during sessions/lectures. Use IM, or 'quiet override <minutes>' if David really wants nearby chat.";
    }
    // greeting suppression with a log line once per avatar per quiet period
    static void QLogSuppressedGreet(GreetCand who)
    {
        lock (qSuppressedLogged)
        {
            if (qSuppressedLogged.ContainsKey(who.id)) return;
            qSuppressedLogged[who.id] = DateTime.Now;
        }
        WLog($"greeting SUPPRESSED (quiet mode: {QWhy()}): would have greeted {who.name} at {HDist(who.pos, client.Self.SimPosition):F1} m");
    }

    static string QuietStatus(bool fresh = true)
    {
        if (fresh) { try { QuietEval(); } catch { } }
        var sb = new System.Text.StringBuilder();
        var now = DateTime.Now;
        sb.AppendLine($"quiet mode: {(QuietOn ? "ON" : "off")} (flag quiet={QuietFlag()}); why: {QWhy()}");
        if (!InNaberrie) sb.AppendLine("  (not in Naberrie: detector idle)");
        lock (qAreas)
            foreach (var a in qAreas.Values)
            {
                sb.Append($"  {a.Name}: {(a.In ? "IN SESSION" : "no session")}, seated now {a.Count}/{QThreshold}");
                if (a.Names.Count > 0) sb.Append(" [" + string.Join(", ", a.Names) + "]");
                if (a.In) sb.Append($"; since {a.Since:HH:mm:ss} (peak {a.PeakCount})");
                else if (a.Since != default) sb.Append($"; last session ended {a.Since:HH:mm:ss}");
                if (a.BelowSince != null) sb.Append($"; below threshold for {(now - a.BelowSince.Value).TotalSeconds:F0}/{QLeaveAfter.TotalSeconds:F0} s");
                sb.AppendLine();
            }
        sb.AppendLine($"  seated avatars with unknown position (seat not loaded): {qUnknownSeated}; last eval {(qLastEval == DateTime.MinValue ? "-" : qLastEval.ToString("HH:mm:ss"))}");
        sb.AppendLine($"  areas: zendo = |x-80.5|+|y-142| <= 18.6, z 40-80; deerpark = r {DeerParkRadius} m around {P3(DeerParkCentre)}, z 32-62; threshold {QThreshold} seated (Galatay excluded, David counts), enter at once, leave after {QLeaveAfter.TotalSeconds:F0} s below");
        sb.Append($"  while ON: no greetings; wander >= {QKeepOut} m from in-session areas (deerpark leg turns around at r {DeerParkRadius + QKeepOut} m; zendo spur skipped); no seats in keep-outs; manual nearby chat refused{(now < qOverrideUntil ? $" (OVERRIDE active until {qOverrideUntil:HH:mm:ss})" : "")}");
        return sb.ToString();
    }

    // ---- selftest -----------------------------------------------------------------------------------------
    static string QuietSelfTest()
    {
        var lines = new List<string>(); int pass = 0, fail = 0;
        void Ok(string name, bool ok, string detail = "") { if (ok) pass++; else fail++; lines.Add($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? ": " + detail : "")}"); }
        var self = UUID.Random(); var david = UUID.Random();
        QAv S(string n, float x, float y, float z, bool seated = true, UUID? id = null, bool known = true) => new(id ?? UUID.Random(), n, new Vector3(x, y, z), seated, known);
        int C(List<QAv> avs, string area) => QCount(avs, self, out _)[area].n;
        // detection
        Ok("2 seated at Deer Park -> 2 (no session)", C(new() { S("A", 138, 96, 42.5f), S("B", 140, 97, 42.5f) }, "deerpark") == 2);
        Ok("3 seated at Deer Park incl. David -> 3", C(new() { S("A", 138, 96, 42.5f), S("B", 140, 97, 42.5f), S("David Nightingale", 136, 95, 42.5f, id: david) }, "deerpark") == 3);
        Ok("Galatay herself not counted", C(new() { S("A", 138, 96, 42.5f), S("B", 140, 97, 42.5f), S("Galatay", 136, 95, 42.5f, id: self) }, "deerpark") == 2);
        Ok("standing avatars not counted", C(new() { S("A", 138, 96, 42.5f, false), S("B", 140, 97, 42.5f, false), S("C", 136, 95, 42.5f, false) }, "deerpark") == 0);
        Ok("ground-sit counts (seated flag from SIT_GROUND)", C(new() { S("A", 138, 96, 42.5f), S("B", 140, 97, 42.5f), S("C (ground-sit)", 141, 99, 42.5f) }, "deerpark") == 3);
        Ok("seated 13 m from the Deer Park centre -> outside", C(new() { S("A", 152, 96.5f, 43) }, "deerpark") == 0);
        Ok("all 33 pillow positions inside r 12", new[] { (142.6f, 94.2f), (133.5f, 98.5f), (134.4f, 99.5f), (139.9f, 100.4f), (141.2f, 99.9f), (137.4f, 94.1f) }.All(p => QAreaOf(new Vector3(p.Item1, p.Item2, 42.5f)) == "deerpark"));
        Ok("3 seated in the zendo -> zendo 3", C(new() { S("A", 80, 140, 53), S("B", 85, 145, 53), S("C", 75, 142, 53) }, "zendo") == 3);
        Ok("same spots in a skybox z 253 -> 0", C(new() { S("A", 80, 140, 253), S("B", 85, 145, 253), S("C", 75, 142, 253) }, "zendo") == 0);
        var split = QCount(new List<QAv> { S("A", 80, 140, 53), S("B", 85, 145, 53), S("C", 138, 96, 42.5f) }, self, out _);
        Ok("2 zendo + 1 Deer Park -> neither reaches 3", split["zendo"].n == 2 && split["deerpark"].n == 1);
        QCount(new List<QAv> { S("A", 0, 0, 0, known: false) }, self, out var unk);
        Ok("seated on an unloaded seat -> 'unknown', not counted", unk == 1);
        Ok("landing / pool rock are in no area", QAreaOf(new Vector3(107.2f, 149.9f, 52.2f)) == null && QAreaOf(new Vector3(133, 141.8f, 47.2f)) == null);
        // hysteresis
        var a = new QArea { Name = "deerpark" }; var t0 = new DateTime(2026, 9, 25, 20, 0, 0);
        var seq = new (int sec, int n, bool expectIn, string expectTr)[] {
            (0, 2, false, null), (10, 3, true, "enter"), (20, 2, true, null), (60, 2, true, null), (70, 3, true, null), (80, 1, true, null),
            (190, 0, true, null), (199, 2, true, null), (200, 1, false, "leave"), (210, 2, false, null), (220, 4, true, "enter") };
        foreach (var (sec, n, expIn, expTr) in seq)
        {
            var tr = QStep(a, n, t0.AddSeconds(sec));
            Ok($"hysteresis t={sec,3}s seated {n} -> {(expIn ? "IN" : "out")}{(expTr != null ? " (" + expTr + ")" : "")}", a.In == expIn && tr == expTr, $"got {(a.In ? "IN" : "out")} {tr ?? "-"}");
        }
        // keep-outs & seats
        bool deerOn(string x) => x == "deerpark"; bool zenOn(string x) => x == "zendo"; bool none(string x) => false;
        List<Vector3> noSeated(string x) => new();
        Ok("Deer Park pillow blocked while Deer Park in session", QBlocked(new Vector3(138, 96, 42.5f), deerOn, noSeated, false) == "deerpark");
        Ok("Deer Park pillow fine with no session", QBlocked(new Vector3(138, 96, 42.5f), none, noSeated, false) == null);
        Ok("landing chair blocked while the zendo is in session (L1 32 < 39.8)", QBlocked(new Vector3(104.5f, 150.3f, 52.7f), zenOn, noSeated, false) == "zendo");
        Ok("landing chair fine when only Deer Park is in session", QBlocked(new Vector3(104.5f, 150.3f, 52.7f), deerOn, noSeated, false) == null);
        Ok("pool rock pillow fine for either session", QBlocked(new Vector3(133, 141.8f, 47.2f), x => true, noSeated, false) == null);
        Ok("outside r 27 but < 15 m from a seated session member -> blocked", QBlocked(new Vector3(165, 110, 45), deerOn, x => new() { new Vector3(152, 104, 44) }, true) == "deerpark" && QBlocked(new Vector3(165, 110, 45), deerOn, noSeated, true) == null);
        // greeting
        var me = new Vector3(120, 120, 22);
        var (gw, gwhy) = PickGreet(new() { new(UUID.Random(), "Anna Walker", new Vector3(125, 120, 22), false) }, me, t0, new(), t0.AddMinutes(-5), x => false, self, "deerpark in session");
        Ok("no greeting while quiet mode is on", gw == null && gwhy.StartsWith("quiet"), gwhy);
        var (gw2, _) = PickGreet(new() { new(UUID.Random(), "Anna Walker", new Vector3(125, 120, 22), false) }, me, t0, new(), t0.AddMinutes(-5), x => false, self, null);
        Ok("greeting allowed again when quiet is off", gw2 != null);
        // deerpark leg truncation on the real graph
        var g = LoadGraph(HomeSeatRegion);
        if (g == null) Ok("graph loaded", false);
        else
        {
            var (pts, err) = GraphRoute(g, g.N[g.Places["landing"].node], g.Places["deerpark"].node);
            if (err != null) Ok("route landing->deerpark", false, err);
            else
            {
                var poly = new Poly(pts);
                var seated = new List<Vector3> { new(138, 96, 42.5f), new(140, 98, 42.5f), new(141, 94, 42.5f) };
                var (cutp, cut, at) = QTruncate(poly, p => QBlocked(p, deerOn, x => seated, true) != null);
                var end = cutp?.P[^1] ?? poly.At(0);
                float dC = HDist(end, DeerParkCentre), dS = seated.Min(s => HDist(s, end));
                bool neverInside = cutp != null && Enumerable.Range(0, (int)(cutp.Len * 2) + 1).All(i => HDist(cutp.At(i * 0.5f), DeerParkCentre) > DeerParkRadius + QKeepOut - 0.01f);
                Ok("landing->deerpark leg is cut short of the keep-out", cut && cutp != null && dC >= DeerParkRadius + QKeepOut - 0.6f && dS >= QKeepOut && neverInside,
                    $"full {poly.Len:F0} m -> {at:F0} m, turns at {P3(end)}, {dC:F1} m from the centre, {dS:F1} m from the nearest seated");
                var (p2, cut2, _) = QTruncate(poly, p => QBlocked(p, none, noSeated, true) != null);
                Ok("no cut without a session", !cut2 && p2 == poly);
                var from = new Poly(new List<Vector3> { new(145, 95, 43.3f), new(150.7f, 98.6f, 43.4f) });
                var (p3, cut3, _) = QTruncate(from, p => QBlocked(p, deerOn, noSeated, true) != null);
                Ok("already inside the keep-out -> nothing to walk (turn around)", cut3 && p3 == null);
            }
        }
        // spur + approach
        Ok("zendo spur skipped while the zendo is in session", QSkipSpur(zenOn) && !QSkipSpur(deerOn));
        Ok("approach suppressed: speaker inside in-session Deer Park", QApproachBlock(new Vector3(139, 97, 42.5f), new Vector3(141, 98, 42.5f), deerOn) != null);
        Ok("approach allowed: speaker at the landing, Deer Park in session", QApproachBlock(new Vector3(107, 150, 52.5f), new Vector3(108, 152, 52.5f), deerOn) == null);
        Ok("approach suppressed: speaker inside the zendo, zendo in session", QApproachBlock(new Vector3(85, 142, 53), new Vector3(100, 143, 52.5f), zenOn) != null);
        Ok("approach suppressed: spot inside the Deer Park keep-out", QApproachBlock(new Vector3(155, 110, 45), new Vector3(154, 108, 45), deerOn) != null);
        return $"quiet selftest: {pass} pass, {fail} fail (synthetic avatars; nothing sent)\n" + string.Join("\n", lines);
    }
    static bool QSkipSpur(Func<string, bool> inSession) => inSession("zendo");
    static List<Vector3> QSeatedIn(string area) { lock (qAreas) return qAreas.TryGetValue(area, out var a) && a.In ? a.Seated.ToList() : new List<Vector3>(); }
    // reason to NOT walk to a chatting speaker, or null
    static string QApproachBlock(Vector3 speaker, Vector3 goal, Func<string, bool> inSession)
    {
        var ar = QAreaOf(speaker);
        if (ar != null && inSession(ar)) return $"speaker is inside the in-session {ar} area";
        if (inSession("deerpark") && QInKeepOut("deerpark", goal)) return "the approach spot is within 15 m of the in-session Deer Park";
        return null;
    }

    static string QuietCmds(string[] a)
    {
        var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
        switch (sub)
        {
            case "status": return QuietStatus();
            case "selftest": return QuietSelfTest();
            case "override":
                if (a.Length > 1 && a[1] == "off") { qOverrideUntil = DateTime.MinValue; QLog("manual chat override cleared"); return "override off"; }
                if (a.Length < 2 || !int.TryParse(a[1], out var m) || m < 1 || m > 30) return "usage: quiet override <minutes 1-30> | quiet override off";
                qOverrideUntil = DateTime.Now.AddMinutes(m); QLog($"manual nearby-chat override until {qOverrideUntil:HH:mm:ss} (greetings stay off)");
                return $"manual say/shout/whisper allowed until {qOverrideUntil:HH:mm:ss} (auto-greetings stay suppressed while quiet)";
            default: return "usage: quiet status | quiet selftest | quiet override <minutes 1-30>|off";
        }
    }
}
