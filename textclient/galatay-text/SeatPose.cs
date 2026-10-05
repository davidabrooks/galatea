// Seat poses (added 2026-09-25 23:30, David's request): after a wander sit, pick a random pose from the SEAT'S OWN pose menu
// (AVsitter dialog sent on sit, or opened by touching the seat). No daemon-played animations: we only press dialog buttons.
// Skipped buttons: anything in [brackets] (AVsitter controls: [ADJUST] [BACK] [SYNC] [SWAP] [STOP] ...), adjust/position/sync/unsit/stand/next/prev/options/help/reset.
// Occupancy / couples (David, 2026-10-05 evening + noon follow-up): NEVER auto-pick couples. Couples menus only when
// David explicitly asks ('pose couples'). The "David chooses" hold-back applies ONLY while David himself is on the same
// seat — other sitters (e.g. Sophie) do not block solo pose / recovery. When David leaves a seat we shared in a couples
// pose, auto-switch to solo (last solo path on that seat, else PG Solo* menu; skip adult). 'pose path' always backs to
// the menu root first ([BACK] until top). Bug 2026-10-05 11:53: shared mid-sit must not auto-pick Couples PG.
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
    // Adult / multi-avatar menus to skip when auto-picking PG solo after David leaves.
    static readonly Regex PoseAdultMenuName = new(@"\b(adult|ffm|mmf|f\+?f\d*|m\+?f\d*|xxx|nsfw)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // David 2026-09-25: never pick male poses
    static readonly Regex PoseMaleSkip = new(@"\b(male|males|men|man|guy|guys|boy|boys|him|his|masc\w*)\b|(^|[\s(\[_/-])m(\d+|(?=[\s)\]_*/-])|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly ConcurrentQueue<(DateTime at, ScriptDialogEventArgs e)> wDialogs = new();
    static string wLastPose = "-";
    static List<string> wLastPosePath = new(); // last AVsitter menu path we pressed/restored
    static List<string> wLastSoloPosePath = new(); // last non-couples path on this seat (for David-left restore)
    // Leaf pose name -> came from a couples menu path (Magnetize under Couples PG*, etc.)
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> poseLeafFromCouples = new(StringComparer.OrdinalIgnoreCase);
    static bool? poseSeatShared; // last seen shared-seat state while seated (any other sitter)
    static bool poseDavidOnSeat; // David Nightingale specifically on this seat with me
    static DateTime poseSharedChangedAt = DateTime.MinValue; // alone<->shared flip (recovery grace)
    static int poseDavidLeftSwitchGen; // debounce auto-solo when David leaves
    // David 2026-10-05: after a partner sits/stands, AVsitter often restarts anims; wait before "pose lost" recovery.
    public static double PoseSharedFlipGraceS = 15;
    public static double PoseRecoveryMissingS = 8; // was 3; brief AVsitter swaps were false positives

    // pure: automatic paths never pick couples; only explicit 'pose couples' (David asked).
    public static bool AutoMayPickCouples(bool explicitCouplesRequest) => explicitCouplesRequest;

    // pure: still inside the post-occupancy-change grace window?
    public static bool InSharedFlipGrace(DateTime flipAt, DateTime now, double graceS) =>
        flipAt != DateTime.MinValue && (now - flipAt).TotalSeconds < graceS;

    // pure: "David chooses" hold-back — only while David himself shares the seat (not Sophie alone).
    public static bool DavidChoosesPoses(bool davidOnSeat, bool explicitCouplesRequest) =>
        davidOnSeat && !explicitCouplesRequest;

    // pure: does this menu path go through a couples-named step?
    public static bool PosePathIsCouples(IReadOnlyList<string> path) =>
        path != null && path.Any(PoseIsCouplesNamed);

    // Remember whether a leaf pose was reached via a couples menu (for recovery after David leaves).
    static void NotePoseLeafMenu(IReadOnlyList<string> path)
    {
        if (path == null || path.Count == 0) return;
        var leaf = PoseBare(path[^1]);
        if (string.IsNullOrEmpty(leaf)) return;
        poseLeafFromCouples[leaf] = PosePathIsCouples(path);
    }

    // pure: is this on-screen pose name from a couples menu / couples-named?
    public static bool PoseLeafIsCouples(string name, IReadOnlyList<string> lastPath = null)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (PoseIsCouplesNamed(name)) return true;
        if (PosePathIsCouples(lastPath)) return true;
        return poseLeafFromCouples.TryGetValue(PoseBare(name), out var c) && c;
    }

    // pure: find index of want button in labels (trim / bare match)
    public static int PoseFindButton(IReadOnlyList<string> labels, string want)
    {
        if (labels == null || string.IsNullOrEmpty(want)) return -1;
        var w = want.Trim();
        for (int i = 0; i < labels.Count; i++)
        {
            var b = labels[i] ?? "";
            if (string.Equals(b.Trim(), w, StringComparison.OrdinalIgnoreCase)) return i;
            if (string.Equals(PoseBare(b), PoseBare(w), StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    // pure: if the open menu already contains the path's final leaf, start at that leaf only;
    // else if it contains some later step, start from the first matching step; else 0 (full path from root).
    public static int PosePathStartIndex(IReadOnlyList<string> labels, IReadOnlyList<string> want)
    {
        if (labels == null || want == null || want.Count == 0) return 0;
        // Prefer the final leaf when present (submenu reopened on Solo Couch* with Cross legs visible)
        if (PoseFindButton(labels, want[^1]) >= 0) return want.Count - 1;
        for (int i = 0; i < want.Count; i++)
            if (PoseFindButton(labels, want[i]) >= 0) return i;
        return 0;
    }

    // pure: sitter name list from Sitters() includes David Nightingale?
    public static bool SitterListHasDavid(IEnumerable<string> names) =>
        names != null && names.Any(n => n != null && n != "ME"
            && n.IndexOf("David Nightingale", StringComparison.OrdinalIgnoreCase) >= 0);

    static void WanderDialogIn(ScriptDialogEventArgs e)
    {
        wDialogs.Enqueue((DateTime.Now, e));
        while (wDialogs.Count > 30) wDialogs.TryDequeue(out _);
        if (client?.Self?.SittingOn != 0)
        {
            var cur = PoseCurrent(e.Message);
            if (!string.IsNullOrEmpty(cur))
            {
                // Couples submenu (sibling names like Together) or known couples path → remember this leaf
                bool couplesMenu = (e.ButtonLabels != null && e.ButtonLabels.Any(PoseIsCouplesNamed))
                    || PosePathIsCouples(wLastPosePath);
                if (couplesMenu) poseLeafFromCouples[PoseBare(cur)] = true;
                else if (e.ButtonLabels != null && e.ButtonLabels.Any(PoseIsSoloMenu)
                         && !e.ButtonLabels.Any(PoseIsCouplesNamed))
                    poseLeafFromCouples[PoseBare(cur)] = false;
                if (wLastPosePath.Count > 0
                    && !string.Equals(wLastPosePath[^1], cur, StringComparison.OrdinalIgnoreCase))
                    wLastPosePath[^1] = cur;
            }
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
    static bool PoseIsAdultMenu(string raw) => PoseAdultMenuName.IsMatch(PoseBare(raw));
    static bool PoseIsMale(string raw) => PoseMaleSkip.IsMatch(PoseBare(raw));
    static bool PoseIsBackButton(string raw)
    {
        var b = PoseBare(raw).Trim().TrimStart('[').TrimEnd(']').Trim();
        return b.Equals("BACK", StringComparison.OrdinalIgnoreCase) || b.Equals("<<", StringComparison.OrdinalIgnoreCase);
    }

    // sitter name list on this seat's root (Sitters map: "ME", "David Nightingale", ...).
    static List<string> SeatSitterNames(Primitive seat)
    {
        if (seat == null) return new();
        var sim = Sim; if (sim == null) return new();
        uint root = seat.LocalID;
        if (seat.ParentID != 0 && sim.ObjectsPrimitives.ContainsKey(seat.ParentID)) root = seat.ParentID;
        if (!Sitters(sim).TryGetValue(root, out var l) || l == null) return new();
        return l.ToList();
    }
    // true when another avatar (not ME) is seated on this seat's root.
    static bool SeatHasOtherSitters(Primitive seat) => SeatSitterNames(seat).Any(n => n != "ME");
    // true when David Nightingale specifically shares this seat with me.
    static bool SeatHasDavid(Primitive seat) => SitterListHasDavid(SeatSitterNames(seat));

    // Track alone/shared flips. Do NOT hard-clear kept anim copies on a flip: that + AVsitter's brief restart
    // looked like "no seat pose for 3 s" and pose recovery then auto-picked Couples (2026-10-05 Lalou sofa).
    static void NotePoseSeatShared(bool? shared, bool? davidOnSeat = null)
    {
        if (shared == null)
        {
            poseSeatShared = null; poseSharedChangedAt = DateTime.MinValue; poseDavidOnSeat = false; return;
        }
        if (poseSeatShared != null && poseSeatShared != shared)
        {
            SoftForgetKeptPoseCopies();
            poseSharedChangedAt = DateTime.Now;
            lastSeatPoseMenuAt = DateTime.Now;
            seatPoseMissingSince = null;
            Log("height", $"pose: seat occupancy -> {(shared.Value ? "shared" : "alone")}; recovery grace {PoseSharedFlipGraceS:g} s (AVsitter may restart anims)");
        }
        poseSeatShared = shared;
        if (davidOnSeat != null) NotePoseDavidOnSeat(davidOnSeat.Value);
    }

    // When David leaves a seat we shared in a couples pose, switch to solo right away (other sitters OK).
    static void NotePoseDavidOnSeat(bool davidHere)
    {
        bool was = poseDavidOnSeat;
        poseDavidOnSeat = davidHere;
        if (was && !davidHere && PosePathIsCouples(wLastPosePath) && client?.Self?.SittingOn != 0)
        {
            var gen = System.Threading.Interlocked.Increment(ref poseDavidLeftSwitchGen);
            Log("height", "pose: David left the seat while we were in a couples pose — switching to solo");
            _ = Task.Run(() => PoseSwitchToSoloAfterDavidLeft(gen));
        }
    }

    // pure: which button to press (null = nothing suitable). submenu = AVsitter '*' label.
    // couplesMode: ONLY for explicit 'pose couples'. !couplesMode (default/recovery/wander): skip couples even if shared.
    static (string pick, bool submenu, string why) PickPoseButton(List<string> labels, string current, Random rnd, bool couplesMode = false, bool inCouplesMenu = false, bool preferPgSolo = false)
    {
        var ok = new List<string>(); var skipped = new List<string>();
        bool seatSel = PoseMenuLooksLikeSeatSelect(labels);
        foreach (var raw in labels ?? new())
        {
            var l = raw ?? "";
            if (PoseIsControl(l) || PoseIsMale(l)) { skipped.Add(l); continue; }
            // Never pick AVsitter seat-select chrome (⊘sitter, bare F/M roles)
            if (l.TrimStart().StartsWith("⊘") || l.TrimStart().StartsWith("⊘")) { skipped.Add(l); continue; }
            if (seatSel) { skipped.Add(l); continue; }
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
                if (preferPgSolo && PoseIsAdultMenu(l)) { skipped.Add(l); continue; }
            }
            ok.Add(l);
        }
        if (ok.Count == 0)
        {
            var mode = couplesMode ? (inCouplesMenu ? "couples submenu" : "couples (explicit)") : "solo";
            return (null, false, $"no suitable {mode} option (skipped: {string.Join(", ", skipped)})");
        }
        // After David leaves: prefer Solo*/SINGLE* submenu at the top level when present
        if (preferPgSolo && !couplesMode && !inCouplesMenu)
        {
            var soloMenus = ok.Where(PoseIsSoloMenu).ToList();
            if (soloMenus.Count > 0) ok = soloMenus;
        }
        var notCur = ok.Where(l => current == null || !string.Equals(PoseBare(l), current, StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = notCur.Count > 0 ? notCur : ok;
        var p = pool[rnd.Next(pool.Count)];
        return (p, p.TrimEnd().EndsWith("*"), $"{pool.Count} choices, skipped {skipped.Count}{(couplesMode ? ", couples" : preferPgSolo ? ", pg-solo" : ", solo")}");
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
            var hits = wDialogs.ToArray().Where(d => d.at >= since && DialogFromSeat(d.e, seat, sim)).OrderByDescending(d => d.at).ToList();
            // Prefer a real pose menu over AVsitter seat-select (F/M/sitter) when both appear after a touch
            var pose = hits.FirstOrDefault(d => d.e?.ButtonLabels != null && !PoseMenuLooksLikeSeatSelect(d.e.ButtonLabels));
            if (pose.e != null) return pose.e;
            if (hits.Count > 0 && hits[0].e != null) return hits[0].e;
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
    static async Task<string> SeatPose(Primitive seat, string seatName, DateTime since, bool change, CancellationToken ct, bool explicitCouples = false, bool recovery = false, bool preferPgSolo = false)
    {
        lastSeatPoseMenuAt = DateTime.Now; seatPoseMissingSince = null;
        bool shared = SeatHasOtherSitters(seat);
        bool davidHere = SeatHasDavid(seat);
        NotePoseSeatShared(shared, davidHere);
        bool couplesMode = AutoMayPickCouples(explicitCouples);
        if (recovery) couplesMode = false;
        if (recovery && wLastPosePath.Count > 0 && !PosePathIsCouples(wLastPosePath))
        {
            var rest = await SeatPosePath(seat, seatName, wLastPosePath.ToList(), ct);
            if (rest.Contains("restored") || rest.Contains("already") || rest.Contains("chose"))
            { WLog("POSE recovery restore: " + rest); return rest; }
            WLog("POSE recovery: path restore failed (" + rest + "); falling back to solo/leave");
        }
        // ONLY while David himself shares the seat: he chooses poses (other sitters do not block solo)
        if (DavidChoosesPoses(davidHere, explicitCouples) && !recovery)
        {
            var m = $"'{seatName}': David is on this seat — not auto-picking a pose (he chooses; use 'pose couples' only if he asks)";
            WLog("POSE " + m); return m;
        }
        var d = change || recovery ? null : await WaitSeatDialog(seat, since, 5000, ct);
        if (d == null)
        {
            var t0 = DateTime.Now; client.Self.Touch(seat.LocalID);
            d = await WaitSeatDialog(seat, t0, 5000, ct);
            if (d == null) { var m = $"'{seatName}': no pose menu (none on sit, none after a touch)"; WLog("POSE " + m); return m; }
        }
        // Escape AVsitter seat-select (F/M/sitter) — not a pose menu
        if (PoseMenuLooksLikeSeatSelect(d.ButtonLabels))
        {
            int fIdx = d.ButtonLabels.FindIndex(b => PoseBare(b).Equals("F", StringComparison.OrdinalIgnoreCase));
            if (fIdx >= 0)
            {
                var tF = DateTime.Now;
                client.Self.ReplyToScriptDialog(d.Channel, fIdx, d.ButtonLabels[fIdx], d.ObjectID);
                var d2 = await WaitSeatDialog(seat, tF, 5000, ct);
                if (d2 != null) d = d2;
            }
            if (PoseMenuLooksLikeSeatSelect(d.ButtonLabels))
            {
                var m = $"'{seatName}': seat-select menu only (no pose categories); not picking";
                WLog("POSE " + m); return m;
            }
        }
        var path = new List<string>(); string current = PoseCurrent(d.Message); bool inCouples = false;
        if (recovery && !string.IsNullOrEmpty(current))
        {
            // Couples leaf after David left must be replaced (Magnetize etc. are not PoseIsCouplesNamed by text alone)
            if (PoseLeafIsCouples(current, wLastPosePath))
            {
                WLog($"POSE '{seatName}': recovery — seat shows couples [{current}]; replacing with solo (not leaving it)");
                if (wLastSoloPosePath.Count > 0)
                {
                    var rest = await SeatPosePath(seat, seatName, wLastSoloPosePath.ToList(), ct);
                    if (rest.Contains("restored") || rest.Contains("already") || rest.Contains("chose"))
                    { WLog("POSE recovery couples->solo: " + rest); return rest; }
                    WLog("POSE recovery couples->solo path failed (" + rest + "); picking PG solo");
                }
                preferPgSolo = true; // fall through to PickPoseButton with preferPgSolo
            }
            else
            {
                if (wLastPosePath.Count == 0) wLastPosePath = new List<string> { current };
                var m = $"'{seatName}': recovery — seat already shows [{current}]; leaving it (no couples auto-pick)";
                WLog("POSE " + m); return m;
            }
        }
        for (int depth = 0; depth < 3; depth++)
        {
            if (client.Self.SittingOn == 0 || (WanderOn && wanderPause != null)) { WLog($"POSE '{seatName}': stopped choosing (no longer seated or paused)"); return "aborted"; }
            shared = SeatHasOtherSitters(seat);
            davidHere = SeatHasDavid(seat);
            NotePoseSeatShared(shared, davidHere);
            if (DavidChoosesPoses(davidHere, explicitCouples) && !recovery)
            {
                var m = $"'{seatName}': David joined mid-pick — stopping (he chooses poses)";
                WLog("POSE " + m); return m;
            }
            var (pick, sub, why) = PickPoseButton(d.ButtonLabels, current, wRnd, couplesMode, inCouples, preferPgSolo: recovery || preferPgSolo);
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
                NotePoseLeafMenu(path);
                if (!PosePathIsCouples(path)) wLastSoloPosePath = path.ToList();
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

    // True when this dialog looks like the AVsitter pose *category* root (Solo* + Couples* siblings).
    // Solo* submenu alone (Solo Adult* / Solo Couch* / Solo Pouf*) is NOT the root — BACK further.
    public static bool PoseMenuLooksLikeRoot(IReadOnlyList<string> labels)
    {
        if (labels == null || labels.Count == 0) return false;
        bool hasSolo = labels.Any(PoseIsSoloMenu);
        bool hasCouples = labels.Any(PoseIsCouplesNamed);
        if (hasSolo && hasCouples) return true;
        // Several category * menus that are not all solo-named (e.g. Clean* + Solo* + Adult*)
        var cats = labels.Where(b => (b ?? "").TrimEnd().EndsWith("*") && !PoseIsControl(b)).ToList();
        if (cats.Count >= 3 && cats.Any(PoseIsSoloMenu) && cats.Any(b => !PoseIsSoloMenu(b))) return true;
        return false;
    }

    // Seat-select / role menus (AVsitter™ seat select): bare F + M role buttons, sitter names — not pose categories.
    // Do not treat "M*" / "Female" pose submenus as seat-select (PoseBare("M*") == "M").
    static bool PoseMenuLooksLikeSeatSelect(IReadOnlyList<string> labels)
    {
        if (labels == null || labels.Count == 0) return false;
        if (PoseMenuLooksLikeRoot(labels)) return false;
        int poseCats = labels.Count(b => !PoseIsControl(b) && (PoseIsSoloMenu(b) || PoseIsCouplesNamed(b) || PoseIsAdultMenu(b)));
        if (poseCats > 0) return false;
        static bool ExactRole(string raw, string role)
        {
            var t = (raw ?? "").Trim();
            if (t.EndsWith("*")) return false; // M* / F* are pose submenus, not seat-select roles
            return PoseBare(t).Equals(role, StringComparison.OrdinalIgnoreCase);
        }
        bool hasF = labels.Any(b => ExactRole(b, "F"));
        bool hasM = labels.Any(b => ExactRole(b, "M"));
        return hasF && hasM; // both role buttons present
    }

    // Press [BACK] until the top-level pose menu (AVsitter reopens in the last submenu).
    // Do NOT press [BACK] when already on the category root (it still shows [BACK] — that goes to seat select).
    static async Task<ScriptDialogEventArgs> SeatPoseBackToRoot(Primitive seat, ScriptDialogEventArgs d, CancellationToken ct)
    {
        for (int i = 0; i < 8; i++)
        {
            if (d?.ButtonLabels == null) return d;
            if (PoseMenuLooksLikeRoot(d.ButtonLabels)) return d; // already at pose root — keep it
            // Ignore seat-select overlays: re-touch for the pose menu
            if (PoseMenuLooksLikeSeatSelect(d.ButtonLabels))
            {
                var tTouch = DateTime.Now; client.Self.Touch(seat.LocalID);
                var again = await WaitSeatDialog(seat, tTouch, 5000, ct);
                if (again == null) return d;
                d = again;
                if (PoseMenuLooksLikeRoot(d.ButtonLabels)) return d;
                // if still seat-select, try pressing F (female role) then continue
                int fIdx = d.ButtonLabels.FindIndex(b => PoseBare(b).Equals("F", StringComparison.OrdinalIgnoreCase));
                if (fIdx >= 0)
                {
                    var tF = DateTime.Now;
                    client.Self.ReplyToScriptDialog(d.Channel, fIdx, d.ButtonLabels[fIdx], d.ObjectID);
                    again = await WaitSeatDialog(seat, tF, 5000, ct);
                    if (again != null) d = again;
                    if (PoseMenuLooksLikeRoot(d.ButtonLabels)) return d;
                }
                continue;
            }
            int back = d.ButtonLabels.FindIndex(PoseIsBackButton);
            if (back < 0) return d; // no [BACK] => treat as root
            var before = string.Join("|", d.ButtonLabels);
            var t1 = DateTime.Now;
            client.Self.ReplyToScriptDialog(d.Channel, back, d.ButtonLabels[back], d.ObjectID);
            var next = await WaitSeatDialog(seat, t1, 5000, ct);
            if (next == null) return d;
            var after = string.Join("|", next.ButtonLabels ?? new List<string>());
            d = next;
            if (after == before) return d; // stuck
            if (PoseMenuLooksLikeRoot(d.ButtonLabels)) return d;
        }
        return d;
    }

    // Press an exact AVsitter menu path. If the open menu already has the final leaf (e.g. Cross legs on Solo Couch*),
    // press it directly. Otherwise BACK to the real category root, then walk the path.
    static async Task<string> SeatPosePath(Primitive seat, string seatName, List<string> want, CancellationToken ct)
    {
        if (want == null || want.Count == 0) return "empty path";
        lastSeatPoseMenuAt = DateTime.Now; seatPoseMissingSince = null;
        var t0 = DateTime.Now; client.Self.Touch(seat.LocalID);
        var d = await WaitSeatDialog(seat, t0, 5000, ct);
        if (d == null) return $"'{seatName}': no pose menu for path restore";
        int start = PosePathStartIndex(d.ButtonLabels, want);
        if (start == 0 && PoseFindButton(d.ButtonLabels, want[0]) < 0)
            d = await SeatPoseBackToRoot(seat, d, ct);
        // Re-evaluate after backing out
        start = PosePathStartIndex(d.ButtonLabels, want);
        var path = new List<string>();
        // If we skip prefix steps, keep them in the recorded path for wLastPosePath
        for (int pfx = 0; pfx < start; pfx++) path.Add(want[pfx].Trim());
        for (int i = start; i < want.Count; i++)
        {
            if (client.Self.SittingOn == 0) return "aborted (stood)";
            var wantBtn = want[i].Trim();
            var current = PoseCurrent(d.Message);
            if (i == want.Count - 1 && current != null && string.Equals(current, PoseBare(wantBtn), StringComparison.OrdinalIgnoreCase))
            {
                wLastPosePath = want.Select(x => x.Trim()).ToList();
                NotePoseLeafMenu(wLastPosePath);
                return $"'{seatName}': already [{current}] (path {string.Join(" > ", want)})";
            }
            int idx = PoseFindButton(d.ButtonLabels, wantBtn);
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
                NotePoseLeafMenu(path);
                if (!PosePathIsCouples(path)) wLastSoloPosePath = path.ToList();
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
                NotePoseLeafMenu(path);
                return $"'{seatName}': restored path to menu showing [{cur ?? "?"}] via {string.Join(" > ", path)}";
            }
        }
        return $"'{seatName}': path ended in submenus ({string.Join(" > ", path)})";
    }

    static async Task PoseSwitchToSoloAfterDavidLeft(int gen)
    {
        try
        {
            await Task.Delay(800); // let AVsitter finish its sit-leave shuffle
            if (gen != System.Threading.Volatile.Read(ref poseDavidLeftSwitchGen)) return;
            if (client?.Self?.SittingOn == 0) return;
            if (poseDavidOnSeat) return; // he came back
            var sim = Sim;
            if (sim == null || !sim.ObjectsPrimitives.TryGetValue(client.Self.SittingOn, out var p) || p == null) return;
            if (p.ParentID != 0 && sim.ObjectsPrimitives.TryGetValue(p.ParentID, out var root) && root != null) p = root;
            if (p.Properties == null) await EnsureProperties(sim, new() { p });
            var name = p.Properties?.Name ?? p.ID.ToString();
            using var cts = new CancellationTokenSource(45000);
            if (wLastSoloPosePath.Count > 0)
            {
                var r = await SeatPosePath(p, name, wLastSoloPosePath.ToList(), cts.Token);
                Log("height", "pose: David-left solo restore: " + r);
                if (r.Contains("restored") || r.Contains("already") || r.Contains("chose")) return;
            }
            // random PG solo (prefer Solo* menus, skip adult). Not recovery: that would leave the couples pose.
            var r2 = await SeatPose(p, name, DateTime.Now, true, cts.Token, explicitCouples: false, recovery: false, preferPgSolo: true);
            Log("height", "pose: David-left solo pick: " + r2);
        }
        catch (Exception ex) { Log("height", "pose: David-left solo switch error: " + ex.GetBaseException().Message); }
    }

    // 'pose' / 'pose change' = solo only (never couples). Blocked only while David himself shares the seat.
    // 'pose couples' = ONLY when David explicitly asks (and is on the seat)
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
        if (couples && !SeatHasDavid(p)) return "pose couples: David is not on this seat; refusing (couples only when he asks while sharing)";
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
        {
            bool d1 = DavidChoosesPoses(davidOnSeat: true, explicitCouplesRequest: false);
            bool d2 = !DavidChoosesPoses(davidOnSeat: false, explicitCouplesRequest: false); // Sophie alone: no hold-back
            bool d3 = !DavidChoosesPoses(davidOnSeat: true, explicitCouplesRequest: true);
            bool names = SitterListHasDavid(new[] { "ME", "SophieJeanneLaDouce Resident" }) == false
                      && SitterListHasDavid(new[] { "ME", "David Nightingale" });
            bool couplesPath = PosePathIsCouples(new[] { "Couples PG*", "Cuddles*", "Together" })
                            && !PosePathIsCouples(new[] { "Solo*", "Solo Couch*", "Cross legs" });
            if (d1 && d2 && d3) pass++; else fail++;
            lines.Add($"{(d1 && d2 && d3 ? "PASS" : "FAIL")} DavidChoosesPoses: only while David is on the seat");
            if (names) pass++; else fail++;
            lines.Add($"{(names ? "PASS" : "FAIL")} SitterListHasDavid: David Nightingale vs Sophie");
            if (couplesPath) pass++; else fail++;
            lines.Add($"{(couplesPath ? "PASS" : "FAIL")} PosePathIsCouples: Couples PG path vs Solo path");
        }
        {
            // after David leaves: prefer Solo*, skip Adult / Couples
            bool good = true;
            for (int i = 0; i < 40; i++)
            {
                var (p, _, _) = PickPoseButton(new() { "[ SWAP ]*", "Clean*", "[ADJUST]", "F+F2*", "FFM*", "Solo*", "Couples PG*", "Adult M+F*", "[BACK]" },
                    "Together", rnd, couplesMode: false, preferPgSolo: true);
                if (p != "Solo*") good = false;
            }
            if (good) pass++; else fail++;
            lines.Add($"{(good ? "PASS" : "FAIL")} after David leaves: preferPgSolo picks Solo* (skips adult/couples)");
        }
        {
            bool back = PoseIsBackButton("[BACK]") && PoseIsBackButton("BACK") && PoseIsBackButton("[ BACK ]")
                     && PoseIsBackButton("<<") && !PoseIsBackButton("Solo*") && !PoseIsBackButton("[ADJUST]");
            if (back) pass++; else fail++;
            lines.Add($"{(back ? "PASS" : "FAIL")} PoseIsBackButton detects [BACK]");
        }
        {
            // Sophie alone must not trigger DavidChooses; shared-with-Sophie is fine for solo pick
            bool hold = DavidChoosesPoses(davidOnSeat: false, explicitCouplesRequest: false) == false;
            if (hold) pass++; else fail++;
            lines.Add($"{(hold ? "PASS" : "FAIL")} Sophie alone: pose/recovery not held back (DavidChooses false)");
        }
        {
            var root = new List<string> { "[ SWAP ]*", "Clean*", "[ADJUST]", "F+F2*", "FFM*", "MMF*", "Solo*", "Couples PG*", "Adult M+F*", "[BACK]" };
            var seatSel = new List<string> { "M2", "  ", "[ADJUST]", "F", "M", "⊘SophieJeanne" };
            var leaf = new List<string> { "[ADJUST]", "[<<]", "[>>]", "Forever", "Together", "[BACK]" };
            var soloSub = new List<string> { "Solo Adult*", "[ SWAP ]*", "[ADJUST]", "[BACK]", "Solo Couch*", "Solo Pouf*" };
            bool r = PoseMenuLooksLikeRoot(root) && !PoseMenuLooksLikeRoot(seatSel) && !PoseMenuLooksLikeRoot(leaf)
                  && !PoseMenuLooksLikeRoot(soloSub); // Solo* submenu must not stop BACK (#53 follow-up 13:53)
            bool s = PoseMenuLooksLikeSeatSelect(seatSel) && !PoseMenuLooksLikeSeatSelect(root);
            if (r && s) pass++; else fail++;
            lines.Add($"{(r && s ? "PASS" : "FAIL")} PoseMenuLooksLikeRoot vs seat-select (do not BACK off root / Solo* submenu)");
        }
        {
            // Path restore: Solo Couch* menu already has Cross legs — start at leaf, do not require Solo*
            var couch = new List<string> { "[ADJUST]", "[<<]", "[>>]", "Hang out", "Relaxing", "Sexy sit", "Meh", "Backlean", "Pretty", "[BACK]", "Cross legs", "Contemplate" };
            var want = new[] { "Solo*", "Solo Couch*", "Cross legs" };
            bool startLeaf = PosePathStartIndex(couch, want) == 2 && PoseFindButton(couch, "Cross legs") >= 0;
            bool startRoot = PosePathStartIndex(new List<string> { "Solo*", "Couples PG*", "Clean*" }, want) == 0;
            if (startLeaf && startRoot) pass++; else fail++;
            lines.Add($"{(startLeaf && startRoot ? "PASS" : "FAIL")} PosePathStartIndex: leaf on open submenu vs full path from root");
        }
        {
            // Couples leaf tracking: Magnetize under Couples PG* must not be "left" after David leaves
            poseLeafFromCouples.Clear();
            NotePoseLeafMenu(new[] { "Couples PG*", "Cuddles*", "Magnetize" });
            NotePoseLeafMenu(new[] { "Solo*", "Solo Couch*", "Cross legs" });
            bool mag = PoseLeafIsCouples("Magnetize", null) && PoseLeafIsCouples("Magnetize", new[] { "Couples PG*", "Cuddles*", "Magnetize" });
            bool cross = !PoseLeafIsCouples("Cross legs", new[] { "Solo*", "Solo Couch*", "Cross legs" });
            bool together = PoseLeafIsCouples("Together", null) == false && PoseLeafIsCouples("Together", new[] { "Couples PG*", "Cuddles*", "Together" });
            // Together is couples-named by regex too
            bool togetherNamed = PoseIsCouplesNamed("Together");
            if (mag && cross && togetherNamed) pass++; else fail++;
            lines.Add($"{(mag && cross && togetherNamed ? "PASS" : "FAIL")} PoseLeafIsCouples: Magnetize from Couples PG path; Cross legs solo");
            poseLeafFromCouples.Clear();
        }
        var cur = PoseCurrent("AVsitter™2.1\n\n [Onlegs 4]"); bool c = cur == "Onlegs 4"; if (c) pass++; else fail++;
        lines.Add($"{(c ? "PASS" : "FAIL")} current pose parsed from the menu text: '{cur}'");
        bool sh = PoseIsCouplesNamed("COUPLES*") && PoseIsSoloMenu("SINGLE*") && !PoseIsCouplesNamed("Indeed") && !PoseIsSoloMenu("Snuggle");
        if (sh) pass++; else fail++;
        lines.Add($"{(sh ? "PASS" : "FAIL")} COUPLES*/SINGLE* classifiers");
        return $"seat pose selftest: {pass} pass, {fail} fail (offline; no dialog pressed)\n" + string.Join("\n", lines);
    }
}
