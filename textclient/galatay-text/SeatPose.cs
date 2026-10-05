// Seat poses (added 2026-09-25 23:30, David's request): after a wander sit, pick a random pose from the SEAT'S OWN pose menu
// (AVsitter dialog sent on sit, or opened by touching the seat). No daemon-played animations: we only press dialog buttons.
// Skipped buttons: anything in [brackets] (AVsitter controls: [ADJUST] [BACK] [SYNC] [SWAP] [STOP] ...), adjust/position/sync/unsit/stand/next/prev/options/help/reset.
// Occupancy (David, 2026-10-05): when someone else shares the seat (objinfo occupied=ME,<name>), pick only from COUPLES*/couples menus;
// when alone, pick solo/SINGLE* menus and never couples. Shared with no couples menu -> leave the current pose alone.
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
    // Couples submenu / pose names (also used to skip when alone).
    static readonly Regex PoseCouplesName = new(@"\b(couples?|cuddl\w*|kiss\w*|hugs?|hugging|spoon\w*|snuggl\w*|romanc\w*|lap|together|partners?|duo|pair|2p)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Solo submenu names (Trompe Loeil Reiley etc. use SINGLE*); skip these when the seat is shared.
    static readonly Regex PoseSoloMenuName = new(@"\b(singles?|solo|alone|one\s*p|1p)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // David 2026-09-25: never pick male poses
    static readonly Regex PoseMaleSkip = new(@"\b(male|males|men|man|guy|guys|boy|boys|him|his|masc\w*)\b|(^|[\s(\[_/-])m(\d+|(?=[\s)\]_*/-])|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly ConcurrentQueue<(DateTime at, ScriptDialogEventArgs e)> wDialogs = new();
    static string wLastPose = "-";
    static bool? poseSeatShared; // last seen shared-seat state while seated (pose keeper clears copies when it flips)
    static void WanderDialogIn(ScriptDialogEventArgs e)
    {
        wDialogs.Enqueue((DateTime.Now, e));
        while (wDialogs.Count > 30) wDialogs.TryDequeue(out _);
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

    // Track alone/shared flips so the pose keeper drops copies of the old mode's anim when a partner sits/stands.
    static void NotePoseSeatShared(bool? shared)
    {
        if (shared == null) { poseSeatShared = null; return; }
        if (poseSeatShared != null && poseSeatShared != shared) ClearKeptPoseCopies();
        poseSeatShared = shared;
    }

    // pure: which button to press (null = nothing suitable). submenu = AVsitter '*' label.
    // couplesMode: seat is shared -> only couples menus/poses; once inside a couples submenu, any non-control non-male pose is fine.
    // !couplesMode: alone -> skip couples; SINGLE* / solo menus and neutral poses are fine.
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
                    // Top level while shared: only couples-named buttons (usually COUPLES*); never SINGLE*.
                    if (PoseIsSoloMenu(l) || !PoseIsCouplesNamed(l)) { skipped.Add(l); continue; }
                }
                // Inside a couples submenu: any remaining non-control non-male pose (Just Us, Snuggle, Relax, ...).
            }
            else
            {
                if (PoseIsCouplesNamed(l)) { skipped.Add(l); continue; }
            }
            ok.Add(l);
        }
        if (ok.Count == 0)
        {
            var mode = couplesMode ? (inCouplesMenu ? "couples submenu" : "shared seat") : "solo";
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
    {   // the pose keeper may hold copies of the previous seat pose; drop them so the seat's new pose shows
        foreach (var id in keptPose.Keys.ToList()) { try { client.Self.AnimationStop(id, true); } catch { } keptPose.TryRemove(id, out _); }
        lastSeatPoseMenuAt = DateTime.Now; // grace: SeatAttachLoop must not "recover" during an intentional pose change
        seatPoseMissingSince = null;
    }
    // choose a random pose from the seat's menu; since = when the sit was requested (the auto menu may already be here)
    static async Task<string> SeatPose(Primitive seat, string seatName, DateTime since, bool change, CancellationToken ct)
    {
        lastSeatPoseMenuAt = DateTime.Now; seatPoseMissingSince = null;
        bool shared = SeatHasOtherSitters(seat);
        NotePoseSeatShared(shared);
        var d = change ? null : await WaitSeatDialog(seat, since, 5000, ct);
        if (d == null)
        {
            var t = DateTime.Now; client.Self.Touch(seat.LocalID);
            d = await WaitSeatDialog(seat, t, 5000, ct);
            if (d == null) { var m = $"'{seatName}': no pose menu (none on sit, none after a touch)"; WLog("POSE " + m); return m; }
        }
        var path = new List<string>(); string current = PoseCurrent(d.Message); bool inCouples = false;
        for (int depth = 0; depth < 3; depth++)
        {
            if (client.Self.SittingOn == 0 || (WanderOn && wanderPause != null)) { WLog($"POSE '{seatName}': stopped choosing (no longer seated or paused)"); return "aborted"; }
            // re-check occupancy each depth (partner may have stood/sat while we were in a submenu)
            shared = SeatHasOtherSitters(seat);
            NotePoseSeatShared(shared);
            var (pick, sub, why) = PickPoseButton(d.ButtonLabels, current, wRnd, shared, inCouples);
            if (pick == null)
            {
                var m = shared && !inCouples
                    ? $"'{seatName}': shared seat but no couples menu; keeping the current pose {current ?? "?"}"
                    : $"'{seatName}': {why}; keeping the current pose {current ?? "?"}";
                WLog("POSE " + m); return m;
            }
            var idx = d.ButtonLabels.IndexOf(pick);
            if (!sub) ClearKeptPoseCopies();
            var t = DateTime.Now;
            client.Self.ReplyToScriptDialog(d.Channel, idx, pick, d.ObjectID);
            path.Add(pick.Trim());
            if (sub && PoseIsCouplesNamed(pick)) inCouples = true;
            if (!sub)
            {
                wLastPose = $"{DateTime.Now:HH:mm:ss} '{seatName}' {seat.ID}: {string.Join(" > ", path)}{(change ? " (mid-sit change)" : "")}{(shared ? " [couples]" : " [solo]")}";
                var m = $"'{seatName}' {seat.ID}: chose {string.Join(" > ", path)} (was {current ?? "?"}; {why}){(change ? " - mid-sit change" : "")}";
                WLog("POSE " + m); return m;
            }
            d = await WaitSeatDialog(seat, t, 5000, ct);
            if (d == null) { var m = $"'{seatName}': submenu '{pick}' did not open"; WLog("POSE " + m); return m; }
        }
        WLog($"POSE '{seatName}': menu deeper than 3 levels ({string.Join(" > ", path)}); stopped");
        return "too deep";
    }

    // 'pose' = pick a random pose from the current seat's menu (uses the menu sent on sit within the last 20 s, else touches the seat)
    // 'pose change' = always touch the seat for a fresh menu; 'pose selftest'
    static async Task<string> PoseCmd(string[] a)
    {
        if (a.Length > 0 && a[0] == "selftest") return PoseSelfTest();
        var sim = Sim; var sit = client.Self.SittingOn;
        if (sit == 0) return "not seated";
        if (!sim.ObjectsPrimitives.TryGetValue(sit, out var p)) return "seat object not loaded";
        if (p.ParentID != 0 && sim.ObjectsPrimitives.TryGetValue(p.ParentID, out var root)) p = root;
        if (p.Properties == null) await EnsureProperties(sim, new() { p });
        using var t = new CancellationTokenSource(30000);
        bool change = a.Length > 0 && a[0] == "change";
        return await SeatPose(p, p.Properties?.Name ?? p.ID.ToString(), DateTime.Now.AddSeconds(-20), change, t.Token);
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
        // Reiley Net Chair-style menus (2026-10-05): COUPLES* vs SINGLE*
        T("alone on Reiley: SINGLE* not COUPLES*", new() { "COUPLES*", "[SWAP]", "[ADJUST]", "[SECURITY]", "SINGLE*" }, null, p => p == "SINGLE*");
        T("shared on Reiley: COUPLES* not SINGLE*", new() { "COUPLES*", "[SWAP]", "[ADJUST]", "[SECURITY]", "SINGLE*" }, null, p => p == "COUPLES*", couples: true);
        T("shared inside COUPLES: any pose except [BACK]/male", new() { "Just Us", "Feelings", "My Heart", "Snuggle", "Sweetness", "Relax", "[BACK]", "Drama", "Guy Sit" }, "Snuggle",
            p => p is "Just Us" or "Feelings" or "My Heart" or "Sweetness" or "Relax" or "Drama", couples: true, inCouples: true);
        T("shared with no couples menu -> no press", new() { "SINGLE*", "[ADJUST]", "Indeed", "Bored" }, null, _ => false, expectNull: true, couples: true);
        T("alone inside SINGLE: Indeed ok, Guy skipped", new() { "Guy Sit 1", "[PAGE-]", "[PAGE+]", "Bored", "Curl", "Indeed", "[BACK]", "Listen" }, null,
            p => p is "Bored" or "Curl" or "Indeed" or "Listen");
        var cur = PoseCurrent("AVsitter™2.1\n\n [Onlegs 4]"); bool c = cur == "Onlegs 4"; if (c) pass++; else fail++;
        lines.Add($"{(c ? "PASS" : "FAIL")} current pose parsed from the menu text: '{cur}'");
        bool sh = PoseIsCouplesNamed("COUPLES*") && PoseIsSoloMenu("SINGLE*") && !PoseIsCouplesNamed("Indeed") && !PoseIsSoloMenu("Snuggle");
        if (sh) pass++; else fail++;
        lines.Add($"{(sh ? "PASS" : "FAIL")} COUPLES*/SINGLE* classifiers");
        return $"seat pose selftest: {pass} pass, {fail} fail (offline; no dialog pressed)\n" + string.Join("\n", lines);
    }
}
