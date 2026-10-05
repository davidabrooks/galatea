// Doors.cs (2026-10-03, David): 'door [name filter | object uuid]' - find the nearest door-like prim (name or description says
// door / gate / entrance / entry; root objects AND child prims of linksets such as houses) within 10 m and touch it once,
// then report what was touched and whether it visibly moved/rotated ~2.5 s later. Also used by AutoFollow.cs when she is stuck
// (radius 4 m, at most once per 30 s). Touch only: never buys, sits, or answers a dialog.
// 2026-10-05 (David's house tour: she got stuck at a door while following): DoorUnstick - a sequence of distinct attempts,
// each logged as [door] 'attempt k <how>: <outcome>' (moved / phantom / passed / bounced back / nothing / locked message):
//   1. touch it (skipped only if phantom, or pose-open with a recent LastTouch — stale pose after auto-close
//      used to skip touch and walk into a closed door, 2026-10-05 Plane.014); open -> walk through at once
//      (many doors auto-close on a timer); already-open walk blocked -> touch then walk (not stuck-escape)
//   2. walk into it (collision / volume-detect doors open on bump)
//   3. locked/owner-only? (no movement after touch + walk, or chat/IM from the door says locked/owner/access): press an
//      'Open'/'Unlock'/'Enter' button on a dialog from the door if one came, else touch once more and wait for a menu
//   4./5. back up 1.5 m and re-approach 0.5 m left / right of the opening (slightly different angle), touch if still closed, walk
//   Failure is reported only after all distinct attempts. Never teleports or flies; never pays; only door-like dialog buttons.
// Used by follow (Follow.cs, when stalled), WalkLeg stuck recovery (walk_path / goto_avatar / nav legs) and 'door try'.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static readonly Regex DoorRx = new(@"\b(doors?|gates?|entrance|entry|doorway)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex DoorBad = new(@"door ?(mat|bell|stop|knocker|frame|sign|rug|hanger|wreath)|\b(frame|sign|vendor|rezz|mat)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    internal static bool LooksLikeDoor(string name, string desc) =>
        (DoorRx.IsMatch(name ?? "") && !DoorBad.IsMatch(name ?? "")) || (DoorRx.IsMatch(desc ?? "") && !DoorBad.IsMatch(desc ?? "") && !DoorBad.IsMatch(name ?? ""));

    static Vector3 WorldPos(Simulator sim, Primitive p, out Primitive root)
    {
        root = p;
        if (p.ParentID == 0) return p.Position;
        if (!sim.ObjectsPrimitives.TryGetValue(p.ParentID, out var parent) || parent == null) { root = null; return Vector3.Zero; } // on an avatar, or parent unknown
        root = parent;
        return parent.Position + p.Position * parent.Rotation;
    }

    // returns the report; quiet=true returns null when nothing door-like is near (stuck helper)
    static async Task<string> DoorTouch(string arg, float radius = 10f, bool quiet = false)
    {
        if (!LoggedIn) return quiet ? null : "not logged in";
        var sim = client.Network.CurrentSim; if (sim == null) return quiet ? null : "no region";
        arg = (arg ?? "").Trim().Trim('"');
        var a0 = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (a0.Length > 0 && a0[0].Equals("selftest", StringComparison.OrdinalIgnoreCase)) return DoorSelfTest();
        if (a0.Length > 0 && a0[0] is "try" or "open" or "through" or "unstick")
        {
            var filt = a0.Length > 1 ? a0[1] : null;
            if (WanderBlocksManualWalk) return "wander is running: 'wander pause' (or 'wander stop') first";
            followId = UUID.Zero;
            return StartWalk($"door {a0[0]}{(filt != null ? " " + filt : "")}", async ct =>
            {
                await EnsureStandingForWalk(ct);
                return await DoorUnstick(filt, null, 10f, "door " + a0[0], ct) ?? $"no door-like prim{(filt != null ? $" matching '{filt}'" : "")} within 10 m";
            });
        }
        var me = client.Self.SimPosition;
        var cands = new List<(Primitive p, Primitive root, Vector3 pos, float d)>();
        bool byId = UUID.TryParse(arg, out var wantId);
        foreach (var p in sim.ObjectsPrimitives.Values.ToList())
        {
            if (p == null || p.PrimData.PCode != PCode.Prim) continue;
            if (byId && p.ID != wantId) continue;
            var pos = WorldPos(sim, p, out var root); if (root == null) continue;
            var d = Vector3.Distance(pos, me);
            if (d <= (byId ? 64f : radius)) cands.Add((p, root, pos, d));
        }
        if (byId && cands.Count == 0) return $"object {wantId} is not within 64 m (or is worn by an avatar)";
        cands = cands.OrderBy(c => c.d).Take(400).ToList();
        await EnsureProperties(sim, cands.Select(c => c.p).Concat(cands.Select(c => c.root)).Distinct().ToList());
        var navDoors = NavDoorIds();   // NavPlan.cs: door panels found on a nav grid (house links are often just 'Object')
        var doors = byId ? cands : cands.Where(c => (LooksLikeDoor(c.p.Properties?.Name, c.p.Properties?.Description) || navDoors.Contains(c.p.ID))
                                   && (arg.Length == 0 || (c.p.Properties?.Name ?? "").Contains(arg, StringComparison.OrdinalIgnoreCase)
                                       || (c.p.Properties?.Description ?? "").Contains(arg, StringComparison.OrdinalIgnoreCase)
                                       || (c.root.Properties?.Name ?? "").Contains(arg, StringComparison.OrdinalIgnoreCase))).ToList();
        if (doors.Count == 0)
            return quiet ? null : $"no door-like prim (name/description: door, gate, entrance, entry){(arg.Length > 0 ? $" matching '{arg}'" : "")} within {radius:F0} m ({cands.Count} prims checked)";
        var t = doors[0];
        var rot0 = t.p.Rotation; var pos0 = t.pos;
        client.Self.Touch(t.p.LocalID);
        string what = $"'{t.p.Properties?.Name ?? "?"}'{(string.IsNullOrEmpty(t.p.Properties?.Description) ? "" : $" desc '{t.p.Properties.Description}'")} {t.p.ID}" +
                      (t.root != t.p ? $" (link of '{t.root.Properties?.Name ?? "?"}' {t.root.ID})" : "") + string.Format(CultureInfo.InvariantCulture, " at {0:F1} m", t.d);
        Log("door", "touched " + what);
        await Task.Delay(2500);
        var pos1 = WorldPos(sim, t.p, out _); var rot1 = t.p.Rotation;
        double ang = Math.Acos(Math.Min(1.0, Math.Abs(Quaternion.Dot(rot0, rot1)))) * 2 * 180 / Math.PI;
        float mv = Vector3.Distance(pos0, pos1);
        var after = ang > 5 || mv > 0.2f ? string.Format(CultureInfo.InvariantCulture, "it moved {0:F1} m / turned {1:F0}° (likely opened or closed)", mv, ang) : "no visible movement yet (may be phantom-on-touch, locked, or opens a menu)";
        var others = doors.Count > 1 ? $"; other door-like prims: {string.Join(", ", doors.Skip(1).Take(4).Select(c => $"'{c.p.Properties?.Name}' {c.d:F1} m"))}" : "";
        return $"touched {what}; {after}{others}";
    }

    // ---- robust doors (2026-10-05) ----
    internal static readonly Regex DoorLockedRx = new(@"\b(locked|is lock|owner[- ]only|only the owner|owner only|access denied|access is|not allowed|no access|not authori[sz]ed|permission denied|no permission|group only|members only|not a member|keep out|you can'?t (open|enter|use)|restricted)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex DoorButtonRx = new(@"^\s*(open( door)?|unlock|enter|let me in|toggle|door|knock)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // index of the door-opening button to press (prefers 'Open'), -1 = none (never anything else: no Buy/Pay/Close/Lock)
    internal static int PickDoorButton(IList<string> labels)
    {
        if (labels == null) return -1;
        int best = -1, bestRank = 99;
        for (int i = 0; i < labels.Count; i++)
        {
            var l = (labels[i] ?? "").Trim(); if (!DoorButtonRx.IsMatch(l)) continue;
            int rank = l.StartsWith("open", StringComparison.OrdinalIgnoreCase) ? 0 : l.StartsWith("unlock", StringComparison.OrdinalIgnoreCase) ? 1 : l.StartsWith("enter", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
            if (rank < bestRank) { best = i; bestRank = rank; }
        }
        return best;
    }
    // what the door did after a touch/bump: moved (> 0.2 m or > 5 deg), phantom (became phantom), nothing
    internal static string DoorMoveOutcome(float moved, double turned, bool phantomBefore, bool phantomNow)
        => moved > 0.2f || turned > 5 ? string.Format(CultureInfo.InvariantCulture, "moved ({0:F1} m, {1:F0} deg)", moved, turned) : phantomNow && !phantomBefore ? "phantom (walk-through)" : "nothing";
    // progress values are signed distances past the door plane along the travel direction (negative = in front of it)
    internal static string DoorWalkOutcome(float start, float best, float end)
        => end > 0.6f ? "passed through"
         : best - end > 0.3f && best - start > 0.2f ? "bounced back"
         : end - start > 0.3f ? "got closer, blocked"
         : "nothing (no progress)";
    // How long after OUR touch we trust pose-"open" (auto-close + stale ObjectUpdate otherwise lie).
    internal const double DoorOpenTrustAfterTouchS = 12;
    // Skip the opening touch only for true phantom walk-through, or pose-open right after we touched.
    // Pose-"open" alone while stuck is often a stale rotation after auto-close (2026-10-05 Plane.014).
    internal static bool DoorSkipTouchAlreadyOpen(bool poseMovedOpen, bool phantom, DateTime lastTouch, DateTime now, double trustAfterTouchS = DoorOpenTrustAfterTouchS)
    {
        if (phantom) return true;
        if (!poseMovedOpen) return false;
        if (lastTouch == DateTime.MinValue) return false;
        var age = (now - lastTouch).TotalSeconds;
        return age >= 0 && age < trustAfterTouchS;
    }
    // After "already open -> walk" that did not pass: next step must be touch (not stuck-escape / walk-again).
    internal static string DoorNextAfterFailedOpenWalk(string walkOutcome)
        => string.IsNullOrEmpty(walkOutcome) || walkOutcome.StartsWith("passed", StringComparison.OrdinalIgnoreCase) ? "none" : "touch";
    // the attempt plan (selftest documents it): kind, lateral offset
    internal static readonly (string kind, float lateral)[] DoorPlan = { ("touch", 0f), ("walk-into", 0f), ("dialog/locked", 0f), ("re-approach", 0.5f), ("re-approach", -0.5f) };

    static volatile string doorLast = "-";
    static DateTime doorRunAt = DateTime.MinValue;

    // nearest door-like prim within radius; with a goal only doors ahead of her, near the line to the goal
    static async Task<(Primitive p, Primitive root, Vector3 pos, float d)?> FindDoor(string filter, Vector3? goal, float radius)
    {
        var sim = client.Network.CurrentSim; if (sim == null) return null;
        var me = client.Self.SimPosition;
        bool byId = UUID.TryParse(filter ?? "", out var wantId);
        var cands = new List<(Primitive p, Primitive root, Vector3 pos, float d)>();
        foreach (var p in sim.ObjectsPrimitives.Values.ToList())
        {
            if (p == null || p.PrimData.PCode != PCode.Prim) continue;
            if (byId && p.ID != wantId) continue;
            var pos = WorldPos(sim, p, out var root); if (root == null) continue;
            if (MathF.Abs(pos.Z - me.Z) > 3f && !byId) continue;
            var d = Vector3.Distance(pos, me);
            if (d <= (byId ? 64f : radius)) cands.Add((p, root, pos, d));
        }
        cands = cands.OrderBy(c => c.d).Take(400).ToList();
        if (cands.Count == 0) return null;
        await EnsureProperties(sim, cands.Select(c => c.p).Concat(cands.Select(c => c.root)).Distinct().ToList());
        var navDoors = NavDoorIds();
        var doors = byId ? cands : cands.Where(c => (LooksLikeDoor(c.p.Properties?.Name, c.p.Properties?.Description) || navDoors.Contains(c.p.ID))
            && (string.IsNullOrEmpty(filter) || (c.p.Properties?.Name ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (c.p.Properties?.Description ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (c.root.Properties?.Name ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();
        if (doors.Count == 0) return null;
        if (goal is Vector3 gl)
        {
            var dir = new Vector2(gl.X - me.X, gl.Y - me.Y); if (dir.Length() > 0.1f) dir = Vector2.Normalize(dir);
            // only doors ahead of her (not behind: > -0.3 m along the travel line) and within 2.5 m of that line
            doors = doors.Where(c =>
            {
                float along = (c.pos.X - me.X) * dir.X + (c.pos.Y - me.Y) * dir.Y, across = MathF.Abs((c.pos.X - me.X) * -dir.Y + (c.pos.Y - me.Y) * dir.X);
                return along > -0.3f && across <= 2.5f;
            }).ToList();
            if (doors.Count == 0) return null;
        }
        return doors.OrderBy(c => c.d).First();
    }

    // returns null when no door-like prim is near; otherwise "door '<name>': through|FAILED after k attempts: ..."
    static async Task<string> DoorUnstick(string filter, Vector3? goal, float radius, string why, CancellationToken ct)
    {
        if (!LoggedIn || client.Self.SittingOn != 0) return null;
        var found = await FindDoor(filter, goal, radius); if (found == null) return null;
        var (dp, droot, dpos, _) = found.Value;
        var sim = client.Network.CurrentSim;
        string dname = dp.Properties?.Name ?? "?", rname = droot.Properties?.Name ?? "?";
        string what = $"'{dname}' {dp.ID}" + (droot != dp ? $" (link of '{rname}')" : "");
        var navDoor = NavGrids().SelectMany(g => g.Doors).FirstOrDefault(nd => nd.Id == dp.ID);
        var me0 = client.Self.SimPosition;
        // doorway centre: a nav door's opening, else the panel where it stands now (closed pose)
        var c0 = navDoor != null ? new Vector3(navDoor.OpenCenter.X, navDoor.OpenCenter.Y, me0.Z) : new Vector3(dpos.X, dpos.Y, me0.Z);
        var g0 = goal ?? (c0 + Norm2(c0 - me0) * 2f);
        var n = Norm2(g0 - me0); if (Dot2(c0 - me0, n) < 0) n = Norm2(c0 - me0);   // travel direction through the doorway
        var side = new Vector3(-n.Y, n.X, 0);
        var pos0 = dpos; var rot0 = dp.Rotation; bool ph0 = (dp.Flags & PrimFlags.Phantom) != 0;
        float Prog() => Dot2(client.Self.SimPosition - c0, n);
        (float mv, double ang, bool ph) DoorNow()
        {
            var wp = WorldPos(sim, dp, out _); float mv = Vector3.Distance(wp, pos0);
            double ang = Math.Acos(Math.Min(1.0, Math.Abs(Quaternion.Dot(rot0, dp.Rotation)))) * 2 * 180 / Math.PI;
            return (mv, ang, (dp.Flags & PrimFlags.Phantom) != 0);
        }
        bool OpenNow()
        {
            if (navDoor != null) { var st = DoorState(navDoor); if (st.known) return st.open; }
            var dn = DoorNow(); return dn.mv > 0.2f || dn.ang > 5 || dn.ph;
        }
        // Skip touch only when passable for sure (phantom, or we just opened it). Stale pose-"open" must not skip.
        bool SkipTouchAlreadyOpen()
        {
            bool ph = (dp.Flags & PrimFlags.Phantom) != 0;
            if (navDoor != null)
            {
                var st = DoorState(navDoor);
                if (st.known) return DoorSkipTouchAlreadyOpen(poseMovedOpen: st.open && !ph, phantom: ph, navDoor.LastTouch, DateTime.Now);
            }
            return ph;
        }
        // messages / dialogs from the door while we work
        var msgs = new ConcurrentQueue<string>(); ScriptDialogEventArgs dlg = null;
        bool FromDoor(UUID id, string name) => id == dp.ID || id == droot.ID || (!string.IsNullOrEmpty(name) && (name == dname || name == rname) && name != "Object");
        EventHandler<ChatEventArgs> onChat = (s, e) => { if (e.SourceType == ChatSourceType.Object && FromDoor(e.SourceID, e.FromName) && !string.IsNullOrWhiteSpace(e.Message)) msgs.Enqueue(e.Message); };
        EventHandler<InstantMessageEventArgs> onIm = (s, e) => { if (e.IM.Dialog == InstantMessageDialog.MessageFromObject && FromDoor(UUID.Zero, e.IM.FromAgentName)) msgs.Enqueue(e.IM.Message); };
        EventHandler<ScriptDialogEventArgs> onDlg = (s, e) => { if (FromDoor(e.ObjectID, e.ObjectName)) { dlg = e; msgs.Enqueue("[dialog] " + e.Message + " [" + string.Join("|", e.ButtonLabels ?? new()) + "]"); } };
        EventHandler<AlertMessageEventArgs> onAlert = (s, e) => { if (DoorLockedRx.IsMatch(e.Message ?? "")) msgs.Enqueue("[alert] " + e.Message); };
        client.Self.ChatFromSimulator += onChat; client.Self.IM += onIm; client.Self.ScriptDialog += onDlg; client.Self.AlertMessage += onAlert;
        var log = new List<string>(); int k = 0; bool through = false; bool lockedSeen = false;
        void Note(string how, string outcome)
        {
            k++; var l = $"attempt {k} {how}: {outcome}"; log.Add(l);
            Log("door", $"[{why}] {what}: {l}");
        }
        string Heard() { var l = new List<string>(); while (msgs.TryDequeue(out var m)) { l.Add(m.Length > 80 ? m[..80] + "…" : m); if (DoorLockedRx.IsMatch(m)) lockedSeen = true; } return l.Count == 0 ? "" : "; heard: " + string.Join(" / ", l); }
        async Task<string> Touch()
        {
            var before = DoorNow();
            client.Self.Touch(dp.LocalID); if (navDoor != null) navDoor.LastTouch = DateTime.Now;
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(300, ct); var dn = DoorNow();
                if (MathF.Abs(dn.mv - before.mv) > 0.2f || Math.Abs(dn.ang - before.ang) > 5 || dn.ph != before.ph) { await Task.Delay(300, ct); break; }
            }
            var now = DoorNow();
            return DoorMoveOutcome(MathF.Abs(now.mv - before.mv), Math.Abs(now.ang - before.ang), before.ph, now.ph);
        }
        async Task<(string outcome, bool doorMoved)> Walk(Vector3 target, double secs)
        {
            var before = DoorNow(); float start = Prog(), best = start;
            var tgt = new Vector3(target.X, target.Y, client.Self.SimPosition.Z);
            AutoPilotTo(tgt); client.Self.Movement.TurnToward(tgt);
            var t0 = DateTime.Now; var lastMove = DateTime.Now; var lastP = client.Self.SimPosition;
            while ((DateTime.Now - t0).TotalSeconds < secs)
            {
                await Task.Delay(300, ct);
                var p = Prog(); best = MathF.Max(best, p);
                if (p > 0.9f) break;
                if (HDist(client.Self.SimPosition, lastP) > 0.15f) { lastP = client.Self.SimPosition; lastMove = DateTime.Now; }
                else if ((DateTime.Now - lastMove).TotalSeconds > 1.8) break;
            }
            client.Self.AutoPilotCancel();
            await Task.Delay(300, ct);
            var after = DoorNow();
            bool moved = MathF.Abs(after.mv - before.mv) > 0.2f || Math.Abs(after.ang - before.ang) > 5 || after.ph != before.ph;
            return (DoorWalkOutcome(start, best, Prog()), moved);
        }
        var throughPt = c0 + n * 1.6f;
        try
        {
            doorRunAt = DateTime.Now;
            Log("door", $"[{why}] stuck near {what}, {HDist(client.Self.SimPosition, c0):F1} m from the opening (progress {Prog():+0.0;-0.0} m): trying a door sequence");
            // 1. touch (unless phantom / just-opened), walk through at once
            if (SkipTouchAlreadyOpen())
            {
                var (w, _) = await Walk(throughPt, 5);
                Note("door already open/phantom -> walk through", w + Heard());
                if (w.StartsWith("passed")) through = true;
                else if (DoorNextAfterFailedOpenWalk(w) == "touch")
                {
                    // Stale open / auto-closed: touch next — never stuck-escape or walk-again first (2026-10-05)
                    var t = await Touch();
                    (w, _) = await Walk(throughPt, 5);
                    Note("open-walk blocked -> touch then walk", $"door {t}, {w}{Heard()}");
                    through = w.StartsWith("passed");
                }
            }
            else
            {
                var t = await Touch();
                if (t == "nothing") { Note("touch", t + Heard()); }
                else
                {
                    var (w, _) = await Walk(throughPt, 5);
                    Note("touch -> walk through", $"door {t}, {w}{Heard()}");
                    through = w.StartsWith("passed");
                    if (!through && OpenNow())
                    {
                        await StuckEscape(throughPt, "door open but stuck", ct);
                        (w, _) = await Walk(throughPt, 5);
                        Note("door open but stuck -> stuck-escape, walk again", w + Heard());
                        through = w.StartsWith("passed");
                    }
                }
            }
            // 2. walk into it (collision doors)
            if (!through)
            {
                var (w, moved) = await Walk(throughPt, 4);
                Note("walk into it", $"{w}{(moved ? ", door moved on bump" : "")}{Heard()}");
                through = w.StartsWith("passed");
                if (!through && moved && OpenNow()) { (w, _) = await Walk(throughPt, 5); Note("bump opened it -> walk through", w + Heard()); through = w.StartsWith("passed"); }
            }
            // 3. locked / owner-only? try a dialog 'Open' button, else touch once more and wait for a menu
            if (!through)
            {
                Heard();
                if (dlg == null && !OpenNow()) { var t = await Touch(); await Task.Delay(1200, ct); Note("touch again (menu? locked?)", t + (dlg != null ? ", a dialog came" : "") + Heard()); if (t != "nothing" && !through) { var (w, _) = await Walk(throughPt, 5); Note("walk through", w + Heard()); through = w.StartsWith("passed"); } }
                if (!through && dlg != null)
                {
                    var d = dlg; int bi = PickDoorButton(d.ButtonLabels);
                    if (bi >= 0)
                    {
                        client.Self.ReplyToScriptDialog(d.Channel, bi, d.ButtonLabels[bi], d.ObjectID);
                        await Task.Delay(1500, ct);
                        var (w, _) = await Walk(throughPt, 5);
                        Note($"dialog '{d.ButtonLabels[bi]}' -> walk through", (OpenNow() ? "door open, " : "no movement, ") + w + Heard());
                        through = w.StartsWith("passed");
                    }
                    else Note("dialog", $"no Open/Unlock/Enter button (buttons: {string.Join(" | ", d.ButtonLabels ?? new())}); not pressing anything");
                }
                if (!through && lockedSeen) Log("door", $"[{why}] {what}: says LOCKED / owner-only");
            }
            // 4./5. back up 1.5 m, re-approach 0.5 m to either side (slightly different angle), touch if closed, walk
            foreach (var lat in new[] { 0.5f, -0.5f })
            {
                if (through) break;
                ct.ThrowIfCancellationRequested();
                var me = client.Self.SimPosition;
                var back = me - n * 1.5f + side * lat;
                AutoPilotTo(back);
                for (int i = 0; i < 10 && HDist(client.Self.SimPosition, back) > 0.4f; i++) await Task.Delay(300, ct);
                client.Self.AutoPilotCancel();
                string t = OpenNow() ? "already open" : await Touch();
                var (w, moved) = await Walk(throughPt - side * (lat * 0.6f), 6);   // aim across the opening: an oblique line
                Note($"back up 1.5 m, re-approach {(lat > 0 ? "left" : "right")} 0.5 m", $"touch: {t}; walk: {w}{(moved ? ", door moved" : "")}{Heard()}");
                through = w.StartsWith("passed");
            }
        }
        finally
        {
            client.Self.ChatFromSimulator -= onChat; client.Self.IM -= onIm; client.Self.ScriptDialog -= onDlg; client.Self.AlertMessage -= onAlert;
        }
        var res = $"door {what}: {(through ? "through" : "FAILED")} after {k} attempt(s){(lockedSeen && !through ? " (it says locked / owner-only)" : "")}: {string.Join("; ", log)}";
        doorLast = $"{DateTime.Now:HH:mm:ss} {res}";
        Log("door", $"[{why}] {(through ? "through" : "GAVE UP")}: {what} after {k} attempts at {V(client.Self.SimPosition)}");
        return res;
    }
    static Vector3 Norm2(Vector3 v) { var h = new Vector3(v.X, v.Y, 0); return h.Length() < 0.01f ? Vector3.UnitX : Vector3.Normalize(h); }
    static float Dot2(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y;

    internal static string DoorSelfTest()
    {
        var sb = new StringBuilder(); int pass = 0, fail = 0; void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        C(PickDoorButton(new[] { "Close", "Open", "Lock" }) == 1, "dialog: picks 'Open'");
        C(PickDoorButton(new[] { "Lock", "Unlock" }) == 1, "dialog: 'Unlock' when no Open");
        C(PickDoorButton(new[] { "Buy", "Pay", "Close", "Lock" }) == -1, "dialog: never Buy/Pay/Close/Lock");
        C(PickDoorButton(new[] { "Enter", " open " }) == 1, "dialog: prefers Open over Enter");
        C(DoorLockedRx.IsMatch("This door is locked.") && DoorLockedRx.IsMatch("Access denied") && DoorLockedRx.IsMatch("Sorry, owner only!") && DoorLockedRx.IsMatch("Group only door"), "locked messages detected");
        C(!DoorLockedRx.IsMatch("Door opening") && !DoorLockedRx.IsMatch("Welcome home"), "ordinary door chat is not 'locked'");
        C(DoorMoveOutcome(0f, 85, false, false).StartsWith("moved"), "swing 85 deg = moved");
        C(DoorMoveOutcome(1.1f, 0, false, false).StartsWith("moved"), "slide 1.1 m = moved");
        C(DoorMoveOutcome(0f, 0, false, true).StartsWith("phantom"), "became phantom");
        C(DoorMoveOutcome(0.05f, 2, false, false) == "nothing", "no movement = nothing");
        C(DoorWalkOutcome(-1.5f, 1.0f, 1.0f) == "passed through", "walk: passed");
        C(DoorWalkOutcome(-1.5f, -0.3f, -0.9f) == "bounced back", "walk: bounced back");
        C(DoorWalkOutcome(-1.5f, -0.5f, -0.5f) == "got closer, blocked", "walk: blocked at the panel");
        C(DoorWalkOutcome(-0.4f, -0.35f, -0.4f).StartsWith("nothing"), "walk: no progress");
        var t0 = new DateTime(2026, 10, 5, 15, 22, 0);
        C(!DoorSkipTouchAlreadyOpen(true, false, DateTime.MinValue, t0), "stale pose-open, never touched: do NOT skip touch");
        C(!DoorSkipTouchAlreadyOpen(true, false, t0.AddMinutes(-30), t0), "pose-open, LastTouch 30 min ago: do NOT skip (auto-close / stale)");
        C(DoorSkipTouchAlreadyOpen(true, false, t0.AddSeconds(-5), t0), "pose-open, touched 5 s ago: skip touch");
        C(DoorSkipTouchAlreadyOpen(false, true, DateTime.MinValue, t0), "phantom: skip touch");
        C(!DoorSkipTouchAlreadyOpen(false, false, t0, t0), "closed, not phantom: touch");
        C(DoorNextAfterFailedOpenWalk("got closer, blocked") == "touch", "blocked open-walk -> touch next");
        C(DoorNextAfterFailedOpenWalk("bounced back") == "touch", "bounced open-walk -> touch next");
        C(DoorNextAfterFailedOpenWalk("nothing (no progress)") == "touch", "no-progress open-walk -> touch next");
        C(DoorNextAfterFailedOpenWalk("passed through") == "none", "passed -> no extra touch");
        C(DoorPlan.Length >= 5 && DoorPlan.Select(p => p.kind).Distinct().Count() >= 4 && DoorPlan.Any(p => p.lateral > 0) && DoorPlan.Any(p => p.lateral < 0), "plan: >= 5 distinct attempts incl. +-0.5 m re-approach");
        C(LooksLikeDoor("Front Door", "") && LooksLikeDoor("Garden gate", "") && !LooksLikeDoor("Door mat", "") && !LooksLikeDoor("Door frame", ""), "door names (not mats/frames)");
        return $"door selftest: {pass} PASS, {fail} FAIL\n" + sb.ToString().TrimEnd();
    }
}
