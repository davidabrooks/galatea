// Experiences.cs (2026-10-05): Second Life Experience Tools support for AVsitter props.
// Root cause (textclient.log 10:33 / 10:38 PT): ScriptQuestion for Dutchie pencil / assistant notes /
// Dutchie her coffeemug asked Attach (and later the experience bitmask 408628) and was IGNORED.
// AVsitter props use llRequestExperiencePermissions + llAttachToAvatarTemp; without a grant, and without
// ExperiencePreferences Allow, the prop never attaches.
//
// Policy: auto-grant experience ScriptQuestions when the experience is (a) already Allowed in prefs,
// (b) on our small name/id allowlist (AVsitter by Code Violet), or (c) Allowed/Trusted on the current
// region / permitted on the current parcel. Never grant Debit. Persist Allow/Block via ExperiencePreferences.
// Classic Attach (no ExperienceID) while seated: grant Attach|TriggerAnimation only (AVsitter fallback).
// Temp attachments (llAttachToAvatarTemp) are tracked, shown as TEMP in `worn`, never written to COF,
// and cleaned up on stand if the experience does not detach them.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using LibreMetaverse;
using LibreMetaverse.Messages.Linden;

namespace GalatayText;

public static partial class Program
{
    // Experience permission set from llRequestExperiencePermissions (plus common high bits the sim sends).
    static readonly ScriptPermission ExpSafePerms =
        ScriptPermission.TakeControls | ScriptPermission.TriggerAnimation | ScriptPermission.Attach |
        ScriptPermission.TrackCamera | ScriptPermission.ControlCamera | ScriptPermission.Teleport |
        (ScriptPermission)(8192 | 0x20000 | 0x40000); // SilentEstateManagement + experience-protocol high bits

    static readonly string ExpAllowFile = Env("GT_EXP_ALLOW", "/home/box/viewers/textclient/exp-allow.txt");
    static readonly string ExpBlockFile = Env("GT_EXP_BLOCK", "/home/box/viewers/textclient/exp-block.txt");
    // Seed allowlist names (matched case-insensitive against GetExperienceInfo / FindExperienceByName).
    static readonly string[] ExpAllowNames = { "AVsitter" };
    // Cached experience id for AVsitter once resolved (also written into exp-allow.txt).
    static UUID avsitterExpId = UUID.Zero;
    static readonly ConcurrentDictionary<UUID, string> expNameCache = new(); // id -> name
    static readonly ConcurrentDictionary<UUID, byte> expAllowedLocal = new(); // local allowlist (+ prefs mirror)
    static readonly ConcurrentDictionary<UUID, byte> expBlockedLocal = new();
    static readonly ConcurrentDictionary<UUID, byte> expRegionOk = new(); // region Allowed/Trusted
    static DateTime expRegionFetched = DateTime.MinValue;
    static DateTime lastExpGrant = DateTime.MinValue; // recent grant -> next new attachment is likely TEMP
    // Temp attachments: object UUID -> (localId, itemId, name, since)
    static readonly ConcurrentDictionary<UUID, (uint local, UUID item, string name, DateTime since)> tempAttaches = new();
    static int expHooked;

    static void HookExperiences()
    {
        if (Interlocked.Exchange(ref expHooked, 1) == 1) return;
        LoadExpLists();
        client.Self.ScriptQuestion += OnScriptQuestionExp;
        client.Network.SimChanged += (s, e) => { expRegionOk.Clear(); expRegionFetched = DateTime.MinValue; _ = Task.Run(RefreshRegionExperiences); };
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(4000);
                await RefreshExperiencePrefs();
                await ResolveAvsitterId();
                await RefreshRegionExperiences();
            }
            catch (Exception ex) { Log("exp", "bootstrap error: " + ex.GetBaseException().Message); }
        });
        Log("exp", "experience permission handler hooked (auto-grant allowlist/region-allowed; Debit never granted)");
    }

    static void LoadExpLists()
    {
        foreach (var id in ReadUuidFile(ExpAllowFile)) expAllowedLocal[id] = 1;
        foreach (var id in ReadUuidFile(ExpBlockFile)) expBlockedLocal[id] = 1;
        if (expAllowedLocal.Count > 0) avsitterExpId = expAllowedLocal.Keys.FirstOrDefault();
    }

    static HashSet<UUID> ReadUuidFile(string path)
    {
        var s = new HashSet<UUID>();
        try { if (File.Exists(path)) foreach (var l in File.ReadAllLines(path)) { var t = l.Split('#')[0].Trim(); if (UUID.TryParse(t, out var u) && u != UUID.Zero) s.Add(u); } } catch { }
        return s;
    }

    static void SaveUuidFile(string path, IEnumerable<UUID> ids)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllLines(tmp, ids.Distinct().OrderBy(u => u.ToString()).Select(u => u.ToString()));
            File.Move(tmp, path, true);
        }
        catch (Exception ex) { Log("exp", $"save {path} failed: " + ex.GetBaseException().Message); }
    }

    // Pure decision (offline selftest). grantPerms = what to put in ScriptAnswerYes (Debit stripped).
    internal static (bool grant, string why, ScriptPermission grantPerms) DecideScriptQuestion(
        UUID experienceId, ScriptPermission questions, bool seated,
        bool prefAllowed, bool prefBlocked, bool regionOrParcelAllowed, bool nameOrIdAllowlisted)
    {
        var hasDebit = (questions & ScriptPermission.Debit) != 0;
        var safe = questions & ~ScriptPermission.Debit;
        if (safe == ScriptPermission.None && !hasDebit)
            return (false, "nothing requested", ScriptPermission.None);

        if (experienceId != UUID.Zero)
        {
            if (prefBlocked) return (false, "blocked in ExperiencePreferences", ScriptPermission.None);
            if (prefAllowed || nameOrIdAllowlisted || regionOrParcelAllowed)
            {
                if (safe == ScriptPermission.None)
                    return (false, "experience request only asked for Debit (never granted)", ScriptPermission.None);
                var why = prefAllowed ? "already Allowed in prefs"
                    : nameOrIdAllowlisted ? "allowlist"
                    : "region/parcel allowed";
                return (true, why, safe);
            }
            return (false, "experience not allowed (not in prefs/allowlist/region)", ScriptPermission.None);
        }

        // Classic (no ExperienceID): AVsitter fallback while seated — Attach (+ TriggerAnimation) only.
        if (!seated) return (false, "classic ScriptQuestion while standing (ignored)", ScriptPermission.None);
        if ((questions & ScriptPermission.Attach) == 0)
            return (false, "classic ScriptQuestion without Attach (ignored)", ScriptPermission.None);
        if (hasDebit && (questions & ~ScriptPermission.Debit & ~ScriptPermission.Attach & ~ScriptPermission.TriggerAnimation) != 0)
            return (false, "classic request includes Debit plus other perms (ignored)", ScriptPermission.None);
        var classic = questions & (ScriptPermission.Attach | ScriptPermission.TriggerAnimation);
        if (classic == ScriptPermission.None) return (false, "classic Attach stripped to nothing", ScriptPermission.None);
        return (true, "classic Attach while seated (prop fallback)", classic);
    }

    static async void OnScriptQuestionExp(object s, ScriptQuestionEventArgs e)
    {
        try
        {
            var expId = e.ExperienceID;
            var seated = false; try { seated = client.Self.SittingOn != 0; } catch { }
            bool prefAllowed = false, prefBlocked = false, regionOk = false, allowlisted = false;
            string expName = expId == UUID.Zero ? "(none)" : (expNameCache.TryGetValue(expId, out var n0) ? n0 : expId.ToString());

            if (expId != UUID.Zero)
            {
                prefBlocked = expBlockedLocal.ContainsKey(expId);
                prefAllowed = expAllowedLocal.ContainsKey(expId);
                allowlisted = IsAllowlisted(expId, expName);
                if (!prefAllowed && !prefBlocked)
                {
                    // Refresh prefs once if we have no local opinion.
                    try
                    {
                        var p = await client.Self.GetExperiencePermissionAsync(expId);
                        if (p == "Allow") { prefAllowed = true; expAllowedLocal[expId] = 1; }
                        else if (p == "Block") { prefBlocked = true; expBlockedLocal[expId] = 1; }
                    }
                    catch { }
                }
                if ((DateTime.Now - expRegionFetched).TotalSeconds > 60) await RefreshRegionExperiences();
                regionOk = expRegionOk.ContainsKey(expId);
                if (!regionOk)
                {
                    try
                    {
                        var parcel = await ParcelAt(client.Network.CurrentSim, client.Self.SimPosition.X, client.Self.SimPosition.Y, 5000);
                        if (parcel != null)
                        {
                            var q = await client.Self.QueryExperiencesOnParcelAsync(parcel.LocalID, new[] { expId });
                            if (q != null && q.TryGetValue(expId, out var ok) && ok) regionOk = true;
                        }
                    }
                    catch { }
                }
                if (expName == expId.ToString())
                {
                    try
                    {
                        var info = await client.Self.GetExperienceInfoAsync(new[] { expId });
                        var hit = info?.Experiences?.FirstOrDefault(x => x.ExperienceID == expId || x.PublicID == expId);
                        if (hit != null && !string.IsNullOrEmpty(hit.Name))
                        {
                            expName = hit.Name;
                            expNameCache[expId] = expName;
                            if (hit.PublicID != UUID.Zero) expNameCache[hit.PublicID] = expName;
                            allowlisted = IsAllowlisted(expId, expName) || IsAllowlisted(hit.PublicID, expName);
                        }
                    }
                    catch { }
                }
            }

            var (grant, why, perms) = DecideScriptQuestion(expId, e.Questions, seated, prefAllowed, prefBlocked, regionOk, allowlisted);
            if (!grant)
            {
                Log("perm", $"'{e.ObjectName}' (owner {e.ObjectOwnerName}) asks [{e.Questions}] exp={expName} ({expId}): DENY ({why}); Debit never granted");
                try { client.Self.ScriptQuestionReply(e.Simulator, e.ItemID, e.TaskID, ScriptPermission.None); } catch { }
                return;
            }

            lastExpGrant = DateTime.Now;
            try { client.Self.ScriptQuestionReply(e.Simulator, e.ItemID, e.TaskID, perms); } catch (Exception ex) { Log("perm", "ScriptQuestionReply failed: " + ex.GetBaseException().Message); return; }
            Log("perm", $"'{e.ObjectName}' (owner {e.ObjectOwnerName}) asks [{e.Questions}] exp={expName} ({expId}): GRANT [{perms}] ({why})");

            if (expId != UUID.Zero && !prefAllowed)
            {
                try
                {
                    var ok = await client.Self.SetExperiencePermissionAsync(expId, "Allow");
                    expAllowedLocal[expId] = 1;
                    expBlockedLocal.TryRemove(expId, out _);
                    PersistExpLists();
                    Log("exp", $"ExperiencePreferences Allow {(ok ? "saved" : "FAILED")} for '{expName}' ({expId})");
                }
                catch (Exception ex) { Log("exp", "SetExperiencePermission Allow failed: " + ex.GetBaseException().Message); }
            }
        }
        catch (Exception ex) { Log("perm", "ScriptQuestion handler error: " + ex.GetBaseException().Message); }
    }

    static bool IsAllowlisted(UUID id, string name)
    {
        if (id != UUID.Zero && (expAllowedLocal.ContainsKey(id) || id == avsitterExpId)) return true;
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var n in ExpAllowNames)
            if (name.Equals(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static void PersistExpLists()
    {
        SaveUuidFile(ExpAllowFile, expAllowedLocal.Keys);
        SaveUuidFile(ExpBlockFile, expBlockedLocal.Keys);
    }

    static async Task RefreshExperiencePrefs()
    {
        try
        {
            var prefs = await client.Self.GetAgentExperiencePermissionsAsync()
                        ?? await client.Self.GetExperiencePreferencesAsync();
            if (prefs == null) { Log("exp", "ExperiencePreferences unavailable"); return; }
            foreach (var id in prefs.Allowed) expAllowedLocal[id] = 1;
            foreach (var id in prefs.Blocked) { expBlockedLocal[id] = 1; expAllowedLocal.TryRemove(id, out _); }
            PersistExpLists();
            Log("exp", $"prefs loaded: {prefs.Allowed.Count} allowed, {prefs.Blocked.Count} blocked");
        }
        catch (Exception ex) { Log("exp", "RefreshExperiencePrefs: " + ex.GetBaseException().Message); }
    }

    static async Task RefreshRegionExperiences()
    {
        try
        {
            var reg = await client.Self.GetRegionExperiencesAsync();
            expRegionOk.Clear();
            if (reg != null)
            {
                foreach (var id in reg.Allowed) expRegionOk[id] = 1;
                foreach (var id in reg.Trusted) expRegionOk[id] = 1;
                if (reg.Default != UUID.Zero) expRegionOk[reg.Default] = 1;
                Log("exp", $"region experiences: {reg.Allowed.Count} allowed, {reg.Trusted.Count} trusted, {reg.Blocked.Count} blocked");
            }
            expRegionFetched = DateTime.Now;
        }
        catch (Exception ex) { Log("exp", "RefreshRegionExperiences: " + ex.GetBaseException().Message); }
    }

    static async Task ResolveAvsitterId()
    {
        try
        {
            var found = await client.Self.FindExperienceByNameAsync("AVsitter");
            if (found?.Experiences == null || found.Experiences.Count == 0) { Log("exp", "FindExperienceByName AVsitter: none"); return; }
            // Prefer exact name match; Code Violet is the known creator (name match is enough for allowlist).
            var hit = found.Experiences.FirstOrDefault(x => x.Name.Equals("AVsitter", StringComparison.OrdinalIgnoreCase))
                      ?? found.Experiences[0];
            var id = hit.PublicID != UUID.Zero ? hit.PublicID : hit.ExperienceID;
            if (id == UUID.Zero) return;
            avsitterExpId = id;
            expNameCache[id] = hit.Name;
            if (hit.ExperienceID != UUID.Zero) expNameCache[hit.ExperienceID] = hit.Name;
            expAllowedLocal[id] = 1;
            PersistExpLists();
            Log("exp", $"AVsitter experience resolved: '{hit.Name}' id={id} (owner agent {hit.AgentID})");
        }
        catch (Exception ex) { Log("exp", "ResolveAvsitterId: " + ex.GetBaseException().Message); }
    }

    // ---- temp attachments -------------------------------------------------
    // Called from AttachTrack when a new attachment on us is seen.
    static void NotePossibleTempAttach(UUID fullId, uint localId, UUID itemId, string via)
    {
        if (fullId == UUID.Zero) return;
        // Our own attach/wear commands set lastDetachSent / Appearance.Attach — those are not TEMP.
        // A grant in the last 45 s, or an item id missing from inventory, marks TEMP.
        bool recentGrant = (DateTime.Now - lastExpGrant).TotalSeconds < 45;
        bool inInv = false;
        try { if (itemId != UUID.Zero && client.Inventory.Store != null) inInv = client.Inventory.Store.Contains(itemId); } catch { }
        bool likelyTemp = recentGrant || itemId == UUID.Zero || (itemId != UUID.Zero && !inInv);
        if (!likelyTemp) return;
        // Skip known seat-off / AO item ids and anything already in COF-style wear we initiated.
        try { if (itemId != UUID.Zero && SeatOffItems().ContainsKey(itemId)) return; } catch { }
        try { if (lastDetachSent.ContainsKey(itemId)) return; } catch { }
        var name = "?";
        try
        {
            if (client.Network.CurrentSim != null && client.Network.CurrentSim.ObjectsPrimitives.TryGetValue(localId, out var p) && p?.Properties?.Name != null)
                name = p.Properties.Name;
        }
        catch { }
        tempAttaches[fullId] = (localId, itemId, name, DateTime.Now);
        Log("exp", $"TEMP attachment noted '{name}' obj {fullId} item {itemId} local {localId} via {via}");
    }

    static bool IsTempAttach(UUID objId) => tempAttaches.ContainsKey(objId);
    static bool IsTempAttachItem(UUID itemId) => itemId != UUID.Zero && tempAttaches.Values.Any(t => t.item == itemId);
    static bool IsTempAttachSource(UUID src) => src != UUID.Zero && tempAttaches.ContainsKey(src);

    static void TempAttachGone(UUID objId)
    {
        if (tempAttaches.TryRemove(objId, out var t))
            Log("exp", $"TEMP attachment gone '{t.name}' obj {objId}");
    }

    // After standing: give the experience a few seconds to detach props, then force-detach leftovers.
    static async Task CleanupTempAttachesAfterStand()
    {
        await Task.Delay(2500);
        if (client.Self.SittingOn != 0) return; // sat again
        var left = tempAttaches.ToArray();
        if (left.Length == 0) return;
        var sim = client.Network.CurrentSim;
        if (sim == null) return;
        var locals = new List<uint>();
        foreach (var kv in left)
        {
            var id = kv.Key; var t = kv.Value;
            uint local = t.local;
            try
            {
                var p = sim.ObjectsPrimitives.Values.FirstOrDefault(x => x != null && x.ID == id && x.ParentID == client.Self.LocalID);
                if (p != null) local = p.LocalID;
                else { TempAttachGone(id); continue; } // already gone
            }
            catch { }
            if (local != 0) locals.Add(local);
            Log("exp", $"TEMP lingering after stand: detaching '{t.name}' obj {id} local {local}");
            TempAttachGone(id);
        }
        if (locals.Count > 0)
        {
            try { client.Objects.DetachObjects(sim, locals); } catch (Exception ex) { Log("exp", "DetachObjects failed: " + ex.GetBaseException().Message); }
        }
    }

    // ---- commands ---------------------------------------------------------
    static async Task<string> ExpCmd(string[] a)
    {
        if (a.Length == 0 || a[0] == "status" || a[0] == "list")
        {
            await RefreshExperiencePrefs();
            await RefreshRegionExperiences();
            var sb = new StringBuilder();
            sb.AppendLine($"experiences: local allow {expAllowedLocal.Count}, local block {expBlockedLocal.Count}, region ok {expRegionOk.Count}, temp attaches {tempAttaches.Count}; AVsitter id={avsitterExpId}");
            async Task<string> Nm(UUID id)
            {
                if (expNameCache.TryGetValue(id, out var n)) return n;
                try
                {
                    var info = await client.Self.GetExperienceInfoAsync(new[] { id });
                    var hit = info?.Experiences?.FirstOrDefault();
                    if (hit != null && !string.IsNullOrEmpty(hit.Name)) { expNameCache[id] = hit.Name; return hit.Name; }
                }
                catch { }
                return "?";
            }
            foreach (var id in expAllowedLocal.Keys.Take(40))
                sb.AppendLine($"  ALLOW '{await Nm(id)}' {id}{(expRegionOk.ContainsKey(id) ? " [region]" : "")}");
            foreach (var id in expBlockedLocal.Keys.Take(20))
                sb.AppendLine($"  BLOCK '{await Nm(id)}' {id}");
            foreach (var id in expRegionOk.Keys.Where(x => !expAllowedLocal.ContainsKey(x)).Take(20))
                sb.AppendLine($"  REGION '{await Nm(id)}' {id}");
            foreach (var kv in tempAttaches)
                sb.AppendLine($"  TEMP '{kv.Value.name}' obj {kv.Key} item {kv.Value.item} since {kv.Value.since:HH:mm:ss}");
            return sb.ToString().TrimEnd();
        }
        if (a[0] == "selftest") return ExpSelfTest();
        if (a[0] == "info" && a.Length >= 2 && UUID.TryParse(a[1], out var iid))
        {
            var info = await client.Self.GetExperienceInfoAsync(new[] { iid });
            var hit = info?.Experiences?.FirstOrDefault();
            if (hit == null) return $"no experience info for {iid}";
            expNameCache[hit.ExperienceID] = hit.Name;
            if (hit.PublicID != UUID.Zero) expNameCache[hit.PublicID] = hit.Name;
            var pref = await client.Self.GetExperiencePermissionAsync(hit.PublicID != UUID.Zero ? hit.PublicID : hit.ExperienceID);
            return $"'{hit.Name}' id={hit.ExperienceID} public={hit.PublicID} owner={hit.AgentID} maturity={hit.Maturity} pref={pref ?? "?"} desc={hit.Description}";
        }
        if (a[0] == "allow" && a.Length >= 2 && UUID.TryParse(a[1], out var aid))
        {
            var ok = await client.Self.SetExperiencePermissionAsync(aid, "Allow");
            expAllowedLocal[aid] = 1; expBlockedLocal.TryRemove(aid, out _); PersistExpLists();
            return $"Allow {(ok ? "saved" : "FAILED")} for {aid}";
        }
        if (a[0] == "block" && a.Length >= 2 && UUID.TryParse(a[1], out var bid))
        {
            var ok = await client.Self.SetExperiencePermissionAsync(bid, "Block");
            expBlockedLocal[bid] = 1; expAllowedLocal.TryRemove(bid, out _); PersistExpLists();
            return $"Block {(ok ? "saved" : "FAILED")} for {bid}";
        }
        if (a[0] == "forget" && a.Length >= 2 && UUID.TryParse(a[1], out var fid))
        {
            var ok = await client.Self.ForgetExperiencePermissionAsync(fid);
            expAllowedLocal.TryRemove(fid, out _); expBlockedLocal.TryRemove(fid, out _); PersistExpLists();
            return $"Forget {(ok ? "saved" : "FAILED")} for {fid}";
        }
        if (a[0] == "refresh")
        {
            await RefreshExperiencePrefs(); await ResolveAvsitterId(); await RefreshRegionExperiences();
            return "experience prefs + region + AVsitter refreshed";
        }
        return "usage: exp [list|status|refresh|selftest] | exp info <id> | exp allow <id> | exp block <id> | exp forget <id>";
    }

    internal static string ExpSelfTest()
    {
        var sb = new StringBuilder(); int pass = 0, fail = 0;
        void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        var avs = new UUID("11111111-1111-1111-1111-111111111111");
        var other = new UUID("22222222-2222-2222-2222-222222222222");
        var expBits = ScriptPermission.TakeControls | ScriptPermission.TriggerAnimation | ScriptPermission.Attach |
                      ScriptPermission.TrackCamera | ScriptPermission.ControlCamera | ScriptPermission.Teleport;
        var withDebit = expBits | ScriptPermission.Debit;

        var (g, why, p) = DecideScriptQuestion(avs, expBits, true, false, false, false, true);
        C(g && p == expBits && why.Contains("allowlist"), "allowlist experience -> grant full safe perms");

        (g, why, p) = DecideScriptQuestion(avs, expBits, true, true, false, false, false);
        C(g && why.Contains("prefs"), "prefs Allowed -> grant");

        (g, why, p) = DecideScriptQuestion(avs, expBits, true, false, false, true, false);
        C(g && why.Contains("region"), "region/parcel allowed -> grant");

        (g, why, p) = DecideScriptQuestion(avs, expBits, true, false, true, true, true);
        C(!g && why.Contains("blocked"), "prefs Block wins over allowlist/region");

        (g, why, p) = DecideScriptQuestion(other, expBits, true, false, false, false, false);
        C(!g, "unknown experience, not region-allowed -> deny");

        (g, why, p) = DecideScriptQuestion(avs, withDebit, true, false, false, false, true);
        C(g && (p & ScriptPermission.Debit) == 0 && (p & ScriptPermission.Attach) != 0, "Debit stripped from grant");

        (g, why, p) = DecideScriptQuestion(UUID.Zero, ScriptPermission.Attach, true, false, false, false, false);
        C(g && p == ScriptPermission.Attach && why.Contains("classic"), "classic Attach while seated -> grant");

        (g, why, p) = DecideScriptQuestion(UUID.Zero, ScriptPermission.Attach | ScriptPermission.TriggerAnimation, true, false, false, false, false);
        C(g && (p & ScriptPermission.TriggerAnimation) != 0, "classic Attach+TriggerAnimation while seated -> grant both");

        (g, why, p) = DecideScriptQuestion(UUID.Zero, ScriptPermission.Attach, false, false, false, false, false);
        C(!g, "classic Attach while standing -> deny");

        (g, why, p) = DecideScriptQuestion(UUID.Zero, ScriptPermission.Debit, true, false, false, false, false);
        C(!g, "classic Debit-only -> deny");

        (g, why, p) = DecideScriptQuestion(UUID.Zero, ScriptPermission.ControlCamera, true, false, false, false, false);
        C(!g, "classic without Attach -> deny");

        // 10:38 case: experience bitmask 408628 (== 0x63C34) with allowlist
        var raw = (ScriptPermission)408628;
        (g, why, p) = DecideScriptQuestion(avs, raw, true, false, false, false, true);
        C(g && (p & ScriptPermission.Attach) != 0 && (p & ScriptPermission.Debit) == 0, "408628 experience bitmask (Dutchie coffeemug) -> grant without Debit");

        C(!IsTempAttachSource(UUID.Zero), "UUID.Zero is not a temp source");
        return $"exp selftest: {pass} PASS, {fail} FAIL\n" + sb.ToString().TrimEnd();
    }
}
