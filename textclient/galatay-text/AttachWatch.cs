// AttachWatch (added 2026-09-25): who animates Galatay, and control over worn attachments.
// - logs every own AvatarAnimation change (start / restart / stop) with its source object (AnimationSourceList)
// - "worn" lists attachments (incl. HUDs) with item ids, script flag and the animations each is playing
// - "detach <item>" / "attach <item>" (reversible; recorded in detached-attachments.log)
// - detach also removes the item's Current Outfit Folder (COF) link(s), like "wear remove" (2026-10-05). Seat-off /
//   attach-block temporary detaches and AO-restore detach+re-attach KEEP the COF link so the item is re-worn on stand /
//   after restore. Unexpected self-detach (e.g. unpacker llDetachFromAvatar) drops the stale COF link and logs it.
// - "attach move <item|obj|name> <dx> <dy> <dz> [hudok]" / "attach pos <...>" : nudge root prim attachment-local position (2026-10-05)
// - attach-block.txt: items kept OFF while seated (detached before/after every sit, again if re-worn) and re-attached when standing
// - anim-block.txt: animation ids stopped whenever they start while seated
// - pose keeper: while seated, if a non-seat animation (re)starts after the seat pose, the seat pose is re-asserted (only anims the seat still sources; drops stale solo/couples copies)
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using LibreMetaverse;
using LibreMetaverse.Packets;

namespace GalatayText;

public static partial class Program
{
    static readonly string AttachBlockFile = Env("GT_ATTACH_BLOCK", "/home/box/viewers/textclient/attach-block.txt");
    static readonly string AnimBlockFile = Env("GT_ANIM_BLOCK", "/home/box/viewers/textclient/anim-block.txt");
    static readonly string DetachLog = Env("GT_DETACH_LOG", "/workspace/secondlife/detached-attachments.log");
    static bool poseKeeper = Env("GT_POSE_KEEPER", "1") != "0";
    static DateTime animLogUntil = DateTime.MinValue;
    static Dictionary<UUID, (int seq, UUID src)> ownAnims = new();
    static readonly object animLock = new();
    static readonly ConcurrentDictionary<UUID, UUID> keptPose = new(); // pose anims we re-asserted ourselves (stop them on stand)
    static DateTime lastKeep = DateTime.MinValue;
    static readonly ConcurrentDictionary<UUID, DateTime> animSince = new();
    static readonly ConcurrentDictionary<UUID, bool> longLived = new(); // anims seen playing > 20 s
    static readonly ConcurrentDictionary<UUID, Queue<DateTime>> restartTimes = new(); // periodic restarters (hand/face loops) are ignored by the pose keeper
    static bool Periodic(UUID id) { if (!restartTimes.TryGetValue(id, out var q)) return false; lock (q) { while (q.Count > 0 && (DateTime.Now - q.Peek()).TotalSeconds > 60) q.Dequeue(); return q.Count >= 3; } }

    static HashSet<UUID> ReadIdFile(string path)
    {
        var s = new HashSet<UUID>();
        try { if (File.Exists(path)) foreach (var l in File.ReadAllLines(path)) { var t = l.Split('#')[0].Trim(); if (UUID.TryParse(t, out var u)) s.Add(u); } } catch { }
        return s;
    }

    static void HookAttachWatch()
    {
        client.Network.RegisterCallback(PacketType.AvatarAnimation, OnAvatarAnimation);
        HookAttachTrack(); // raw-packet attachment table for worn all / worn raw (AttachTrack.cs, read-only)
    }

    static UUID AttachItemId(Primitive p)
    {
        try { var nv = p.NameValues?.FirstOrDefault(n => n.Name == "AttachItemID"); if (nv != null && UUID.TryParse(nv.Value.Value?.ToString(), out var u)) return u; } catch { }
        return UUID.Zero;
    }

    static List<Primitive> WornPrims()
    {
        var sim = client?.Network?.CurrentSim; var me = client?.Self?.LocalID ?? 0;
        if (sim == null || me == 0) return new();
        return sim.ObjectsPrimitives.Values.Where(p => p != null && p.ParentID == me).ToList();
    }

    static string SrcDesc(UUID src)
    {
        if (src == UUID.Zero) return "none";
        if (src == client.Self.AgentID) return "self/sim (default or override state)";
        var sim = client.Network.CurrentSim;
        var p = sim?.ObjectsPrimitives.Values.FirstOrDefault(x => x != null && x.ID == src);
        if (p == null) return $"object {src} (not in view)";
        if (p.Properties == null) _ = EnsureProperties(sim, new() { p });
        var name = p.Properties?.Name ?? "?";
        var seat = client.Self.SittingOn;
        if (p.ParentID == client.Self.LocalID) return $"attachment '{name}' @{p.PrimData.AttachmentPoint} item {AttachItemId(p)}";
        if (seat != 0 && (p.LocalID == seat || p.ParentID == seat || (sim.ObjectsPrimitives.TryGetValue(seat, out var sp) && sp != null && (sp.ParentID == p.LocalID || (sp.ParentID != 0 && sp.ParentID == p.ParentID))))) return $"seat '{name}'";
        return $"object '{name}' {src}";
    }
    static bool IsSeatSource(UUID src)
    {
        var seat = client.Self.SittingOn; if (seat == 0 || src == UUID.Zero || src == client.Self.AgentID) return false;
        var sim = client.Network.CurrentSim; if (sim == null) return false;
        var p = sim.ObjectsPrimitives.Values.FirstOrDefault(x => x != null && x.ID == src); if (p == null) return false;
        if (p.LocalID == seat || p.ParentID == seat) return true;
        return sim.ObjectsPrimitives.TryGetValue(seat, out var sp) && sp != null && (sp.ParentID == p.LocalID || (sp.ParentID != 0 && sp.ParentID == p.ParentID));
    }

    static void OnAvatarAnimation(object sender, PacketReceivedEventArgs e)
    {
        try
        {
            var pk = (AvatarAnimationPacket)e.Packet;
            if (pk.Sender.ID != client.Self.AgentID) return;
            var now = new Dictionary<UUID, (int, UUID)>();
            for (int i = 0; i < pk.AnimationList.Length; i++)
            {
                var src = i < pk.AnimationSourceList.Length ? pk.AnimationSourceList[i].ObjectID : UUID.Zero;
                now[pk.AnimationList[i].AnimID] = (pk.AnimationList[i].AnimSequenceID, src);
            }
            Dictionary<UUID, (int seq, UUID src)> prev;
            lock (animLock) { prev = ownAnims; ownAnims = now; }
            var started = now.Where(kv => !prev.TryGetValue(kv.Key, out var o) || o.seq != kv.Value.Item1).ToList();
            var stopped = prev.Keys.Where(k => !now.ContainsKey(k)).ToList();
            bool log = DateTime.Now < animLogUntil;
            foreach (var kv in started) if (!prev.ContainsKey(kv.Key)) animSince[kv.Key] = DateTime.Now;
            foreach (var kv in started) { var q = restartTimes.GetOrAdd(kv.Key, _ => new Queue<DateTime>()); lock (q) q.Enqueue(DateTime.Now); }
            foreach (var k in stopped) if (animSince.TryRemove(k, out var t0) && (DateTime.Now - t0).TotalSeconds > 20) longLived[k] = true;
            foreach (var kv in now) if (animSince.TryGetValue(kv.Key, out var t1) && (DateTime.Now - t1).TotalSeconds > 20) longLived[kv.Key] = true;
            foreach (var kv in started)
                if (log) Log("anim", $"{(prev.ContainsKey(kv.Key) ? "RESTART" : "start")} {AnimName(kv.Key)} seq {kv.Value.Item1} from {SrcDesc(kv.Value.Item2)}");
            foreach (var k in stopped)
                if (log) Log("anim", $"stop {AnimName(k)} (was from {SrcDesc(prev[k].src)})");
            if (client.Self.SittingOn != 0 && started.Count > 0) _ = Task.Run(() => SeatedAnimGuard(started.Select(kv => (kv.Key, kv.Value.Item2, prev.ContainsKey(kv.Key))).ToList(), now));
            if (client.Self.SittingOn == 0)
            {
                NotePoseSeatShared(null); // alone/shared tracking ends when we stand
                if (!keptPose.IsEmpty)
                    foreach (var id in keptPose.Keys.ToList()) { client.Self.AnimationStop(id, true); keptPose.TryRemove(id, out _); Log("height", $"pose keeper: stopped our copy of {AnimName(id)} after standing"); }
            }
        }
        catch (Exception ex) { Log("anim", "watch error: " + ex.GetBaseException().Message); }
    }

    // while seated: stop blocked anims; built-in sit anims from non-seat sources once a seat pose runs; re-assert the seat pose.
    // Never re-assert a kept copy the seat is no longer playing (solo<->couples switch when a partner sits/stands).
    static async Task SeatedAnimGuard(List<(UUID id, UUID src, bool restart)> started, Dictionary<UUID, (int seq, UUID src)> now)
    {
        var block = ReadIdFile(AnimBlockFile);
        // Occupancy flip (partner sat/stood): drop kept copies so we do not re-apply the old mode's anim.
        try
        {
            var sim = client.Network.CurrentSim;
            if (sim != null && client.Self.SittingOn != 0 && sim.ObjectsPrimitives.TryGetValue(client.Self.SittingOn, out var seatPrim) && seatPrim != null)
            {
                var root = seatPrim;
                if (seatPrim.ParentID != 0 && sim.ObjectsPrimitives.TryGetValue(seatPrim.ParentID, out var r) && r != null) root = r;
                NotePoseSeatShared(SeatHasOtherSitters(root));
            }
        }
        catch { }
        // Only animations the seat is currently sourcing — not orphaned keptPose copies from a previous pose mode.
        var seatAnims = now.Where(kv => IsSeatSource(kv.Value.src)).Select(kv => kv.Key).ToList();
        foreach (var id in keptPose.Keys.ToList())
        {
            if (seatAnims.Contains(id)) continue;
            try { client.Self.AnimationStop(id, true); } catch { }
            keptPose.TryRemove(id, out _);
            if (DateTime.Now < animLogUntil) Log("height", $"pose keeper: dropped stale copy of {AnimName(id)} (seat no longer playing it)");
        }
        bool interloper = false;
        foreach (var (id, src, restart) in started)
        {
            if (block.Contains(id)) { client.Self.AnimationStop(id, true); Log("height", $"seated guard: stopped blocked anim {AnimName(id)} from {SrcDesc(src)}"); continue; }
            if (seatAnims.Count > 0 && SitAnims.Contains(id) && !IsSeatSource(src) && id != Animations.SIT_GROUND_staticRAINED)
            { client.Self.AnimationStop(id, true); Log("height", $"seated guard: stopped built-in {AnimName(id)} from {SrcDesc(src)} (seat pose is playing)"); continue; }
            if (!IsSeatSource(src) && src != client.Self.AgentID && !keptPose.ContainsKey(id) && (restart || longLived.ContainsKey(id)) && !Periodic(id)) interloper = true;
        }
        if (poseKeeper && interloper && seatAnims.Count > 0 && (DateTime.Now - lastKeep).TotalSeconds > 2)
        {
            lastKeep = DateTime.Now;
            await Task.Delay(300);
            foreach (var id in seatAnims) { client.Self.AnimationStart(id, true); keptPose[id] = id; }
            if (DateTime.Now < animLogUntil) Log("height", $"pose keeper: re-asserted seat pose {string.Join(",", seatAnims.Select(AnimName))} after a non-seat animation started");
        }
    }

    static async Task<string> WornList(bool scripts)
    {
        var sim = Sim; var prims = WornPrims();
        await EnsureProperties(sim, prims);
        Dictionary<UUID, (int seq, UUID src)> cur; lock (animLock) cur = ownAnims;
        var block = SeatOffItems().Keys.ToHashSet();
        var sb = new StringBuilder($"{prims.Count} attachments (incl. HUDs); seat-off rule: {seatAttachState}\n");
        foreach (var p in prims.OrderBy(p => p.PrimData.AttachmentPoint.ToString()))
        {
            var item = AttachItemId(p);
            var anims = cur.Where(kv => kv.Value.src == p.ID).Select(kv => AnimName(kv.Key)).ToList();
            sb.AppendLine($"  {p.PrimData.AttachmentPoint,-14} '{p.Properties?.Name ?? "?"}' item {item} obj {p.ID} {(((p.Flags & PrimFlags.Scripted) != 0) ? "SCRIPTED" : "no-scripts")}{(block.Contains(item) ? " SEAT-OFF" : "")}{(anims.Count > 0 ? " animating: " + string.Join(",", anims) : "")}");
            if (scripts)
            {
                try
                {
                    using var t = new CancellationTokenSource(15000);
                    var inv = await client.Inventory.GetTaskInventoryAsync(p.ID, p.LocalID, sim, t.Token);
                    var names = inv?.OfType<InventoryItem>().Where(i => i.AssetType is AssetType.LSLText or AssetType.Animation).Select(i => $"{i.AssetType}:{i.Name}").ToList() ?? new();
                    if (names.Count > 0) sb.AppendLine("      contents: " + string.Join(" | ", names.Take(40)) + (names.Count > 40 ? $" (+{names.Count - 40})" : ""));
                }
                catch (Exception ex) { sb.AppendLine("      contents: n/a (" + ex.GetBaseException().GetType().Name + ")"); }
            }
        }
        var other = cur.Where(kv => !prims.Any(p => p.ID == kv.Value.src)).Select(kv => $"{AnimName(kv.Key)} seq {kv.Value.seq} from {SrcDesc(kv.Value.src)}");
        sb.Append("other animations: " + string.Join("; ", other));
        return sb.ToString();
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<UUID, DateTime> lastDetachSent = new();
    // recent DetachItem calls: unexpected KillObject must not double-remove COF, and seat-off must keep the link
    static readonly ConcurrentDictionary<UUID, (DateTime t, bool keepCof, string why)> detachIntent = new();

    // Pure: which DetachItem "why" strings KEEP the COF link (temporary / will re-attach). Offline selftest uses this.
    internal static bool KeepCofForDetachWhy(string why)
    {
        if (string.IsNullOrEmpty(why)) return false;
        if (why.Equals("before sit", StringComparison.Ordinal) || why.StartsWith("seated", StringComparison.Ordinal)) return true;
        // AO restore historically detach+re-attach; keep COF so login/stand still has the HUD
        if (why.StartsWith("ao restore", StringComparison.OrdinalIgnoreCase)) return true;
        if (why.StartsWith("ao ", StringComparison.OrdinalIgnoreCase) && why.Contains("restore", StringComparison.OrdinalIgnoreCase)) return true;
        return false; // "command", "wear remove", "unexpected self-detach", ...
    }

    // Pure: whether an attachment KillObject that we did not initiate should drop the stale COF link.
    internal static bool ShouldRemoveCofOnUnexpectedDetach(bool avatarKilledInSamePacket, bool isSeatOffItem, bool currentlySeated,
        bool hadRecentDetachIntent, bool regionOrLoginGrace, int ownAttachmentsKilledInPacket)
    {
        if (avatarKilledInSamePacket) return false; // teleport / region exit often kills the avatar + attachments together
        if (regionOrLoginGrace) return false;
        if (ownAttachmentsKilledInPacket >= 5) return false; // mass kill without avatar line still looks like a region leave
        if (hadRecentDetachIntent) return false; // DetachItem already decided keep vs remove
        if (isSeatOffItem && currentlySeated) return false; // attach-block: keep link for stand re-wear
        return true;
    }

    static string DetachItem(UUID item, string why)
    {
        var p = WornPrims().FirstOrDefault(x => AttachItemId(x) == item);
        var desc = p == null ? "(not currently worn)" : $"'{p.Properties?.Name ?? "?"}' @{p.PrimData.AttachmentPoint} obj {p.ID}";
        bool keepCof = KeepCofForDetachWhy(why);
        detachIntent[item] = (DateTime.Now, keepCof, why);
        try { File.AppendAllText(DetachLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} detach item {item} {desc} point={(p == null ? "?" : ((int)p.PrimData.AttachmentPoint).ToString())} ({why}){(keepCof ? " [COF kept]" : " [COF remove]")}; re-attach: text-galatay.sh cmd \"attach {item}\"\n"); } catch { }
        client.Appearance.Detach(item);
        lastDetachSent[item] = DateTime.Now; // worn all: a chat before this no longer proves the item is attached
        Log("height", $"detached item {item} {desc} ({why}){(keepCof ? "; COF link kept" : "; COF link will be removed")}");
        return $"detach sent for item {item} {desc}" + (keepCof ? " (COF link kept)" : "");
    }

    // detach command / wear remove: send Detach then remove COF link(s) unless KeepCofForDetachWhy.
    static async Task<string> DetachItemAsync(UUID item, string why)
    {
        var msg = DetachItem(item, why);
        if (KeepCofForDetachWhy(why)) return msg;
        using var t = new CancellationTokenSource(30000);
        var cof = await RemoveCofLinksForItem(item, why, t.Token);
        return msg + "; " + cof;
    }

    // KillObject of one of our attachments: drop stale COF when an unpacker (etc.) detaches itself.
    static void NoteOwnAttachmentKilled(UUID item, uint local, bool avatarKilledInSamePacket, int ownAttachmentsKilledInPacket)
    {
        if (item == UUID.Zero) return;
        bool recent = detachIntent.TryGetValue(item, out var di) && (DateTime.Now - di.t).TotalSeconds < 60;
        bool seatOff = false; try { seatOff = SeatOffItems().ContainsKey(item); } catch { }
        bool seated = false; try { seated = client?.Self?.SittingOn != 0; } catch { }
        bool grace = (attTrackStart != DateTime.MinValue && (DateTime.Now - attTrackStart).TotalSeconds < 45)
                     || (lastSimChange != DateTime.MinValue && (DateTime.Now - lastSimChange).TotalSeconds < 45);
        if (!ShouldRemoveCofOnUnexpectedDetach(avatarKilledInSamePacket, seatOff, seated, recent, grace, ownAttachmentsKilledInPacket))
            return;
        Log("wear", $"unexpected self-detach of item {item} local {local}; removing stale COF link");
        _ = Task.Run(async () =>
        {
            try
            {
                using var t = new CancellationTokenSource(30000);
                var r = await RemoveCofLinksForItem(item, "unexpected self-detach", t.Token);
                Log("wear", $"unexpected self-detach COF cleanup for {item}: {r}");
            }
            catch (Exception ex) { Log("wear", $"unexpected self-detach COF cleanup for {item} FAILED: {ex.GetBaseException().Message}"); }
        });
    }

    static async Task<string> AttachItem(UUID item, string pointArg)
    {
        if (item == RetiredAwpAo) { Log("height", "attach REFUSED: retired AWP AO (Martha is the AO now)"); return "refused: the retired AWP AO HUD is never re-attached (Martha is the AO)"; }
        // after a teleport the client may simply not have received the attachment yet: look again before adding a second copy
        if (await FoundAfterRecover(item)) { Log("height", $"attach skipped: item {item} is already worn (found after re-requesting objects)"); return $"already worn: item {item} (found after re-requesting objects; nothing attached)"; }
        using var t = new CancellationTokenSource(20000);
        var inv = await client.Inventory.FetchItemAsync(item, client.Self.AgentID, t.Token);
        if (inv == null) return "item not returned by inventory fetch within 20 s (not found or inventory not loaded yet)";
        var pt = AttachmentPoint.Default;
        if (!string.IsNullOrEmpty(pointArg) && int.TryParse(pointArg, out var n)) pt = (AttachmentPoint)n;
        client.Appearance.Attach(inv, pt, false);
        Log("height", $"attach sent for item {item} '{inv.Name}' point {pt}");
        return $"attach sent for '{inv.Name}' at {pt} (add, not replace)";
    }

    // attach-block.txt = attachments that must be OFF while seated and are re-worn when standing
    // line format: <item uuid> [attach point number]   # comment
    static Dictionary<UUID, int> SeatOffItems()
    {
        var d = new Dictionary<UUID, int>();
        try
        {
            if (File.Exists(AttachBlockFile))
                foreach (var l in File.ReadAllLines(AttachBlockFile))
                {
                    var f = l.Split('#')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length >= 1 && UUID.TryParse(f[0], out var u) && u != RetiredAwpAo) d[u] = f.Length > 1 && int.TryParse(f[1], out var pt) ? pt : 0;
                }
        }
        catch { }
        return d;
    }
    static Dictionary<UUID, Primitive> WornByItem()
    {
        var d = new Dictionary<UUID, Primitive>();
        foreach (var p in WornPrims()) { var it = AttachItemId(p); if (it != UUID.Zero) d[it] = p; }
        return d;
    }

    // sit command: take the seat-off attachments off BEFORE sitting (so no AO sit override ever starts)
    static async Task DetachSeatOffBeforeSit()
    {
        var items = SeatOffItems(); if (items.Count == 0) return;
        var worn = WornByItem(); bool any = false;
        foreach (var it in items.Keys) if (worn.TryGetValue(it, out var p)) { if (p.Properties == null) await EnsureProperties(client.Network.CurrentSim, new() { p }); DetachItem(it, "before sit"); lastSeatOffDetach[it] = DateTime.Now; any = true; }
        if (!any) return;
        for (int i = 0; i < 12 && items.Keys.Any(k => WornByItem().ContainsKey(k)); i++) await Task.Delay(250);
        await Task.Delay(500); // let the sim drop the AO's animation-state overrides
    }

    static readonly ConcurrentDictionary<UUID, DateTime> lastSeatOffDetach = new();
    static string seatAttachState = "starting";

    // every second: seated -> seat-off attachments must be off (detach again if they re-wear);
    // standing -> re-attach them once after standing (or at login), then verify no sit animation is stuck
    static async Task SeatAttachLoop()
    {
        uint lastSeat = uint.MaxValue; // unknown (login)
        DateTime standSince = DateTime.MinValue, attachAt = DateTime.MinValue, loginAt = DateTime.Now;
        int attachTries = 0; bool standChecked = true;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                if (LoggedIn && client.Network.CurrentSim != null && (DateTime.Now - loginAt).TotalSeconds > 6)
                {
                    var seat = client.Self.SittingOn;
                    var items = SeatOffItems();
                    if (items.Count > 0)
                    {
                        var worn = WornByItem();
                        if (seat != 0)
                        {
                            foreach (var it in items.Keys)
                                if (worn.TryGetValue(it, out var p) && (!lastSeatOffDetach.TryGetValue(it, out var t0) || (DateTime.Now - t0).TotalSeconds > 5))
                                {
                                    if (p.Properties == null) await EnsureProperties(client.Network.CurrentSim, new() { p });
                                    lastSeatOffDetach[it] = DateTime.Now;
                                    DetachItem(it, lastSeat == seat ? "seated; it was re-worn" : "seated");
                                }
                            attachTries = 0; standChecked = true; seatAttachState = $"seated: {items.Count} seat-off item(s) kept off";
                        }
                        else
                        {
                            if (lastSeat != 0 || lastSeat == uint.MaxValue)
                            {
                                if (lastSeat != uint.MaxValue && lastSeat != 0) StopKeptPose("stood up");
                                if (lastSeat != 0) { standSince = DateTime.Now; attachTries = 0; }
                            }
                            var missing = items.Where(kv => !worn.ContainsKey(kv.Key)).ToList();
                            // a 'sit' just detached it (detach-before-sit race, 2026-09-25): wait 15 s before treating this as "standing"
                            bool sitInProgress = lastSeatOffDetach.Values.Any(t => (DateTime.Now - t).TotalSeconds < 15);
                            if (missing.Count > 0 && !sitInProgress && attachTries < 2 && (DateTime.Now - standSince).TotalSeconds >= 1.5 && (DateTime.Now - attachAt).TotalSeconds > 15)
                            {
                                attachTries++; attachAt = DateTime.Now; standChecked = false;
                                foreach (var kv in missing)
                                {
                                    if (client.Self.SittingOn != 0) break; // sat down meanwhile
                                    var r = await AttachItem(kv.Key, kv.Value > 0 ? kv.Value.ToString() : null);
                                    Log("height", $"standing: re-attach {kv.Key} -> {r}");
                                }
                            }
                            if (!standChecked && (DateTime.Now - attachAt).TotalSeconds > 10) { standChecked = true; StandAnimCheck(); }
                            seatAttachState = $"standing: {items.Count - missing.Count}/{items.Count} seat-off item(s) worn";
                        }
                    }
                    lastSeat = seat;
                }
            }
            catch (Exception ex) { Log("height", "seat/attach loop error: " + ex.GetBaseException().Message); }
            try { await Task.Delay(1000, cts.Token); } catch { }
        }
    }

    static void StopKeptPose(string why)
    {
        foreach (var id in keptPose.Keys.ToList()) { client.Self.AnimationStop(id, true); keptPose.TryRemove(id, out _); Log("height", $"pose keeper: stopped our copy of {AnimName(id)} ({why})"); }
    }

    // standing: no sit animation may linger (built-in sit anims, anim-block ids, our pose copies)
    static void StandAnimCheck()
    {
        if (client.Self.SittingOn != 0) return;
        Dictionary<UUID, (int seq, UUID src)> cur; lock (animLock) cur = ownAnims;
        var block = ReadIdFile(AnimBlockFile);
        var stuck = cur.Keys.Where(k => (SitAnims.Contains(k) && k != Animations.SIT_TO_STAND) || block.Contains(k) || keptPose.ContainsKey(k)).ToList();
        foreach (var k in stuck) { client.Self.AnimationStop(k, true); keptPose.TryRemove(k, out _); }
        Log("height", $"stand check: playing [{string.Join(", ", cur.Select(kv => $"{AnimName(kv.Key)} from {SrcDesc(kv.Value.src)}"))}]{(stuck.Count > 0 ? " -> stopped stuck " + string.Join(",", stuck.Select(AnimName)) : " OK")}");
    }

    // ---- attach move / attach pos (2026-10-05) ---------------------------------
    // Attachment-local metres on the ROOT prim (SetPosition with Linked carries children).
    // HUD points (31..38) refused unless the caller adds "hudok" / "allow-hud".
    // Each delta axis clamped to ±0.1 m per call. Logged to DetachLog with an undo line.
    const float AttachMoveMaxDelta = 0.1f;

    static bool IsHudAttachPoint(AttachmentPoint pt)
    {
        int n = (int)pt;
        return n >= (int)AttachmentPoint.HUDCenter2 && n <= (int)AttachmentPoint.HUDBottomRight;
    }

    static float ClampAttachDelta(float v) => Math.Clamp(v, -AttachMoveMaxDelta, AttachMoveMaxDelta);

    static string FmtAttachPos(Vector3 p) =>
        string.Format(CultureInfo.InvariantCulture, "{0:F4},{1:F4},{2:F4}", p.X, p.Y, p.Z);

    static string FmtAttachRot(Quaternion q) =>
        string.Format(CultureInfo.InvariantCulture, "{0:F4},{1:F4},{2:F4},{3:F4}", q.X, q.Y, q.Z, q.W);

    static string FmtAttachDelta(float dx, float dy, float dz) =>
        string.Format(CultureInfo.InvariantCulture, "{0:G} {1:G} {2:G}", dx, dy, dz);

    // Pure helpers for offline selftest / parsing (token list already after "attach").
    internal static bool TryParseAttachMoveArgs(List<string> tokens, out string spec, out float dx, out float dy, out float dz, out bool hudOk, out string err)
    {
        spec = ""; dx = dy = dz = 0; hudOk = false; err = "";
        if (tokens == null || tokens.Count < 1 || !tokens[0].Equals("move", StringComparison.OrdinalIgnoreCase))
        { err = "usage: attach move <item uuid|obj uuid|name filter> <dx> <dy> <dz> [hudok]   (metres, attachment-local; ±0.1 m/axis; HUD needs hudok)"; return false; }
        var t = new List<string>(tokens);
        if (t.Count >= 2 && (t[^1].Equals("hudok", StringComparison.OrdinalIgnoreCase) || t[^1].Equals("allow-hud", StringComparison.OrdinalIgnoreCase)))
        { hudOk = true; t.RemoveAt(t.Count - 1); }
        if (t.Count < 5) { err = "usage: attach move <item uuid|obj uuid|name filter> <dx> <dy> <dz> [hudok]"; return false; }
        if (!F(t[^3], out dx) || !F(t[^2], out dy) || !F(t[^1], out dz))
        { err = "attach move: dx dy dz must be numbers (metres)"; return false; }
        spec = string.Join(' ', t.Skip(1).Take(t.Count - 4)).Trim();
        if (spec.Length == 0) { err = "attach move: missing attachment filter"; return false; }
        return true;
    }

    static async Task<(Primitive root, string err)> ResolveOwnAttachmentRoot(string key)
    {
        var sim = client?.Network?.CurrentSim;
        if (sim == null || client?.Self == null) return (null, "no current sim");
        var roots = WornPrims();
        if (roots.Count == 0) return (null, "no worn attachments in view (try 'worn recover')");
        await EnsureProperties(sim, roots);
        var m = MatchAttachment(key, roots);
        if (m.Count == 0) return (null, $"no worn attachment matches '{key}'; worn: {string.Join(", ", roots.Select(r => r.Properties?.Name ?? "?"))}");
        if (m.Count > 1) return (null, $"'{key}' matches {m.Count}: {string.Join(", ", m.Select(r => $"'{r.Properties?.Name}' item {AttachItemId(r)} obj {r.ID}"))}");
        var root = m[0];
        // WornPrims already requires ParentID == our LocalID; also refuse anything not owned by us when OwnerID is known.
        if (root.ParentID != client.Self.LocalID) return (null, "refused: not attached to me");
        if (root.Properties != null && root.Properties.OwnerID != UUID.Zero && root.Properties.OwnerID != client.Self.AgentID)
            return (null, $"refused: owner is {root.Properties.OwnerID}, not me");
        return (root, null);
    }

    static async Task<string> AttachPosCmd(string rest)
    {
        var t = Tokenize(rest);
        if (t.Count < 2 || !t[0].Equals("pos", StringComparison.OrdinalIgnoreCase))
            return "usage: attach pos <item uuid|obj uuid|name filter>   (quote names with spaces)";
        var key = string.Join(' ', t.Skip(1)).Trim();
        if (key.Length == 0) return "usage: attach pos <item uuid|obj uuid|name filter>";
        var (root, err) = await ResolveOwnAttachmentRoot(key);
        if (root == null) return err;
        var item = AttachItemId(root);
        var kids = LinkPrims(root).Count;
        return $"'{root.Properties?.Name ?? "?"}' item {item} obj {root.ID} local {root.LocalID} @{root.PrimData.AttachmentPoint} " +
               $"pos {FmtAttachPos(root.Position)} rot {FmtAttachRot(root.Rotation)} prims {kids} " +
               $"(attachment-local; Chest +X is typically forward/out from the torso)";
    }

    static async Task<string> AttachMoveCmd(string rest)
    {
        var t = Tokenize(rest);
        if (!TryParseAttachMoveArgs(t, out var key, out var rawDx, out var rawDy, out var rawDz, out var hudOk, out var perr))
            return perr;
        float dx = ClampAttachDelta(rawDx), dy = ClampAttachDelta(rawDy), dz = ClampAttachDelta(rawDz);
        bool clamped = dx != rawDx || dy != rawDy || dz != rawDz;
        var (root, err) = await ResolveOwnAttachmentRoot(key);
        if (root == null) return err;
        var pt = root.PrimData.AttachmentPoint;
        if (IsHudAttachPoint(pt) && !hudOk)
            return $"refused: '{root.Properties?.Name}' is at HUD point {pt}; add 'hudok' to move a HUD (usage: attach move ... dx dy dz hudok)";
        if (dx == 0 && dy == 0 && dz == 0)
            return $"nothing to do: delta is zero after clamp (±{AttachMoveMaxDelta} m/axis)" + (clamped ? $" (raw was {FmtAttachDelta(rawDx, rawDy, rawDz)})" : "");

        var sim = client.Network.CurrentSim;
        var item = AttachItemId(root);
        var oldPos = root.Position;
        var newPos = oldPos + new Vector3(dx, dy, dz);
        var undo = string.Format(CultureInfo.InvariantCulture, "attach move {0} {1:G} {2:G} {3:G}",
            item != UUID.Zero ? item.ToString() : root.ID.ToString(), -dx, -dy, -dz);
        var desc = $"'{root.Properties?.Name ?? "?"}' @{pt} item {item} obj {root.ID} local {root.LocalID}";
        try
        {
            File.AppendAllText(DetachLog,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} attach move {desc} old {FmtAttachPos(oldPos)} + ({FmtAttachDelta(dx, dy, dz)}) -> want {FmtAttachPos(newPos)}" +
                (clamped ? $" (clamped from {FmtAttachDelta(rawDx, rawDy, rawDz)})" : "") +
                $"; undo: text-galatay.sh cmd \"{undo}\"\n");
        }
        catch { }

        try { client.Objects.SelectObject(sim, root.LocalID, true); } catch { }
        client.Objects.SetPosition(sim, root.LocalID, newPos);
        Log("attach", $"attach move {desc} {FmtAttachPos(oldPos)} -> {FmtAttachPos(newPos)} (delta {FmtAttachDelta(dx, dy, dz)})");

        // Wait briefly for an ObjectUpdate that moves the root; report the sim's position.
        Vector3 seen = oldPos; bool got = false;
        var deadline = DateTime.UtcNow.AddSeconds(3.5);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            if (!sim.ObjectsPrimitives.TryGetValue(root.LocalID, out var cur) || cur == null) continue;
            seen = cur.Position;
            if (Vector3.Distance(seen, oldPos) > 1e-5f) { got = true; break; }
        }
        try { client.Objects.DeselectObject(sim, root.LocalID); } catch { }

        try
        {
            File.AppendAllText(DetachLog,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} attach move result {desc} sim pos {FmtAttachPos(seen)} {(got ? "UPDATED" : "NO-UPDATE-YET")}; undo: text-galatay.sh cmd \"{undo}\"\n");
        }
        catch { }

        var clampNote = clamped ? $" (clamped from {FmtAttachDelta(rawDx, rawDy, rawDz)})" : "";
        if (!got)
            return $"attach move sent for {desc}: {FmtAttachPos(oldPos)} + ({FmtAttachDelta(dx, dy, dz)}) -> {FmtAttachPos(newPos)}{clampNote}; sim has not echoed a new position yet (still {FmtAttachPos(seen)}); undo: {undo}";
        return $"attach move OK {desc}: {FmtAttachPos(oldPos)} -> {FmtAttachPos(seen)} (asked {FmtAttachPos(newPos)}, delta {FmtAttachDelta(dx, dy, dz)}){clampNote}; undo: {undo}";
    }

    static string AttachMoveSelfTest()
    {
        var lines = new List<string>(); int pass = 0, fail = 0;
        void C(bool ok, string name) { if (ok) pass++; else fail++; lines.Add($"{(ok ? "PASS" : "FAIL")} {name}"); }
        C(IsHudAttachPoint(AttachmentPoint.HUDCenter), "HUDCenter is HUD");
        C(IsHudAttachPoint(AttachmentPoint.HUDBottomRight), "HUDBottomRight is HUD");
        C(IsHudAttachPoint(AttachmentPoint.HUDCenter2), "HUDCenter2 is HUD");
        C(!IsHudAttachPoint(AttachmentPoint.Chest), "Chest is not HUD");
        C(!IsHudAttachPoint(AttachmentPoint.Neck), "Neck is not HUD");
        C(!IsHudAttachPoint(AttachmentPoint.RightPec), "RightPec is not HUD");
        C(ClampAttachDelta(0.05f) == 0.05f, "0.05 m unchanged");
        C(ClampAttachDelta(0.2f) == 0.1f, "0.2 m clamped to +0.1");
        C(ClampAttachDelta(-0.15f) == -0.1f, "-0.15 m clamped to -0.1");
        C(ClampAttachDelta(0f) == 0f, "0 unchanged");
        bool p1 = TryParseAttachMoveArgs(new List<string> { "move", "64f6948c-acb3-3a32-9509-3ab1923f2146", "0.05", "0", "0" },
            out var s1, out var x1, out var y1, out var z1, out var h1, out _);
        C(p1 && s1 == "64f6948c-acb3-3a32-9509-3ab1923f2146" && Math.Abs(x1 - 0.05f) < 1e-6f && y1 == 0 && z1 == 0 && !h1, "parse item uuid +0.05 0 0");
        bool p2 = TryParseAttachMoveArgs(new List<string> { "move", "Nipple", "Rings", "0", "0.02", "-0.01", "hudok" },
            out var s2, out var x2, out var y2, out var z2, out var h2, out _);
        C(p2 && s2 == "Nipple Rings" && x2 == 0 && Math.Abs(y2 - 0.02f) < 1e-6f && Math.Abs(z2 + 0.01f) < 1e-6f && h2, "parse multi-word name + hudok");
        bool p3 = TryParseAttachMoveArgs(new List<string> { "move", "0.05", "0", "0" }, out _, out _, out _, out _, out _, out var e3);
        C(!p3 && e3.StartsWith("usage:"), "too few tokens -> usage");
        bool p4 = TryParseAttachMoveArgs(new List<string> { "pos", "x" }, out _, out _, out _, out _, out _, out var e4);
        C(!p4 && e4.StartsWith("usage:"), "pos token rejected by move parser");
        var undo = string.Format(CultureInfo.InvariantCulture, "attach move {0} {1:G} {2:G} {3:G}",
            "64f6948c-acb3-3a32-9509-3ab1923f2146", -0.05f, 0f, 0f);
        C(undo == "attach move 64f6948c-acb3-3a32-9509-3ab1923f2146 -0.05 0 0", "undo line format");
        // Chest +X as outward: avatar_lad Chest default position is +0.15 on X from mChest
        C(true, "Chest +X documented as forward/out (avatar_lad Chest position 0.15 0 -0.1)");
        return $"attach move selftest: {pass} pass, {fail} fail (offline)\n" + string.Join("\n", lines);
    }

    static string DetachCofSelfTest()
    {
        var lines = new List<string>(); int pass = 0, fail = 0;
        void C(bool ok, string name) { if (ok) pass++; else fail++; lines.Add($"{(ok ? "PASS" : "FAIL")} {name}"); }
        C(!KeepCofForDetachWhy("command"), "detach command removes COF");
        C(!KeepCofForDetachWhy("wear remove"), "wear remove removes COF");
        C(!KeepCofForDetachWhy("unexpected self-detach"), "unexpected self-detach removes COF");
        C(KeepCofForDetachWhy("before sit"), "before sit keeps COF");
        C(KeepCofForDetachWhy("seated"), "seated keeps COF");
        C(KeepCofForDetachWhy("seated; it was re-worn"), "seated re-worn keeps COF");
        C(KeepCofForDetachWhy("ao restore"), "ao restore keeps COF");
        C(KeepCofForDetachWhy("ao restore: not active after 25 s"), "ao restore why keeps COF");
        C(!KeepCofForDetachWhy(""), "empty why removes COF");
        C(!ShouldRemoveCofOnUnexpectedDetach(avatarKilledInSamePacket: true, isSeatOffItem: false, currentlySeated: false, hadRecentDetachIntent: false, regionOrLoginGrace: false, ownAttachmentsKilledInPacket: 1), "avatar killed same packet -> keep");
        C(!ShouldRemoveCofOnUnexpectedDetach(false, false, false, true, false, 1), "recent DetachItem intent -> skip (already handled)");
        C(!ShouldRemoveCofOnUnexpectedDetach(false, true, true, false, false, 1), "seat-off while seated -> keep");
        C(ShouldRemoveCofOnUnexpectedDetach(false, true, false, false, false, 1), "seat-off while standing unexpected -> remove");
        C(!ShouldRemoveCofOnUnexpectedDetach(false, false, false, false, true, 1), "region/login grace -> keep");
        C(!ShouldRemoveCofOnUnexpectedDetach(false, false, false, false, false, 5), "mass kill (>=5) -> keep");
        C(ShouldRemoveCofOnUnexpectedDetach(false, false, false, false, false, 1), "lone unexpected unpacker detach -> remove");
        C(OutfitCheckWarnLine("Top", new UUID("aa265665-7a17-34f6-b423-66d336262ed2"), "last point Chest").StartsWith("WARNING:"), "outfit check WARN line format");
        return $"detach COF selftest: {pass} pass, {fail} fail (offline)\n" + string.Join("\n", lines);
    }

    static async Task<string> AttachCmds(string cmd, string[] a, string rest = "")
    {
        switch (cmd)
        {
            case "worn": if (a.Length > 0 && a[0] == "all") return await WornAll(); if (a.Length > 0 && a[0] == "raw") return WornRaw(); if (a.Length > 0 && a[0] == "selftest") return WornSelfTest(); if (a.Length > 0 && a[0] == "scan") return WornScan(); if (a.Length > 0 && a[0] == "probe") return await WornProbe(); if (a.Length > 0 && a[0] == "recover") { var n = await RecoverAttachments(99, 160, 8000); return $"worn recover: {attLastRecovery}"; } return await WornList(a.Length > 0 && a[0] == "scripts"); // 'worn all': Outfit.cs (read-only)
            case "detach": return a.Length == 1 && UUID.TryParse(a[0], out var d) ? await DetachItemAsync(d, "command") : "usage: detach <inventory item id>  (see 'worn'; removes COF link unless seat-off)";
            case "attach":
                if (a.Length >= 1 && a[0].Equals("move", StringComparison.OrdinalIgnoreCase)) return await AttachMoveCmd(rest);
                if (a.Length >= 1 && a[0].Equals("pos", StringComparison.OrdinalIgnoreCase)) return await AttachPosCmd(rest);
                return a.Length >= 1 && UUID.TryParse(a[0], out var at) ? await AttachItem(at, a.Length > 1 ? a[1] : null)
                    : "usage: attach <inventory item id> [attach point number] | attach move <item|obj|name> <dx> <dy> <dz> [hudok] | attach pos <item|obj|name>";
            case "animwatch":
                if (a.Length > 0 && a[0] == "off") animLogUntil = DateTime.MinValue;
                else if (a.Length > 0 && int.TryParse(a[0], out var mins)) animLogUntil = DateTime.Now.AddMinutes(mins);
                else if (a.Length > 0 && a[0] == "on") animLogUntil = DateTime.Now.AddMinutes(30);
                return $"animation logging {(DateTime.Now < animLogUntil ? "until " + animLogUntil.ToString("HH:mm:ss") : "off")}; pose keeper {(poseKeeper ? "on" : "off")}";
            case "posekeeper":
                if (a.Length == 1 && (a[0] == "on" || a[0] == "off")) poseKeeper = a[0] == "on";
                return $"pose keeper {(poseKeeper ? "on" : "off")}";
        }
        return "?";
    }
}

