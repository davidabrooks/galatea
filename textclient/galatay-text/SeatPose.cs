// Seat poses (added 2026-09-25 23:30, David's request): after a wander sit, pick a random pose from the SEAT'S OWN pose menu
// (AVsitter dialog sent on sit, or opened by touching the seat). No daemon-played animations: we only press dialog buttons.
// Skipped buttons: anything in [brackets] (AVsitter controls: [ADJUST] [BACK] [SYNC] [SWAP] [STOP] ...), adjust/position/sync/unsit/stand/next/prev/options/help/reset.
// Occupancy / couples (David, 2026-10-05 evening): NEVER auto-pick couples. Couples menus only when David explicitly asks
// ('pose couples'). When the seat is shared he controls poses; recovery / wander / plain 'pose' stay solo or restore the
// last known menu path. Shared mid-sit must not switch us into Couples PG / Cuddles / etc. (bug 2026-10-05 11:53 PT).
// PREFERENCE (David, 2026-09-25): skip MALE poses: any button/submenu labelled male, men, man, guy, boy, or the token M (M, M1, M 2, (M))
// is never chosen; female/F/woman sections and neutral poses are fine.
// Submenus (AVsitter labels ending in '*') are entered at random (max depth 3). The current pose (last [..] in the menu text) is avoided.
// Sits >= 180 s (was 90 s before the sit lengths were doubled on 2026-09-26) change to another random pose once, 40-60% of the way through (touch the seat -> menu).
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    // Always skip: empty, [bracket controls], punctuation-only, and furniture chrome (not pose names).
    static readonly Regex PoseControlOnlySkip = new(@"^\s*$|^\s*\[.*\]\s*$|^[<>\-\s.]+$|\b(adjust\w*|position\w*|sync\w*|unsit|stand\s*up|stand|swap|back|next|prev\w*|more|page|options?|menu|help|reset|stop|helper|settings?|security|off|on)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Couples submenu / pose names (also used to skip when alone / on auto paths).
    static readonly Regex PoseCouplesName = new(@"\b(couples?|cuddl\w*|kiss\w*|hugs?|hugging|spoon\w*|snuggl\w*|romanc\w*|lap|together|partners?|duo|pair|2p)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Solo submenu names (Trompe Loeil Reiley etc. use SINGLE*).
    static readonly Regex PoseSoloMenuName = new(@"\b(singles?|solo|alone|one\s*p|1p)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // David 2026-09-25: never pick male poses
    static readonly Regex PoseMaleSkip = new(@"\b(male|males|men|man|guy|guys|boy|boys|him|his|masc\w*)\b|(^|[\s(\[_/-])m(\d+|(?=[\s)\]_*/-])|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly ConcurrentQueue<(DateTime at, ScriptDialogEventArgs e)> wDialogs = new();
    static string wLastPose = "-";
    static List<string> wLastPosePath = new(); // last AVsitter menu path we pressed/restored
    static bool? poseSeatShared; // last seen shared-seat state while seated
    static DateTime poseSharedChangedAt = DateTime.MinValue; // alone<->shared flip (recovery grace)
    // David 2026-10-05: after a partner sits/stands, AVsitter often restarts anims; wait before "pose lost" recovery.
    public static double PoseSharedFlipGraceS = 15;
    public static double PoseRecoveryMissingS = 8; // was 3; brief AVsitter swaps were false positives

    // pure: automatic paths never pick couples; only explicit 'pose couples' (David asked).
    public static bool AutoMayPickCouples(bool explicitCouplesRequest) => explicitCouplesRequest;

    // pure: still inside the post-occupancy-change grace window?
    public static bool InSharedFlipGrace(DateTime flipAt, DateTime now, double graceS) =>
        flipAt != DateTime.MinValue && (now - flipAt).TotalSeconds < graceS;

    static void WanderDialogIn(ScriptDialogEventArgs e)
    {
        wDialogs.Enqueue((DateTime.Now, e));
        while (wDialogs.Count > 30) wDialogs.TryDequeue(out _);
        if (client?.Self?.SittingOn != 0)
        {
            var cur = PoseCurrent(e.Message);
            if (!string.IsNullOrEmpty(cur) && wLastPosePath.Count > 0
                && !string.Equals(wLastPosePath[^1], cur, StringComparison.OrdinalIgnoreCase))
                wLastPosePath[^1] = cur;
        }
    }
    static string PoseCurrent(string msg)
    {
        var ms = Regex.Matches(msg ?? "", @"\[([^\[\]]+)\]");
        return ms.Count == 0 ? null : ms[^1].Groups[1].Value.Trim();
    }
    static string PoseBare(string raw) => (raw ?? "").Trim().TrimEnd('*').Trim();
    static bool PoseIsControl(string raw)
    {
        var l = raw ?? ""; var bare = PoseBare(l);
        return PoseControlOnlySkip.IsMatch(l) || PoseControlOnlySkip.IsMatch(bare);
    }
    static bool PoseIsCouplesNamed(string raw) => PoseCouplesName.IsMatch(PoseBare(raw));
    static bool PoseIsSoloMenu(string raw) => PoseSoloMenuName.IsMatch(PoseBare(raw));
    static bool PoseIsMale(string raw) => PoseMaleSkip.IsMatch(PoseBare(raw));

    // true when another avatar (not ME) is seated on this seat's root (same map objinfo uses as occupied=ME,<name>).
    static bool SeatHasOtherSitters(Primitive seat)
    {
        if (seat == null) return false;
        var sim = Sim; if (sim == null) return false;
        uint root = seat.LocalID;
        if (seat.ParentID != 0 && sim.ObjectsPrimitives.ContainsKey(seat.ParentID)) root = seat.ParentID;
        if (!Sitters(sim).TryGetValue(root, out var l) || l == null) return false;
        return l.Any(n => n != "ME");
    }

    // Track alone/shared flips. Do NOT hard-clear kept anim copies on a flip: that + AVsitter's brief restart
    // looked like "no seat pose for 3 s" and pose recovery then auto-picked Couples (2026-10-05 Lalou sofa).
    static void NotePoseSeatShared(bool? shared)
    {
        if (shared == null) { poseSeatShared = null; poseSharedChangedAt = DateTime.MinValue; return; }
        if (poseSeatShared != null && poseSeatShared != shared)
        {
            SoftForgetKeptPoseCopies();
            poseSharedChangedAt = DateTime.Now;
            lastSeatPoseMenuAt = DateTime.Now;
            seatPoseMissingSince = null;
            Log("height", $"pose: seat occupancy -> {(shared.Value ? "shared" : "alone")}; recovery grace {PoseSharedFlipGraceS:g} s (AVsitter may restart anims)");
        }
        poseSeatShared = shared;
    }

    // pure: which button to press (null = nothing suitable). submenu = AVsitter '*' label.
    // couplesMode: ONLY for explicit 'pose couples'. !couplesMode (default/recovery/wander): skip couples even if shared.
    static (string pick, bool submenu, string why) PickPoseButton(List<string> labels, string current, Random rnd, bool couplesMode = false, bool inCouplesMenu = false)
    {
        var ok = new List<string>(); var skipped = new List<string>();
        foreach (var raw in labels ?? new())
        {
            var l = raw ?? "";
            if (PoseIsControl(l) || PoseIsMale(l)) { skipped.Add(l); continue; }
            if (couplesMode)
            {
                if (!inCouplesMenu)
                {
                    if (PoseIsSoloMenu(l) || !PoseIsCouplesNamed(l)) { skipped.Add(l); continue; }
                }
            }
            else
            {
                if (PoseIsCouplesNamed(l)) { skipped.Add(l); continue; }
            }
            ok.Add(l);
        }
        if (ok.Count == 0)
        {
            var mode = couplesMode ? (inCouplesMenu ? "couples submenu" : "couples (explicit)") : "solo";
            return (null, false, $"no suitable {mode} option (skipped: {string.Join(", ", skipped)})");
        }
        var notCur = ok.Where(l => current == null || !string.Equals(PoseBare(l), current, StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = notCur.Count > 0 ? notCur : ok;
        var p = pool[rnd.Next(pool.Count)];
        return (p, p.TrimEnd().EndsWith("*"), $"{pool.Count} choices, skipped {skipped.Count}{(couplesMode ? ", couples" : ", solo")}");
    }
    static bool DialogFromSeat(ScriptDialogEventArgs e, Primitive seat, Simulator sim)
    {
        if (e.ObjectID == seat.ID) return true;
        var p = sim.ObjectsPrimitives.Values.FirstOrDefault(x => x != null && x.ID == e.ObjectID);
        if (p != null && (p.ParentID == seat.LocalID || (seat.ParentID != 0 && p.LocalID == seat.ParentID))) return true;
        return seat.Properties?.Name != null && e.ObjectName == seat.Properties.Name;
    }
    static async Task<ScriptDialogEventArgs> WaitSeatDialog(Primitive seat, DateTime since, int ms, CancellationToken ct)
    {
        var sim = Sim; var until = DateTime.Now.AddMilliseconds(ms);
        while (true)
        {
            var hit = wDialogs.ToArray().Where(d => d.at >= since && DialogFromSeat(d.e, seat, sim)).OrderByDescending(d => d.at).FirstOrDefault();
            if (hit.e != null) return hit.e;
            if (DateTime.Now >= until) return null;
            await Task.Delay(200, ct);
        }
    }
    static void ClearKeptPoseCopies()
    {   // hard stop: intentional pose change so the seat's new anim shows
        foreach (var id in keptPose.Keys.ToList()) { try { client.Self.AnimationStop(id, true); } catch { } keptPose.TryRemove(id, out _); }
        lastSeatPoseMenuAt = DateTime.Now;
        seatPoseMissingSince = null;
    }
    static void SoftForgetKeptPoseCopies()
    {
        foreach (var id in keptPose.Keys.ToList()) keptPose.TryRemove(id, out _);
        seatPoseMissingSince = null;
    }

    // couplesMode only when explicitCouples (David asked). recovery: restore path or leave/solo — never couples.
    static async Task<string> SeatPose(Primitive seat, string seatName, DateTime since, bool change, CancellationToken ct, bool explicitCouples = false, bool recovery = false)
    {
        lastSeatPoseMenuAt = DateTime.Now; seatPoseMissingSince = null;
        bool shared = SeatHasOtherSitters(seat);
        NotePoseSeatShared(shared);
        bool couplesMode = AutoMayPickCouples(explicitCouples);
        if (recovery) couplesMode = false;
        if (recovery && wLastPosePath.Count > 0)
        {
            var rest = await SeatPosePath(seat, seatName, wLastPosePath.ToList(), ct);
            if (rest.Contains("restored") || rest.Contains("already") || rest.Contains("chose"))
            { WLog("POSE recovery restore: " + rest); return rest; }
            WLog("POSE recovery: path restore failed (" + rest + "); falling back to solo/leave");
        }
        // shared + not explicit couples + not recovery: David controls poses — do not change
        if (shared && !couplesMode && !recovery)
        {
            var m = $"'{seatName}': shared seat — not auto-picking a pose (David chooses; use 'pose couples' only if he asks)";
            WLog("POSE " + m); return m;
        }
        var d = change || recovery ? null : await WaitSeatDialog(seat, since, 5000, ct);
        if (d == null)
        {
            var t0 = DateTime.Now; client.Self.Touch(seat.LocalID);
            d = await WaitSeatDialog(seat, t0, 5000, ct);
            if (d == null) { var m = $"'{seatName}': no pose menu (none on sit, none after a touch)"; WLog("POSE " + m); return m; }
        }
        var path = new List<string>(); string current = PoseCurrent(d.Message); bool inCouples = false;
        if (recovery && !string.IsNullOrEmpty(current))
        {
            if (wLastPosePath.Count == 0) wLastPosePath = new List<string> { current };
            var m = $"'{seatName}': recovery — seat already shows [{current}]; leaving it (no couples auto-pick)";
            WLog("POSE " + m); return m;
        }
        for (int depth = 0; depth < 3; depth++)
        {
            if (client.Self.SittingOn == 0 || (WanderOn && wanderPause != null)) { WLog($"POSE '{seatName}': stopped choosing (no longer seated or paused)"); return "aborted"; }
            shared = SeatHasOtherSitters(seat);
            NotePoseSeatShared(shared);
            if (shared && !couplesMode && !recovery)
            {
                var m = $"'{seatName}': seat became shared mid-pick — stopping (David chooses poses)";
                WLog("POSE " + m); return m;
            }
            var (pick, sub, why) = PickPoseButton(d.ButtonLabels, current, wRnd, couplesMode, inCouples);
            if (pick == null)
            {
                var m = $"'{seatName}': {why}; keeping the current pose {current ?? "?"}";
                WLog("POSE " + m); return m;
            }
            var idx = d.ButtonLabels.IndexOf(pick);
            if (!sub) ClearKeptPoseCopies();
            var t1 = DateTime.Now;
            client.Self.ReplyToScriptDialog(d.Channel, idx, pick, d.ObjectID);
            path.Add(pick.Trim());
            if (sub && PoseIsCouplesNamed(pick)) inCouples = true;
            if (!sub)
            {
                wLastPosePath = path.ToList();
                var tag = recovery ? " (recovery)" : change ? " (mid-sit change)" : "";
                var mode = couplesMode ? " [couples/explicit]" : " [solo]";
                wLastPose = $"{DateTime.Now:HH:mm:ss} '{seatName}' {seat.ID}: {string.Join(" > ", path)}{tag}{mode}";
                var m = $"'{seatName}' {seat.ID}: chose {string.Join(" > ", path)} (was {current ?? "?"}; {why}){tag}";
                WLog("POSE " + m); return m;
            }
            d = await WaitSeatDialog(seat, t1, 5000, ct);
            if (d == null) { var m = $"'{seatName}': submenu '{pick}' did not open"; WLog("POSE " + m); return m; }
        }
        WLog($"POSE '{seatName}': menu deeper than 3 levels ({string.Join(" > ", path)}); stopped");
        return "too deep";
    }

    // Press an exact AVsitter menu path (e.g. Couples PG* > Cuddles* > Together).
    static async Task<string> SeatPosePath(Primitive seat, string seatName, List<string> want, CancellationToken ct)
    {
        if (want == null || want.Count == 0) return "empty path";
        lastSeatPoseMenuAt = DateTime.Now; seatPoseMissingSince = null;
        var t0 = DateTime.Now; client.Self.Touch(seat.LocalID);
        var d = await WaitSeatDialog(seat, t0, 5000, ct);
        if (d == null) return $"'{seatName}': no pose menu for path restore";
        var path = new List<string>();
        for (int i = 0; i < want.Count; i++)
        {
            if (client.Self.SittingOn == 0) return "aborted (stood)";
            var wantBtn = want[i].Trim();
            var current = PoseCurrent(d.Message);
            if (i == want.Count - 1 && current != null && string.Equals(current, PoseBare(wantBtn), StringComparison.OrdinalIgnoreCase))
            {
                wLastPosePath = want.Select(x => x.Trim()).ToList();
                return $"'{seatName}': already [{current}] (path {string.Join(" > ", want)})";
            }
            int idx = d.ButtonLabels.FindIndex(b => string.Equals(b.Trim(), wantBtn, StringComparison.OrdinalIgnoreCase)
                || string.Equals(PoseBare(b), PoseBare(wantBtn), StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return $"'{seatName}': path step '{wantBtn}' not in menu [{string.Join(" | ", d.ButtonLabels)}] (at {string.Join(" > ", path)})";
            var pick = d.ButtonLabels[idx];
            bool sub = pick.TrimEnd().EndsWith("*");
            if (!sub) ClearKeptPoseCopies();
            var t1 = DateTime.Now;
            client.Self.ReplyToScriptDialog(d.Channel, idx, pick, d.ObjectID);
            path.Add(pick.Trim());
            if (i == want.Count - 1 && !sub)
            {
                wLastPosePath = path.ToList();
                wLastPose = $"{DateTime.Now:HH:mm:ss} '{seatName}' {seat.ID}: {string.Join(" > ", path)} (restored)";
                var m = $"'{seatName}' {seat.ID}: restored {string.Join(" > ", path)}";
                WLog("POSE " + m); return m;
            }
            d = await WaitSeatDialog(seat, t1, 5000, ct);
            if (d == null) return $"'{seatName}': submenu after '{pick}' did not open";
            if (i == want.Count - 1)
            {
                var cur = PoseCurrent(d.Message);
                wLastPosePath = path.ToList();
                return $"'{seatName}': restored path to menu showing [{cur ?? "?"}] via {string.Join(" > ", path)}";
            }
        }
        return $"'{seatName}': path ended in submenus ({string.Join(" > ", path)})";
    }

    // 'pose' / 'pose change' = solo only (never couples, even when shared)
    // 'pose couples' = ONLY when David explicitly asks
    // 'pose path A > B > C' = restore an exact menu path
    // 'pose selftest'
    static async Task<string> PoseCmd(string[] a)
    {
        if (a.Length > 0 && a[0] == "selftest") return PoseSelfTest();
        var sim = Sim; var sit = client.Self.SittingOn;
        if (sit == 0) return "not seated";
        if (!sim.ObjectsPrimitives.TryGetValue(sit, out var p)) return "seat object not loaded";
        if (p.ParentID != 0 && sim.ObjectsPrimitives.TryGetValue(p.ParentID, out var root)) p = root;
        if (p.Properties == null) await EnsureProperties(sim, new() { p });
        var name = p.Properties?.Name ?? p.ID.ToString();
        using var cts = new CancellationTokenSource(30000);
        if (a.Length > 0 && a[0].Equals("path", StringComparison.OrdinalIgnoreCase))
        {
            var raw = string.Join(" ", a.Skip(1)).Trim();
            if (raw.Length == 0 && wLastPosePath.Count > 0) raw = string.Join(" > ", wLastPosePath);
            if (raw.Length == 0) return "usage: pose path <A> > <B> > <C>   (or pose path A|B|C)";
            var parts = raw.Split(new[] { '>', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (parts.Count == 0) return "usage: pose path <A> > <B> > <C>";
            return await SeatPosePath(p, name, parts, cts.Token);
        }
        bool change = a.Length > 0 && a[0].Equals("change", StringComparison.OrdinalIgnoreCase);
        bool couples = a.Length > 0 && a[0].Equals("couples", StringComparison.OrdinalIgnoreCase);
        if (couples && !SeatHasOtherSitters(p)) return "pose couples: seat is not shared (nobody else sitting); refusing";
        return await SeatPose(p, name, DateTime.Now.AddSeconds(-20), change || couples, cts.Token, explicitCouples: couples);
    }

    static string PoseSelfTest()
    {
        var lines = new List<string>(); int pass = 0, fail = 0; var rnd = new Random(7);
        void T(string name, List<string> labels, string cur, Func<string, bool> okPick, bool expectNull = false, bool couples = false, bool inCouples = false)
        {
            bool good = true; string seen = "";
            for (int i = 0; i < 60; i++)
            {
                var (p, _, why) = PickPoseButton(labels, cur, rnd, couples, inCouples);
                if (expectNull) { good &= p == null; seen = why; }
                else { good &= p != null && okPick(p); if (p != null && !seen.Contains("'" + p + "'")) seen += $"'{p}' "; }
            }
            if (good) pass++; else fail++;
            lines.Add($"{(good ? "PASS" : "FAIL")} {name}: {seen.Trim()}");
        }
        T("pillow top menu -> a submenu, never [ADJUST]", new() { "Full Lotus*", "Misc*", "[ADJUST]", "Burmese*", "Seiza*", "Half Lotus*" }, "Onlegs 4", p => p.EndsWith("*"));
        T("submenu avoids the current pose and [BACK]", new() { "Burmese 1", "Burmese 2", "[BACK]" }, "Burmese 1", p => p == "Burmese 2");
        T("garden chair numbered poses, not the current '1'", new() { "7", "8", "[ADJUST]", "4", "5", "6", "1", "2", "3" }, "1", p => p != "1" && p != "[ADJUST]");
        T("alone: couples/cuddle/sync/unsit/adjust skipped", new() { "Cuddle", "Couple 1", "Sit 1", "[SYNC]", "Unsit", "Adjust", "Kiss", "Stand" }, null, p => p == "Sit 1");
        T("male submenu skipped, female chosen", new() { "Male*", "Female*", "[ADJUST]" }, null, p => p == "Female*");
        T("M1/M2 skipped, F1/F2 chosen", new() { "M1", "M2", "F1", "F2" }, null, p => p is "F1" or "F2");
        T("man/guy/men skipped, woman or neutral chosen", new() { "Man sit", "Guy relax", "Men*", "Woman sit", "Relax" }, null, p => p is "Woman sit" or "Relax");
        T("'M*' and '(M) lean' skipped; 'Meditate' and 'Female' kept", new() { "M*", "(M) lean", "Meditate", "Female" }, null, p => p is "Meditate" or "Female");
        T("nothing suitable alone -> no press", new() { "[ADJUST]", "[BACK]", "Couple", "Male*" }, null, _ => false, expectNull: true);
        T("alone on Reiley: SINGLE* not COUPLES*", new() { "COUPLES*", "[SWAP]", "[ADJUST]", "[SECURITY]", "SINGLE*" }, null, p => p == "SINGLE*");
        T("explicit couples on Reiley: COUPLES* not SINGLE*", new() { "COUPLES*", "[SWAP]", "[ADJUST]", "[SECURITY]", "SINGLE*" }, null, p => p == "COUPLES*", couples: true);
        T("explicit couples inside COUPLES: any pose except [BACK]/male", new() { "Just Us", "Feelings", "My Heart", "Snuggle", "Sweetness", "Relax", "[BACK]", "Drama", "Guy Sit" }, "Snuggle",
            p => p is "Just Us" or "Feelings" or "My Heart" or "Sweetness" or "Relax" or "Drama", couples: true, inCouples: true);
        T("explicit couples with only SINGLE* -> no press", new() { "SINGLE*", "[ADJUST]", "Indeed", "Bored" }, null, _ => false, expectNull: true, couples: true);
        T("alone inside SINGLE: Indeed ok, Guy skipped", new() { "Guy Sit 1", "[PAGE-]", "[PAGE+]", "Bored", "Curl", "Indeed", "[BACK]", "Listen" }, null,
            p => p is "Bored" or "Curl" or "Indeed" or "Listen");
        // 2026-10-05 bug: seat becomes shared mid-sit — auto path must NOT pick Couples PG / Cuddles / Together
        T("mid-sit shared (auto/recovery): Couples PG* never picked", new() { "[ SWAP ]*", "Clean*", "[ADJUST]", "F+F2*", "FFM*", "MMF*", "Solo*", "Couples PG*", "Adult M+F*", "[BACK]" },
            "Cross legs", p => p != null && !PoseIsCouplesNamed(p) && p != "Couples PG*", couples: false);
        T("mid-sit shared auto must never pick Couples PG*", new() { "[ SWAP ]*", "Clean*", "[ADJUST]", "Solo*", "Couples PG*" }, "Cross legs",
            p => p != null && !PoseIsCouplesNamed(p), couples: false);
        {
            bool good = true;
            for (int i = 0; i < 40; i++)
            {
                var (p, _, _) = PickPoseButton(new() { "Couples PG*", "Cuddles*", "Together", "Solo*", "Cross legs" }, "Cross legs", rnd, couplesMode: false);
                if (p != null && PoseIsCouplesNamed(p)) good = false;
            }
            if (good) pass++; else fail++;
            lines.Add($"{(good ? "PASS" : "FAIL")} seat becomes shared mid-sit: no couples pose auto-picked");
        }
        {
            bool a = !AutoMayPickCouples(false) && AutoMayPickCouples(true);
            bool g = InSharedFlipGrace(DateTime.UtcNow.AddSeconds(-2), DateTime.UtcNow, 15)
                     && !InSharedFlipGrace(DateTime.UtcNow.AddSeconds(-20), DateTime.UtcNow, 15);
            if (a) pass++; else fail++;
            lines.Add($"{(a ? "PASS" : "FAIL")} AutoMayPickCouples: false unless explicit");
            if (g) pass++; else fail++;
            lines.Add($"{(g ? "PASS" : "FAIL")} InSharedFlipGrace: 15 s window after occupancy flip");
        }
        var cur = PoseCurrent("AVsitter™2.1\n\n [Onlegs 4]"); bool c = cur == "Onlegs 4"; if (c) pass++; else fail++;
        lines.Add($"{(c ? "PASS" : "FAIL")} current pose parsed from the menu text: '{cur}'");
        bool sh = PoseIsCouplesNamed("COUPLES*") && PoseIsSoloMenu("SINGLE*") && !PoseIsCouplesNamed("Indeed") && !PoseIsSoloMenu("Snuggle");
        if (sh) pass++; else fail++;
        lines.Add($"{(sh ? "PASS" : "FAIL")} COUPLES*/SINGLE* classifiers");
        return $"seat pose selftest: {pass} pass, {fail} fail (offline; no dialog pressed)\n" + string.Join("\n", lines);
    }
}
