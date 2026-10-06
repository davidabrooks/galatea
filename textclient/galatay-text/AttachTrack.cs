// AttachTrack.cs (2026-09-26 12:20, David: make 'worn all' reliable).
// Keeps its own table of Galatay's attachments straight from the raw object packets
// (ObjectUpdate, ObjectUpdateCompressed, ObjectUpdateCached, KillObject), independent of what
// LibreMetaverse keeps in sim.ObjectsPrimitives. 'worn raw' dumps it; 'worn selftest' checks the
// attach-point decoding. Mostly read-only; KillObject of an unexpected self-detach may remove a
// stale COF link (NoteOwnAttachmentKilled in AttachWatch.cs).
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using LibreMetaverse;
using LibreMetaverse.Packets;

namespace GalatayText;

public static partial class Program
{
    sealed class AttRec
    {
        public uint Local; public UUID Full; public uint Parent; public byte State; public UUID Item; public UUID Owner;
        public string Src = ""; public bool Reliable; public DateTime First; public DateTime Last; public DateTime? Killed; public int Updates;
    }
    static readonly ConcurrentDictionary<uint, AttRec> attTable = new();
    static readonly ConcurrentDictionary<uint, DateTime> selfLocalIds = new();   // every LocalID our own avatar had this session
    static readonly ConcurrentDictionary<uint, DateTime> cachedIds = new();      // LocalIDs announced via ObjectUpdateCached
    static readonly ConcurrentDictionary<uint, int> fullOrCompressedIds = new(); // LocalIDs that got a full/compressed update (for cache-miss check)
    static DateTime attTrackStart = DateTime.MinValue;
    // diagnostics: EVERY prim update seen in the first 120 s of the session (LocalID -> last full id/parent/source), and skipped packets
    sealed class SeenRec { public uint Local; public UUID Full; public uint Parent; public byte PCode; public string Src = ""; public DateTime T; public bool HasItem; }
    static readonly ConcurrentDictionary<uint, SeenRec> attSeenAll = new();
    static readonly ConcurrentDictionary<UUID, (string name, DateTime t)> ownChatters = new(); // object UUIDs that owner-said/said to us (e.g. the LSL Bridge)
    static int attSkippedOtherSim;
    // last attach point seen per inventory item (persisted; local file only) so an attachment the sim lists but never sent can be named
    static readonly string AttachPointsFile = "/home/box/viewers/textclient/attach-points.json";
    static ConcurrentDictionary<string, int> attPointCache;
    static int AttPointOf(UUID item)
    {
        if (attPointCache == null) { try { attPointCache = new(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(AttachPointsFile)) ?? new()); } catch { attPointCache = new(); } }
        return attPointCache.TryGetValue(item.ToString(), out var pt) ? pt : -1;
    }
    static void AttPointRemember(UUID item, int pt)
    {
        if (item == UUID.Zero || pt <= 0) return;
        if (AttPointOf(item) == pt) return;
        attPointCache[item.ToString()] = pt;
        try { var tmp = AttachPointsFile + ".tmp"; File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(attPointCache.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => k.Value), new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); File.Move(tmp, AttachPointsFile, true); } catch { }
    }
    // the sim's OWN list of our attachments, from AvatarAppearance.AttachmentBlock for our avatar (authoritative, read-only)
    static List<(UUID id, byte point)> simAttList = null; static DateTime simAttTime; static int simAttPackets; static int simAttCof;
    static readonly ConcurrentDictionary<uint, DateTime> killedUnseen = new();
    // every child LocalID per parent LocalID seen in raw full/compressed updates (current sim), for 'crowd' (raw vs stored)
    internal static readonly ConcurrentDictionary<uint, ConcurrentDictionary<uint, byte>> rawChildren = new();
    static void SeenNote(uint local, UUID full, uint parent, byte pcode, string src, bool hasItem)
    {
        if (parent != 0) rawChildren.GetOrAdd(parent, _ => new()).TryAdd(local, 0);
        if (attTrackStart == DateTime.MinValue) attTrackStart = DateTime.Now;
        if ((DateTime.Now - attTrackStart).TotalSeconds > 120 && !attSeenAll.ContainsKey(local)) return;
        attSeenAll[local] = new SeenRec { Local = local, Full = full, Parent = parent, PCode = pcode, Src = src, T = DateTime.Now, HasItem = hasItem };
    }
    static int attKillsOfSelfChildren;

    // SL: indra_constants.h ATTACHMENT_ID_FROM_STATE = swap the upper and lower nibble of ObjectData.State
    static int AttachPointFromState(byte s) => ((s & 0xF0) >> 4) | ((s & 0x0F) << 4);

    static int attLmvCount; static DateTime attLmvWindow = DateTime.MinValue;
    static void HookAttachTrack()
    {
        // The library's raw UDP receive queue drops packets (DropWrite) when full; at login the sim floods us with
        // ~10k object updates and SL sends ObjectUpdate UNRELIABLE, so a dropped update is never resent. 512 -> 8192.
        try { if (Settings.UdpReceiveQueueCapacity < 8192) Settings.UdpReceiveQueueCapacity = 8192; } catch { }
        // library warnings/errors (packet decode failures etc.) were going nowhere; log them, rate-limited
        LibreMetaverse.Logger.OnLogMessage += (msg, level) =>
        {
            if (level < Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                // diagnostics: what the library's own appearance code does with the outfit at login
                var m = msg?.ToString() ?? "";
                if (m.Contains("Current Outfit") || m.Contains("outfit send") || m.Contains("Outfit") || m.Contains("bake") || m.Contains("Bake") || m.StartsWith("Wearing ") || m.StartsWith("Region crossing") || m.StartsWith("Own avatar") || m.StartsWith("Seed capability") || m.Contains("server bake") || m.Contains("UpdateAvatarAppearance") || m.Contains("COF v"))
                    Log("lmvapp", $"{level}: {(m.Length > 250 ? m.Substring(0, 250) + "..." : m).Replace('\n', ' ')}");
                return;
            }
            var now = DateTime.Now;
            lock (attTable) { if ((now - attLmvWindow).TotalSeconds > 60) { attLmvWindow = now; attLmvCount = 0; } if (++attLmvCount > 20) return; }
            var t = msg?.ToString() ?? ""; if (t.Length > 300) t = t.Substring(0, 300) + "...";
            Log("lmv", $"{level}: {t.Replace('\n', ' ')}");
        };
        client.Self.ChatFromSimulator += (s2, e2) =>
        {
            try { if (e2.SourceType == ChatSourceType.Object && e2.OwnerID == client.Self.AgentID && e2.SourceID != UUID.Zero) ownChatters[e2.SourceID] = (e2.FromName, DateTime.Now); } catch { }
        };
        // diagnostics: log every OUTGOING wear/detach packet (whoever sends it: our commands or the library itself)
        client.Network.PacketSent += (s3, e3) =>
        {
            try
            {
                if (e3.SentBytes < 10 || e3.SentBytes > 4000) return;
                int end = e3.SentBytes - 1; var zb = new byte[8192];
                var pk = Packet.BuildPacket(e3.Data, ref end, zb);
                switch (pk)
                {
                    case RezMultipleAttachmentsFromInvPacket r:
                        Log("wearpkt", $"OUT RezMultipleAttachmentsFromInv FirstDetachAll={r.HeaderData.FirstDetachAll} total={r.HeaderData.TotalObjects} items: " +
                            string.Join("; ", r.ObjectData.Select(o => $"{Utils.BytesToString(o.Name)} {o.ItemID} pt 0x{o.AttachmentPt:X2}")));
                        break;
                    case RezSingleAttachmentFromInvPacket r1:
                        Log("wearpkt", $"OUT RezSingleAttachmentFromInv {Utils.BytesToString(r1.ObjectData.Name)} {r1.ObjectData.ItemID} pt 0x{r1.ObjectData.AttachmentPt:X2}"); break;
                    case DetachAttachmentIntoInvPacket d:
                        Log("wearpkt", $"OUT DetachAttachmentIntoInv item {d.ObjectData.ItemID}"); break;
                    case ObjectDetachPacket od:
                        Log("wearpkt", $"OUT ObjectDetach locals {string.Join(",", od.ObjectData.Select(o => o.ObjectLocalID))}"); break;
                    case ObjectDropPacket odr:
                        Log("wearpkt", $"OUT ObjectDrop locals {string.Join(",", odr.ObjectData.Select(o => o.ObjectLocalID))}"); break;
                    case AgentIsNowWearingPacket w:
                        Log("wearpkt", $"OUT AgentIsNowWearing {w.WearableData.Length} wearables"); break;
                }
            }
            catch { }
        };
        client.Network.RegisterCallback(PacketType.AvatarAppearance, (s4, e4) =>
        {
            try
            {
                var ap = (AvatarAppearancePacket)e4.Packet;
                if (ap.Sender.ID != client.Self.AgentID) return;
                Interlocked.Increment(ref simAttPackets);
                var list = (ap.AttachmentBlock ?? Array.Empty<AvatarAppearancePacket.AttachmentBlockBlock>()).Select(b => (b.ID, b.AttachmentPoint)).ToList();
                int cof = ap.AppearanceData != null && ap.AppearanceData.Length > 0 ? ap.AppearanceData[0].CofVersion : 0;
                simAttList = list; simAttTime = DateTime.Now; simAttCof = cof;
                Log("attach", $"AvatarAppearance for us (COF v{cof}): the sim lists {list.Count} attachment(s): {string.Join(", ", list.Select(x => $"{x.Item1}@#{x.Item2}"))}");
            }
            catch (Exception ex) { Log("attach", "AvatarAppearance track error: " + ex.GetBaseException().Message); }
        });
        client.Network.RegisterCallback(PacketType.ObjectUpdate, AtObjectUpdate);
        client.Network.RegisterCallback(PacketType.ObjectUpdateCompressed, AtObjectUpdateCompressed);
        client.Network.RegisterCallback(PacketType.ObjectUpdateCached, AtObjectUpdateCached);
        client.Network.RegisterCallback(PacketType.KillObject, AtKillObject);
    }

    static bool IsSelfParent(uint parent) => parent != 0 && (selfLocalIds.ContainsKey(parent) || parent == (client?.Self?.LocalID ?? 0));

    static UUID ItemFromNameValueText(string nv)
    {
        if (string.IsNullOrEmpty(nv)) return UUID.Zero;
        foreach (var line in nv.Split('\n'))
        {
            if (!line.StartsWith("AttachItemID", StringComparison.Ordinal)) continue;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && UUID.TryParse(parts[^1], out var u)) return u;
        }
        return UUID.Zero;
    }

    static void AttNote(uint local, UUID full, uint parent, byte state, UUID item, UUID owner, string src, bool reliable = false)
    {
        var now = DateTime.Now;
        if (attTrackStart == DateTime.MinValue) attTrackStart = now;
        bool isNew = false;
        var r = attTable.GetOrAdd(local, _ => { isNew = true; return new AttRec { Local = local, First = now }; });
        lock (r)
        {
            if (r.Full != UUID.Zero && r.Full != full) { r.First = now; r.Killed = null; r.Updates = 0; r.Item = UUID.Zero; isNew = true; } // LocalID reused
            r.Full = full; r.Parent = parent; r.State = state; r.Owner = owner; r.Src = src; r.Reliable = reliable; r.Last = now; r.Updates++;
            if (item != UUID.Zero) r.Item = item;
            if (r.Killed != null) { r.Killed = null; isNew = true; }
        }
        if (IsSelfParent(parent) && r.Item != UUID.Zero) AttPointRemember(r.Item, AttachPointFromState(state));
        if (isNew && IsSelfParent(parent))
        {
            Log("attach", string.Format(CultureInfo.InvariantCulture, "seen local {0} {1} point #{2} state 0x{3:X2} item {4} via {5}", local, full, AttachPointFromState(state), state, r.Item, src));
            try { NotePossibleTempAttach(full, local, r.Item, src); } catch { } // Experiences.cs: llAttachToAvatarTemp props
        }
    }

    static void AtObjectUpdate(object sender, PacketReceivedEventArgs e)
    {
        try
        {
            var p = (ObjectUpdatePacket)e.Packet;
            if (e.Simulator != client.Network.CurrentSim) { Interlocked.Increment(ref attSkippedOtherSim); return; }
            foreach (var b in p.ObjectData)
            {
                SeenNote(b.ID, b.FullID, b.ParentID, b.PCode, "full", ItemFromNameValueText(Utils.BytesToString(b.NameValue)) != UUID.Zero);
                var pc = (PCode)b.PCode;
                if (pc == PCode.Avatar && b.FullID == client.Self.AgentID) { selfLocalIds.TryAdd(b.ID, DateTime.Now); continue; }
                if (pc != PCode.Prim) continue;
                fullOrCompressedIds.AddOrUpdate(b.ID, 1, (_, n) => n + 1);
                var item = ItemFromNameValueText(Utils.BytesToString(b.NameValue));
                if (b.ParentID != 0 && (IsSelfParent(b.ParentID) || (item != UUID.Zero && b.OwnerID == client.Self.AgentID)))
                    AttNote(b.ID, b.FullID, b.ParentID, b.State, item, b.OwnerID, "full", p.Header.Reliable);
            }
        }
        catch (Exception ex) { Log("attach", "ObjectUpdate track error: " + ex.GetBaseException().Message); }
    }

    static void AtObjectUpdateCompressed(object sender, PacketReceivedEventArgs e)
    {
        try
        {
            var p = (ObjectUpdateCompressedPacket)e.Packet;
            if (e.Simulator != client.Network.CurrentSim) { Interlocked.Increment(ref attSkippedOtherSim); return; }
            foreach (var b in p.ObjectData)
            {
                var d = b.Data; if (d == null || d.Length < 85) continue;
                int i = 0;
                var full = new UUID(d, 0); i += 16;
                uint local = (uint)(d[i] | (d[i + 1] << 8) | (d[i + 2] << 16) | (d[i + 3] << 24)); i += 4;
                var pc = (PCode)d[i++];
                byte state = d[i++];
                i += 4 + 1 + 1 + 12 + 12 + 12; // crc, material, click action, scale, position, rotation
                var flags = (CompressedFlags)Utils.BytesToUInt(d, i); i += 4;
                var owner = new UUID(d, i); i += 16;
                if ((flags & CompressedFlags.HasAngularVelocity) != 0) i += 12;
                uint parent = 0;
                if ((flags & CompressedFlags.HasParent) != 0) { parent = (uint)(d[i] | (d[i + 1] << 8) | (d[i + 2] << 16) | (d[i + 3] << 24)); i += 4; }
                SeenNote(local, full, parent, (byte)pc, "compressed", (flags & CompressedFlags.HasNameValues) != 0);
                if (pc != PCode.Prim) continue;
                fullOrCompressedIds.AddOrUpdate(local, 1, (_, n) => n + 1);
                bool nv = (flags & CompressedFlags.HasNameValues) != 0;
                if (parent != 0 && (IsSelfParent(parent) || (nv && owner == client.Self.AgentID)))
                {
                    // the AttachItemID NameValue sits deep in the block; take it from the library's parsed prim (it runs first or right after)
                    var item = UUID.Zero;
                    if (e.Simulator.ObjectsPrimitives.TryGetValue(local, out var lp) && lp != null) item = AttachItemId(lp);
                    AttNote(local, full, parent, state, item, owner, "compressed", p.Header.Reliable);
                }
            }
        }
        catch (Exception ex) { Log("attach", "ObjectUpdateCompressed track error: " + ex.GetBaseException().Message); }
    }

    static void AtObjectUpdateCached(object sender, PacketReceivedEventArgs e)
    {
        try
        {
            var p = (ObjectUpdateCachedPacket)e.Packet;
            if (e.Simulator != client.Network.CurrentSim) return;
            foreach (var b in p.ObjectData) cachedIds.TryAdd(b.ID, DateTime.Now);
        }
        catch { }
    }

    static void AtKillObject(object sender, PacketReceivedEventArgs e)
    {
        try
        {
            var p = (KillObjectPacket)e.Packet;
            if (e.Simulator != client.Network.CurrentSim) return;
            var now = DateTime.Now;
            var killedIds = p.ObjectData.Select(b => b.ID).ToHashSet();
            bool avatarKilled = killedIds.Any(id => selfLocalIds.ContainsKey(id) || id == (client?.Self?.LocalID ?? 0));
            // own attachment roots in this packet (for unexpected self-detach COF cleanup)
            var ownKilled = new List<(uint local, UUID item)>();
            foreach (var b in p.ObjectData)
            {
                if (attTable.TryGetValue(b.ID, out var r))
                {
                    bool self; UUID item; lock (r) { r.Killed = now; self = IsSelfParent(r.Parent); item = r.Item; }
                    if (self)
                    {
                        Log("attach", string.Format(CultureInfo.InvariantCulture, "killed local {0} {1} point #{2} item {3} (detached or derezzed)", b.ID, r.Full, AttachPointFromState(r.State), item));
                    try { TempAttachGone(r.Full); } catch { }
                        ownKilled.Add((b.ID, item));
                    }
                }
                else
                {
                    uint me0 = client.Self.LocalID;
                    if (me0 != 0 && b.ID > me0 && b.ID <= me0 + 300 && !attSeenAll.ContainsKey(b.ID)) { killedUnseen[b.ID] = now; Log("attach", $"KillObject for never-received LocalID {b.ID} (me+{b.ID - me0})"); }
                }
                if (selfLocalIds.ContainsKey(b.ID))
                {
                    int n = attTable.Values.Count(x => x.Parent == b.ID && x.Killed == null);
                    Interlocked.Add(ref attKillsOfSelfChildren, n);
                    Log("attach", $"KillObject for OUR avatar local {b.ID} (current {client.Self.LocalID}); the library would also drop {n} attachment root(s) parented to it");
                }
            }
            foreach (var (local, item) in ownKilled)
                NoteOwnAttachmentKilled(item, local, avatarKilled, ownKilled.Count);
        }
        catch { }
    }

    // current own attachment roots according to the raw-packet table (not killed, parent = one of our avatar LocalIDs, and our CURRENT one if known)
    static List<AttRec> TrackedAttachments()
    {
        uint me = client?.Self?.LocalID ?? 0;
        return attTable.Values.Where(r => r.Killed == null && r.Parent != 0 && (me != 0 ? r.Parent == me : selfLocalIds.ContainsKey(r.Parent))).ToList();
    }

    static string WornRaw()
    {
        var sb = new StringBuilder();
        var sim = client.Network.CurrentSim; uint me = client.Self.LocalID;
        sb.AppendLine($"self LocalID now {me}; seen this session: {string.Join(", ", selfLocalIds.OrderBy(k => k.Value).Select(k => $"{k.Key} @{k.Value:HH:mm:ss}"))}; tracking since {attTrackStart:HH:mm:ss}");
        sb.AppendLine($"cached announcements {cachedIds.Count}, of which never got a full/compressed update: {cachedIds.Keys.Count(k => !fullOrCompressedIds.ContainsKey(k))}; KillObject of our avatar dropped children: {attKillsOfSelfChildren}");
        if (sim != null) sb.AppendLine($"UDP: received {sim.Stats.GetRecvPackets()} packets, DROPPED by the full receive queue {sim.Stats.GetDroppedPackets()}, resends received {sim.Stats.GetReceivedResends()}; receive queue capacity {Settings.UdpReceiveQueueCapacity}; last recovery: {attLastRecovery}");
        var sal = simAttList;
        sb.AppendLine(sal == null ? $"sim's own attachment list (AvatarAppearance for us): none received yet ({simAttPackets} packets)"
            : $"sim's own attachment list (AvatarAppearance for us, {simAttTime:HH:mm:ss}, COF v{simAttCof}, {simAttPackets} packet(s)): {sal.Count}: " +
              string.Join(", ", sal.Select(x => { var m = attTable.Values.FirstOrDefault(r => r.Full == x.id || r.Item == x.id); return $"{x.id}@#{x.point}{(m != null ? $"=local {m.Local}" : " (NOT received as an object)")}"; })));
        var rows = attTable.Values.Where(r => IsSelfParent(r.Parent) || r.Owner == client.Self.AgentID).OrderBy(r => r.First).ToList();
        sb.AppendLine($"tracked own attachment roots ({rows.Count}):");
        foreach (var r in rows)
        {
            Primitive lp = null; sim?.ObjectsPrimitives.TryGetValue(r.Local, out lp);
            var name = lp?.Properties?.Name ?? "?";
            var libState = lp == null ? "NOT in library" : $"library parent {lp.ParentID} point {lp.PrimData.AttachmentPoint} item {AttachItemId(lp)}";
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  local {0,-10} parent {1,-10}{2} state 0x{3:X2} -> #{4,-3} {5,-15} item {6} '{7}' via {8}{13} first {9:HH:mm:ss} updates {10}{11} | {12}",
                r.Local, r.Parent, r.Parent == me ? "*" : " ", r.State, AttachPointFromState(r.State), (AttachmentPoint)(byte)AttachPointFromState(r.State),
                r.Item, name, r.Src, r.First, r.Updates, r.Killed != null ? $" KILLED {r.Killed:HH:mm:ss}" : "", libState, r.Reliable ? " (reliable)" : " (unreliable)"));
        }
        var lib = WornPrims();
        var missing = lib.Where(p => !attTable.ContainsKey(p.LocalID)).ToList();
        sb.AppendLine($"library prims with ParentID == {me}: {lib.Count}; not in the raw table: {missing.Count}{(missing.Count > 0 ? " (" + string.Join(", ", missing.Select(p => p.LocalID)) + ")" : "")}");
        return sb.ToString().TrimEnd();
    }

    // 'worn scan': where did the missing attachments go? (read-only)
    static string WornScan()
    {
        var sb = new StringBuilder(); var sim = client.Network.CurrentSim; uint me = client.Self.LocalID;
        if (sim == null) return "no sim";
        sb.AppendLine($"self LocalID {me}; prim updates noted in first 120 s: {attSeenAll.Count}; packets skipped as other-sim: {attSkippedOtherSim}");
        var near = attSeenAll.Values.Where(r => r.Local > me - 3 && r.Local <= me + 200).OrderBy(r => r.Local).ToList();
        sb.AppendLine($"updates with LocalID in {me - 2}..{me + 200} ({near.Count}):");
        foreach (var r in near)
        {
            bool inLib = sim.ObjectsPrimitives.ContainsKey(r.Local);
            sb.AppendLine($"  {r.Local} full {r.Full} parent {r.Parent}{(r.Parent == me ? "*" : "")} pcode {r.PCode} via {r.Src} {r.T:HH:mm:ss}{(r.HasItem ? " HAS-AttachItemID" : "")}{(inLib ? "" : " NOT-IN-LIBRARY")}");
        }
        var gaps = new List<uint>(); uint maxSeen = near.Count > 0 ? near.Max(r => r.Local) : me;
        for (uint id = me + 1; id <= maxSeen; id++) if (!attSeenAll.ContainsKey(id) && !sim.ObjectsPrimitives.ContainsKey(id)) gaps.Add(id);
        sb.AppendLine($"KillObject for never-received LocalIDs near us: {killedUnseen.Count} ({string.Join(",", killedUnseen.OrderBy(k => k.Key).Select(k => $"{k.Key}@{k.Value:HH:mm:ss}"))})");
        sb.AppendLine($"never-seen LocalIDs between {me + 1} and {maxSeen}: {gaps.Count} ({string.Join(",", gaps.Take(60))})");
        var orphans = sim.ObjectsPrimitives.Values.Where(p => p != null && p.ParentID != 0 && !sim.ObjectsPrimitives.ContainsKey(p.ParentID) && !sim.ObjectsAvatars.ContainsKey(p.ParentID)).GroupBy(p => p.ParentID).ToList();
        sb.AppendLine($"library child prims whose parent is unknown: {orphans.Sum(g => g.Count())} in {orphans.Count} group(s): {string.Join(", ", orphans.Take(20).Select(g => $"parent {g.Key} x{g.Count()}"))}");
        var withItem = sim.ObjectsPrimitives.Values.Where(p => p != null && AttachItemId(p) != UUID.Zero).ToList();
        sb.AppendLine($"library prims carrying an AttachItemID (any parent) ({withItem.Count}): {string.Join("; ", withItem.Select(p => $"{p.LocalID} parent {p.ParentID} item {AttachItemId(p)}"))}");
        sb.AppendLine($"own objects that chatted to us ({ownChatters.Count}):");
        foreach (var kv in ownChatters)
        {
            var lp = sim.ObjectsPrimitives.Values.FirstOrDefault(p => p != null && p.ID == kv.Key);
            var sr = attSeenAll.Values.FirstOrDefault(r => r.Full == kv.Key);
            sb.AppendLine($"  {kv.Key} '{kv.Value.name}' {kv.Value.t:HH:mm:ss}: library {(lp == null ? "NO prim" : $"local {lp.LocalID} parent {lp.ParentID}")}; raw {(sr == null ? "never received" : $"local {sr.Local} parent {sr.Parent} via {sr.Src}")}");
        }
        return sb.ToString().TrimEnd();
    }

    // 'worn probe' (read-only): does the sim answer object requests at all, and does the chatting bridge object still exist?
    static async Task<string> WornProbe()
    {
        var sb = new StringBuilder(); var sim = client.Network.CurrentSim; uint me = client.Self.LocalID;
        if (sim == null || me == 0) return "no sim";
        // 1) control: re-request one attachment we DO have; the sim should answer with a fresh full update
        var ctl = TrackedAttachments().OrderBy(r => r.Local).FirstOrDefault();
        if (ctl != null)
        {
            int u0; lock (ctl) u0 = ctl.Updates;
            client.Objects.RequestObjects(sim, new List<uint> { ctl.Local });
            var t0 = DateTime.Now; while ((DateTime.Now - t0).TotalSeconds < 5) { lock (ctl) if (ctl.Updates > u0) break; await Task.Delay(200); }
            int u1; lock (ctl) u1 = ctl.Updates;
            sb.AppendLine($"control re-request of known attachment local {ctl.Local}: {(u1 > u0 ? $"ANSWERED (updates {u0} -> {u1}) in {(DateTime.Now - t0).TotalSeconds:F1} s" : "no answer in 5 s")}");
        }
        // 2) chatting own objects: ask the sim for their properties by UUID (plus one known attachment as a control)
        var probeList = ownChatters.ToList();
        if (ctl != null && ctl.Full != UUID.Zero) probeList.Insert(0, new KeyValuePair<UUID, (string name, DateTime t)>(ctl.Full, ("(control: known attachment local " + ctl.Local + ")", DateTime.Now)));
        foreach (var kv in probeList)
        {
            var got = new TaskCompletionSource<Primitive.ObjectProperties>();
            EventHandler<ObjectPropertiesFamilyEventArgs> h = (s1, e1) => { if (e1.Properties?.ObjectID == kv.Key) got.TrySetResult(e1.Properties); };
            client.Objects.ObjectPropertiesFamily += h;
            try
            {
                client.Objects.RequestObjectPropertiesFamily(sim, kv.Key);
                var done = await Task.WhenAny(got.Task, Task.Delay(6000));
                bool inRaw = attSeenAll.Values.Any(r => r.Full == kv.Key);
                sb.AppendLine(done == got.Task
                    ? $"object {kv.Key} '{kv.Value.name}': sim says it EXISTS (name '{got.Task.Result.Name}', owner {got.Task.Result.OwnerID}); object update received: {inRaw}"
                    : $"object {kv.Key} '{kv.Value.name}': NO answer in 6 s (the object no longer exists in the region); object update received: {inRaw}");
            }
            finally { client.Objects.ObjectPropertiesFamily -= h; }
        }
        return sb.ToString().TrimEnd();
    }

    static string attLastRecovery = "none";
    // Ask the sim again (RequestMultipleObjects = the viewer's normal cache-miss request; read-only) for LocalIDs just
    // after our avatar's LocalID that we have never received. Attachments get LocalIDs right after the avatar at login.
    static async Task<int> RecoverAttachments(int wanted, int span = 160, int waitMs = 8000)
    {
        var sim = client.Network.CurrentSim; uint me = client.Self.LocalID;
        if (sim == null || me == 0 || wanted <= 0) return 0;
        int before = TrackedAttachments().Count;
        var ids = UnknownLocalIds(me, span, id => sim.ObjectsPrimitives.ContainsKey(id) || attTable.ContainsKey(id));
        for (int k = 0; k < ids.Count; k += 200) client.Objects.RequestObjects(sim, ids.Skip(k).Take(200).ToList());
        var t0 = DateTime.Now;
        while ((DateTime.Now - t0).TotalMilliseconds < waitMs && TrackedAttachments().Count - before < wanted) await Task.Delay(250);
        int got = TrackedAttachments().Count - before;
        attLastRecovery = $"{DateTime.Now:HH:mm:ss} re-requested {ids.Count} unknown LocalIDs {me + 1}..{me + (uint)span}, recovered {got} of {wanted} missing attachment(s)";
        Log("attach", attLastRecovery);
        return got;
    }

    // (2026-10-04, after teleport) The destination sim gives our avatar and every attachment NEW LocalIDs, and after a fast
    // teleport most of those attachment updates never reach us (they arrive while the old region is still current, or are
    // lost: SL sends them unreliable), so 'worn' showed 1 of 11 and the AO guard thought Martha was gone. Same idea as the
    // reference viewer's cache-miss pass after a region change: ask the sim for the LocalIDs right after our avatar that we
    // have never received (read-only RequestMultipleObjects). The sim answers for objects it never sent us.
    static DateTime attLastAutoRecover = DateTime.MinValue;
    // ids the sim lists on our avatar (AvatarAppearance) that we hold no prim for; zero ids (list still filling) are skipped
    static int SimListMissing(IEnumerable<UUID> simList, ICollection<UUID> haveObjIds) =>
        simList.Count(u => u != UUID.Zero && !haveObjIds.Contains(u));
    // LocalIDs me+1..me+span that we know nothing about (these are what gets re-requested)
    static List<uint> UnknownLocalIds(uint me, int span, Func<uint, bool> known)
    {
        var ids = new List<uint>();
        if (me == 0) return ids;
        for (uint id = me + 1; id <= me + (uint)span; id++) if (!known(id)) ids.Add(id);
        return ids;
    }
    static int AttachmentsMissingNow()
    {
        var have = WornPrims().Select(p => p.ID).Concat(TrackedAttachments().Select(r => r.Full)).ToHashSet();
        int n = simAttList != null && simAttTime > lastSimChange ? SimListMissing(simAttList.Select(x => x.id), have) : 0;
        return n;
    }
    static DateTime lastSimChange = DateTime.MinValue;
    // after a region change: a few re-request rounds until the sim's own list is covered (HUDs are not in that list,
    // so one round always runs to also pick up the AO HUD)
    static async Task RecoverAfterRegionChange()
    {
        lastSimChange = DateTime.Now;
        for (int round = 0; round < 4; round++)
        {
            await Task.Delay(round == 0 ? 2500 : 4000);
            if (!LoggedIn || client.Network.CurrentSim == null) return;
            int miss = AttachmentsMissingNow();
            if (round > 0 && miss == 0) break;
            attLastAutoRecover = DateTime.Now;
            await RecoverAttachments(Math.Max(miss, 1), 300, 4000);
        }
        Log("attach", $"after region change: {WornPrims().Count} attachment(s) known, {AttachmentsMissingNow()} of the sim's list still not received");
    }
    // before (re-)attaching an item we think is missing: re-request unknown objects first (once per 20 s); true = it was worn all along
    static async Task<bool> FoundAfterRecover(UUID item)
    {
        if (WornByItem().ContainsKey(item)) return true;
        if ((DateTime.Now - attLastAutoRecover).TotalSeconds < 20) return false;
        attLastAutoRecover = DateTime.Now;
        await RecoverAttachments(1, 300, 4000);
        return WornByItem().ContainsKey(item);
    }

    static string WornSelfTest()
    {
        // (raw ObjectData.State, expected SL attach point) pairs; State = point with its nibbles swapped
        var cases = new (byte state, int point, string name)[] {
            (0x00, 0, "not attached / Default"), (0x10, 1, "Chest"), (0x20, 2, "Skull"), (0x60, 6, "RightHand"), (0xA0, 10, "Pelvis"),
            (0xC0, 12, "Chin"), (0x22, 34, "HUDTopLeft"), (0x12, 33, "HUDTop"), (0x03, 48, "bento #48"), (0x73, 55, "bento #55"),
            (0x04, 64, "(beyond the enum)"), (0x82, 40, "Avatar center / Root"), (0xE0, 14, "RightEar") };
        var sb = new StringBuilder(); int ok = 0;
        foreach (var (s, pt, nm) in cases)
        {
            int ours = AttachPointFromState(s);
            var cd = new Primitive.ConstructionData { State = s };
            int lib = (int)cd.AttachmentPoint;
            bool pass = ours == pt && lib == pt;
            if (pass) ok++;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0} state 0x{1:X2} -> ours #{2}, library #{3} ({4}), expected #{5} {6}", pass ? "PASS" : "FAIL", s, ours, lib, (AttachmentPoint)(byte)lib, pt, nm));
        }
        // after-teleport recovery helpers (zero ids = list still filling; known ids are not re-requested)
        var a = UUID.Random(); var b = UUID.Random(); var c = UUID.Random();
        int total = cases.Length + 3;
        bool t1 = SimListMissing(new[] { a, b, c, UUID.Zero }, new HashSet<UUID> { a }) == 2;
        bool t2 = SimListMissing(new[] { a }, new HashSet<UUID> { a }) == 0;
        var u = UnknownLocalIds(1000, 5, id => id == 1002 || id == 1004);
        bool t3 = u.SequenceEqual(new uint[] { 1001, 1003, 1005 }) && UnknownLocalIds(0, 5, _ => false).Count == 0;
        foreach (var (pass, nm) in new[] { (t1, "sim list 3 ids + zero, 1 held -> 2 missing"), (t2, "all held -> 0 missing"), (t3, "unknown LocalIDs after me skip known ones") })
        { if (pass) ok++; sb.AppendLine($"  {(pass ? "PASS" : "FAIL")} after-teleport: {nm}"); }
        return $"worn selftest: {ok}/{total} passed (decode = ATTACHMENT_ID_FROM_STATE, nibble swap; after-teleport recovery)\n" + sb.ToString().TrimEnd();
    }
}
