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
    // Couples / intimate submenu / pose names (skip on auto paths). Includes Mirage-style Fist* (2026-10-06: wander
    // picked Fist* > fist 2 labeled [solo] but it was a couples anim). Explicit 'pose couples' may still enter these.
    // 2026-10-07 19:39 (Beach Hammock ADULT: Adults* > 5Adults picked as solo): letter-only boundaries so digits next
    // to the word still match ("5Adults", "Couple2"); adds mirror / love / hers / M&F-pair terms.
    static readonly Regex PoseCouplesName = new(@"(?<![a-z])(couples?|cuddl\w*|kiss\w*|hugs?|hugging|spoon\w*|snuggl\w*|romanc\w*|lap|together|partners?|duo|pair\w*|[2-9]\s*p|fist|spank\w*|sex|lovemak\w*|love(?:rs?)?|intimate|make.?out|grind|straddl\w*|entwine|entangl\w*|mirror\w*|hers|his\s*(?:&|and|n)\s*hers|him|for\s+her)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Solo submenu names (Trompe Loeil Reiley etc. use SINGLE*).
    // Prefix match so "SinglesSet*" counts (19:39 hammock); Sit / Relax-type menus count as solo too.
    static readonly Regex PoseSoloMenuName = new(@"(?<![a-z])(?:singles?|solo|alone|relax|(?:sit(?:s|ting)?|one\s*p|1p)(?![a-z]))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Adult / multi-avatar menus to skip when auto-picking PG solo after David leaves.
    // Seat object names like "... Beach Hammock ADULT": skipped for solo wander sits unless a singles menu was seen on it.
    static readonly Regex SeatAdultName = new(@"(?<![a-z])adults?(?![a-z])|xxx|nsfw", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> poseSinglesSeen = new(StringComparer.OrdinalIgnoreCase);
    // pure: may a solo wander sit use this seat? (ADULT-named only with a confirmed singles/solo menu)
    // 2026-10-16 David: every piece of furniture, even one with "adult" in its name, has at least one non-adult pose. The name no
    // longer rejects a seat: the menu is opened and a pose that the name-based rules rate non-adult is picked (PoseRules.cs); a seat
    // with none is skipped after sitting (PoseSeatClearPgByName, SkipSeatMarker). Kept as a function for callers / tests.
    public static bool SoloSeatNameOk(string name, bool singlesConfirmed) => true;
    internal const string SkipSeatMarker = "[skip-seat: no clearly non-adult pose]";
    // pure: did the seat give a pose that is clearly non-adult by name? pickFound = the picker found a non-adult button;
    // else the pose already showing (the seat's default) counts when it is plain by name and not under an adult menu.
    internal static bool PoseSeatClearPgByName(bool pickFound, string current, IEnumerable<string> currentPath, bool adultSeat) =>
        pickFound || (!string.IsNullOrWhiteSpace(current) && !PoseIsSexualNamed(current) && !PosePathIsSexual(currentPath ?? Enumerable.Empty<string>(), adultSeat));
    // pure: does this dialog offer a solo submenu (Singles*/SinglesSet*/Solo*/Sit*/Relax*)?
    public static bool PoseMenuHasSoloSubmenu(IReadOnlyList<string> labels) =>
        labels != null && labels.Any(b => (b ?? "").TrimEnd().EndsWith("*") && !PoseIsControl(b) && PoseIsSoloMenu(b) && !PoseIsCouplesNamed(b) && !PoseIsAdultMenu(b));
    static bool SeatSinglesConfirmed(UUID id, string name) =>
        poseSinglesSeen.ContainsKey(id.ToString()) || (!string.IsNullOrEmpty(name) && poseSinglesSeen.ContainsKey("name:" + name));
    // David 2026-09-25: never pick male poses
    static readonly Regex PoseMaleSkip = new(@"\b(male|males|men|man|guy|guys|boy|boys|him|his|masc\w*)\b|(^|[\s(\[_/-])m(\d+|(?=[\s)\]_*/-])|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly ConcurrentQueue<(DateTime at, ScriptDialogEventArgs e)> wDialogs = new();
    static string wLastPose = "-";
    static List<string> wLastPosePath = new(); // last AVsitter menu path we pressed/restored
    static List<string> wLastSoloPosePath = new(); // last non-couples path on this seat (for David-left restore)
    // Leaf pose name -> came from a couples menu path (Magnetize under Couples PG*, etc.)
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> poseLeafFromCouples = new(StringComparer.OrdinalIgnoreCase);
    // 2026-10-16 David: every seat has its own default pose names, so NO name matching. The seat's default is the singles pose it
    // gives when I first sit down by myself (the pose showing right after sitting alone, before any change). Recorded per seat
    // (root prim id) by SeatPose on the initial, unchanged sit; restored when David leaves and there is no clearly solo option.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> poseSitDefault = new();
    // pure: may this leaf be a default / fallback pose? Never a submenu, control, male, couples or (unless allowed) sexual entry.
    internal static bool PoseUsableAsDefault(string l, bool allowSexual = false) =>
        !string.IsNullOrWhiteSpace(l) && !l.TrimEnd().EndsWith("*") && !PoseIsControl(l) && !PoseIsMale(l)
        && !PoseIsCouplesNamed(l) && (allowSexual || !PoseIsAdultMenu(l)) && !PoseIsBackButton(l);
    // pure: record the pose active right after sitting alone as the seat's default (only a usable, non-sexual single pose)
    internal static bool PoseRecordSitDefault(string seatKey, string current, bool alone, bool changeOrRecovery, bool currentIsCouples = false)
    {
        if (string.IsNullOrEmpty(seatKey) || string.IsNullOrWhiteSpace(current) || !alone || changeOrRecovery || currentIsCouples) return false;
        if (!PoseUsableAsDefault(current)) return false;
        poseSitDefault[seatKey] = PoseBare(current);
        return true;
    }
    // pure: is the open menu a couples / adult submenu (not the root)? Then a solo pick must start from the root menu.
    internal static bool PoseNeedsRootSearch(IReadOnlyList<string> labels, bool currentIsCouples)
    {
        if (labels == null || labels.Count == 0) return false;
        if (PoseMenuLooksLikeRoot(labels) || PoseMenuLooksLikeSeatSelect(labels)) return false;
        if (!PoseHasBack(labels)) return false;
        return currentIsCouples || labels.Any(l => PoseIsCouplesNamed(l) || PoseIsAdultMenu(l));
    }
    // pure: which button of the seat's root pose menu restores the default? 1) the recorded sit-alone pose,
    // else 2) the first non-sexual single pose on the menu (logged by the caller). null = none at root.
    internal static (string pick, string why) PickDefaultPoseButton(IReadOnlyList<string> rootLabels, string recordedDefault, bool allowSexual = false)
    {
        var labels = rootLabels ?? new List<string>();
        // 2026-10-16 13:43: never pick from a page that is not the top-level menu (a CUDDLES* page gave 'head massage', a couples pose)
        if (!PoseMenuIsTopLevel(labels)) return (null, "not the top-level menu (has [BACK])");
        if (!string.IsNullOrWhiteSpace(recordedDefault))
        {
            var rec = labels.FirstOrDefault(l => PoseUsableAsDefault(l, allowSexual) && string.Equals(PoseBare(l), PoseBare(recordedDefault), StringComparison.OrdinalIgnoreCase));
            if (rec != null) return (rec, $"the seat's pose from when I first sat down alone [{PoseBare(recordedDefault)}]");
        }
        var first = labels.FirstOrDefault(l => PoseUsableAsDefault(l, allowSexual));
        if (first != null) return (first, string.IsNullOrWhiteSpace(recordedDefault) ? "no sit-alone pose recorded for this seat; first non-sexual single pose" : $"recorded sit-alone pose [{PoseBare(recordedDefault)}] not on the root menu; first non-sexual single pose");
        return (null, "no default pose on the root menu");
    }

    static bool? poseSeatShared; // last seen shared-seat state while seated (any other sitter)
    static bool poseDavidOnSeat; // David Nightingale specifically on this seat with me
    static DateTime poseSharedChangedAt = DateTime.MinValue; // alone<->shared flip (recovery grace)
    static int poseDavidLeftSwitchGen; // debounce auto-solo when David leaves
    // David 2026-10-05: after a partner sits/stands, AVsitter often restarts anims; wait before "pose lost" recovery.
    public static double PoseSharedFlipGraceS = 15;
    public static double PoseRecoveryMissingS = 8; // was 3; brief AVsitter swaps were false positives

    // David 2026-10-12: per-seat poses wander never picks (props that cannot line up with her). seat id -> pose names.
    // Nutmeg. Porch Rocking Chair: f_reading rezzes its book where no avatar could read it (phone poses stay allowed).
    internal static readonly Dictionary<UUID, string[]> SeatPoseSkip = new()
    {
        [new UUID("c78d42b6-c75c-df84-aa8c-8f78cbd2cb6c")] = new[] { "f_reading" },
        // David 2026-10-12 15:50: Dutchie maledom desk only in the submissive role: never [SWAP] (would put her in the dom seat)
        [new UUID("1b9ab440-3b52-b827-2eae-7de92fb69cfb")] = new[] { "[SWAP]", "SWAP" },
    };
    internal static readonly UUID DutchieDesk = new("1b9ab440-3b52-b827-2eae-7de92fb69cfb");
    // dom-role buttons on sub-only seats are dropped too (dom / master / male role names)
    static readonly Regex DomRoleName = new(@"(?<![a-z])(dom\w*|master|mistress|male|sir)(?![a-z])|\(m\)", RegexOptions.IgnoreCase);
    internal static bool SeatIsSubOnly(UUID seat) => seat == DutchieDesk;
    // pure: menu labels minus this seat's skipped poses (case-insensitive, '*' submenu marker ignored)
    internal static List<string> SeatPoseFilter(UUID seat, List<string> labels)
    {
        if (labels == null) return labels;
        if (SeatIsSubOnly(seat)) labels = labels.Where(l => !DomRoleName.IsMatch(l ?? "")).ToList();
        if (!SeatPoseSkip.TryGetValue(seat, out var skip)) return labels;
        return labels.Where(l => !skip.Any(x => string.Equals(PoseBare(l ?? ""), x, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    // David 2026-10-12 15:30: sexual poses NEVER while an android outfit is worn; in a human outfit only when the
    // only other avatar in Peronaut itself is David. 2026-10-14: neighbour regions no longer count (an avatar
    // across the sim border isn't "around"); only the current sim's coarse map and its avatar objects. 2026-10-16: only our own
    // parcel counts (AloneCheck in ParcelWatch.cs); this region-wide test is its fail-safe when parcel data is missing. Taking one =
    // stand, wear Naked (Real Kisser stays), sit again, then the pose. Couples poses stay skipped on solo paths either way.
    internal static bool SexualPosesAllowed(bool androidWorn, string region, IEnumerable<UUID> otherAvatars, UUID davidId)
    {
        if (androidWorn) return false;
        if (!string.Equals(region, "Peronaut", StringComparison.OrdinalIgnoreCase)) return false;
        var o = (otherAvatars ?? Enumerable.Empty<UUID>()).Distinct().ToList();
        return o.Count == 1 && o[0] == davidId;
    }
    /// <summary>Pure: other avatars in the sim with handle <paramref name="current"/> only: its coarse-location ids
    /// plus its avatar objects; neighbour regions' entries are ignored.</summary>
    internal static List<UUID> AvatarsInSim(ulong current, IEnumerable<KeyValuePair<ulong, IEnumerable<UUID>>> coarse,
        IEnumerable<KeyValuePair<ulong, IEnumerable<UUID>>> objects, UUID me)
    {
        var ids = new HashSet<UUID>();
        foreach (var src in new[] { coarse, objects })
            foreach (var kv in src ?? Enumerable.Empty<KeyValuePair<ulong, IEnumerable<UUID>>>())
                if (kv.Key == current) foreach (var k in kv.Value ?? Enumerable.Empty<UUID>()) if (k != me && k != UUID.Zero) ids.Add(k);
        return ids.ToList();
    }
    static List<UUID> OtherAvatarsAround()
    {
        var cur = client.Network.CurrentSim; if (cur == null) return new List<UUID>();
        var coarse = new List<KeyValuePair<ulong, IEnumerable<UUID>>>(); var objs = new List<KeyValuePair<ulong, IEnumerable<UUID>>>();
        try { foreach (var kv in coarseBy.ToArray()) coarse.Add(new(kv.Key, kv.Value.Keys.ToList())); } catch { }
        try { foreach (var sim in client.Network.Simulators.ToArray()) objs.Add(new(sim.Handle, sim.ObjectsAvatars.Values.Where(a => a != null).Select(a => a.ID).ToList())); } catch { }
        return AvatarsInSim(cur.Handle, coarse, objs, client.Self.AgentID);
    }
    // 2026-10-16: "alone" = nobody else on OUR OWN PARCEL (ParcelWatch.cs); no parcel data -> the strict region-wide count (OtherAvatarsAround)
    static bool SexualPosesAllowedNow() { try { return AloneCheckNow().Allowed; } catch { return false; } }
    internal static bool PosePathIsSexual(IEnumerable<string> path) => (path ?? Enumerable.Empty<string>()).Any(l => PoseIsSexualNamed(l) || PoseRules.Current.IsAdultMenuName(PoseBare(l)));

    /// <summary>Pure: true when the menu path is sexual and the android / alone rule does not allow it. Applies to every
    /// way a button can be pressed (wander, pose change, explicit 'pose couples', 'pose path', recovery restore).</summary>
    internal static bool PoseSexualRefused(IEnumerable<string> path, bool allowSexual, bool adultSeat = false) => !allowSexual && PosePathIsSexual(path, adultSeat);

    // 2026-10-16: classification lives in PoseRules.cs (one data-driven place; a future pose catalog file replaces it).
    // A seat is adult when its NAME says so or when a menu seen on it has an adult name (PoseSeatIsAdult); its unclear
    // submenu names then count as sexual. Plain PG seats are unaffected.
    internal static bool PoseAdultSeatName(string seatName) => PoseRules.Current.SeatNameAdult(seatName);
    internal static bool PoseSubmenuTrustedPg(string label) => PoseRules.Current.SubmenuTrusted(PoseBare(label));
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> poseSeatAdultMenus = new();
    // pure-ish: remember that a menu of this seat had an adult-named button (kept for the session)
    internal static bool PoseNoteSeatMenu(string seatKey, IEnumerable<string> labels)
    {
        if (!string.IsNullOrEmpty(seatKey) && (labels ?? Enumerable.Empty<string>()).Any(l => !PoseIsBackButton(l) && PoseIsAdultMenu(l))) poseSeatAdultMenus[seatKey] = true;
        return !string.IsNullOrEmpty(seatKey) && poseSeatAdultMenus.ContainsKey(seatKey);
    }
    internal static bool PoseSeatIsAdult(string seatKey, string seatName) => PoseAdultSeatName(seatName) || (!string.IsNullOrEmpty(seatKey) && poseSeatAdultMenus.ContainsKey(seatKey));
    // the menu path is sexual: any step (submenu or pose) has a sexual / adult name, so a pose inherits it from its parent menu
    // (Solo* > Masturbate* > Jiller). On an adult-looking seat a submenu step with an unknown (not plain) name counts too.
    internal static bool PosePathIsSexual(IEnumerable<string> path, bool adultSeat)
    {
        var l = (path ?? Enumerable.Empty<string>()).ToList();
        if (PosePathIsSexual(l)) return true;
        return adultSeat && l.Take(Math.Max(0, l.Count - 1)).Any(x => x != null && x.TrimEnd().EndsWith("*") && !PoseSubmenuTrustedPg(x))
            || adultSeat && l.Count > 0 && l[^1] != null && l[^1].TrimEnd().EndsWith("*") && !PoseSubmenuTrustedPg(l[^1]);
    }

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

    // 2026-10-16: record the seat's sit-alone default pose from the dialog header of the FIRST menu after I sit (any way of sitting: wander,
    // 'sit', quick relog beside the bed ...), not only when SeatPose runs: the header "[Female seat] [F waits]" names the pose AVsitter started.
    // Only when alone on the seat, no pose pressed since the sit, and the header pose is a usable (non-couples / male / sexual) single pose.
    // 2026-10-16 14:00 (Mirage bed, sit cdedb159, header '[Female seat] [F waits]', nothing recorded, nothing logged): the AVsitter menu reaches me
    // ~1 s after the sit response, often BEFORE the object update that sets Self.SittingOn, and the hook just returned when SittingOn was 0.
    // Now: the sit anchor comes from the sit response AND from AvatarSitChanged (so every way of sitting counts); a menu that arrives before
    // the sit state is known is kept and retried (sit-changed event + a few timed retries); every decline is logged with its reason; the
    // recorded default is persisted (seat-default-poses.json next to the log) so it survives relogs and restarts.
    static DateTime poseSitRespAt = DateTime.MinValue; static string poseSitRespPath = ""; static string poseSitAnchorSrc = "";
    static bool poseSitDefaultDone; static string poseSitLastReason = "";
    static readonly HashSet<string> poseSitNoAnchorLogged = new();
    static ScriptDialogEventArgs poseSitPending; static DateTime poseSitPendingAt = DateTime.MinValue; static int poseSitRetryGen;
    static readonly object poseSitLock = new();
    internal const double PoseSitWindowS = 12, PoseSitEarlyDialogS = 5;
    internal static void NotePoseSitResponse(UUID target = default)
    {
        lock (poseSitLock)
        {
            poseSitRespAt = DateTime.Now; poseSitRespPath = string.Join(">", wLastPosePath); poseSitAnchorSrc = "sit response";
            poseSitDefaultDone = false; poseSitPending = null; poseSitLastReason = ""; poseSitNoAnchorLogged.Clear(); poseSitRetryGen++;
        }
    }
    // AvatarSitChanged for me (any way of sitting incl. a login on the seat). Stand-up clears the anchor.
    internal static void NotePoseSitChanged(uint seatLocalId)
    {
        ScriptDialogEventArgs pend = null;
        lock (poseSitLock)
        {
            if (seatLocalId == 0)
            {
                poseSitRespAt = DateTime.MinValue; poseSitDefaultDone = false; poseSitPending = null; poseSitLastReason = ""; poseSitNoAnchorLogged.Clear(); poseSitRetryGen++;
                return;
            }
            if (poseSitRespAt == DateTime.MinValue || (DateTime.Now - poseSitRespAt).TotalSeconds > 20)
            {
                poseSitRespAt = poseSitPending != null && (DateTime.Now - poseSitPendingAt).TotalSeconds < PoseSitEarlyDialogS ? poseSitPendingAt : DateTime.Now;
                poseSitRespPath = string.Join(">", wLastPosePath); poseSitAnchorSrc = "sit changed"; poseSitDefaultDone = false; poseSitNoAnchorLogged.Clear();
            }
            pend = poseSitPending;
        }
        if (pend != null) PoseSitDefaultEval(pend, poseSitPendingAt, "sit changed");
    }
    internal static bool PoseSitDefaultWindowOk(DateTime sitAt, DateTime now, string pathAtSit, string pathNow) =>
        sitAt != DateTime.MinValue && (now - sitAt).TotalSeconds <= PoseSitWindowS && pathAtSit == pathNow;

    // pure: may the first pose menu after a sit be recorded as this seat's sit-alone default? (record, conclusive, pose, reason).
    // conclusive=false means "ask again later" (sit state not known yet, another object's dialog, seat-select menu).
    internal static (bool record, bool conclusive, string cur, string reason) PoseSitDefaultCheck(
        bool anchored, double dialogSinceSitS, bool pathSame, string pathAtSit, string pathNow, bool sitting, bool fromSeat,
        string message, IReadOnlyList<string> labels, bool alone, bool headerChangedSince)
    {
        if (!anchored) return (false, false, null, "no sit seen (no sit response / sit change in the last 12 s)");
        if (dialogSinceSitS > PoseSitWindowS || dialogSinceSitS < -PoseSitEarlyDialogS) return (false, true, null, $"window: this menu came {dialogSinceSitS:0.#} s after the sit (limit {PoseSitWindowS:0} s), so it is not the first menu");
        if (!pathSame) return (false, true, null, $"pressed: a pose was pressed since the sit (path [{pathAtSit}] -> [{pathNow}])");
        if (headerChangedSince) return (false, true, null, "pressed: the menu header changed after this first menu (a pose was picked)");
        if (!sitting) return (false, false, null, "not sitting yet (Self.SittingOn is 0); will retry");
        if (!fromSeat) return (false, false, null, "dialog is from another object, not my seat");
        if (labels == null || labels.Count == 0) return (false, true, null, "menu has no buttons");
        if (PoseMenuLooksLikeSeatSelect(labels)) return (false, false, null, "seat-select menu (F/M), waiting for the pose menu");
        if (PoseHasBack(labels)) return (false, true, null, "menu has [BACK]: a submenu, not the first menu");
        if (labels.Any(PoseIsCouplesNamed) && !labels.Any(PoseIsSoloMenu)) return (false, true, null, "couples menu (couples names, no solo submenu)");
        var cur = PoseCurrent(message);
        if (string.IsNullOrWhiteSpace(cur)) return (false, true, null, $"header parse: no [pose] in the dialog text '{(message ?? "").Replace("\n", " / ")}'");
        if (!alone) return (false, true, cur, $"alone: someone else is on the seat (a default is only recorded when I sit alone), header pose [{cur}]");
        if (!PoseUsableAsDefault(cur) || PoseLeafIsCouples(cur, null)) return (false, true, cur, $"pose [{cur}] is not usable as a default (submenu, control, male, couples or sexual)");
        return (true, true, cur, "ok");
    }

    static bool PoseSitNoAnchorFirst(string k) { lock (poseSitLock) return poseSitNoAnchorLogged.Add(k); }
    static void PoseRecordSitDefaultFromDialog(ScriptDialogEventArgs e) => PoseSitDefaultEval(e, DateTime.Now, "dialog");

    static void PoseSitDefaultEval(ScriptDialogEventArgs e, DateTime dialogAt, string how)
    {
        try
        {
            DateTime anchorAt; string pathAtSit, anchorSrc; bool done; int gen;
            lock (poseSitLock) { anchorAt = poseSitRespAt; pathAtSit = poseSitRespPath; anchorSrc = poseSitAnchorSrc; done = poseSitDefaultDone; gen = poseSitRetryGen; }
            uint sitLocal = client?.Self?.SittingOn ?? 0;
            bool anchored = anchorAt != DateTime.MinValue;
            if (done) return;                                   // first menu of this sit already decided
            var sim = Sim; Primitive p = null;
            if (sitLocal != 0 && sim != null && sim.ObjectsPrimitives.TryGetValue(sitLocal, out var sp) && sp != null)
            {
                p = sp;
                if (p.ParentID != 0 && sim.ObjectsPrimitives.TryGetValue(p.ParentID, out var root) && root != null) p = root;
            }
            // only menus that look like a seat pose header ("[Seat] [pose]" + buttons) are of interest; others stay silent
            if (e?.ButtonLabels == null || e.ButtonLabels.Count == 0 || PoseCurrent(e.Message) == null) return;
            if (!anchored)
            {   // sitting with no anchor (e.g. sat before the client started): say so once per seat, never record
                if (sitLocal != 0 && p != null && DialogFromSeat(e, p, sim) && PoseSitNoAnchorFirst(p.ID.ToString()))
                    WLog($"POSE '{p.Properties?.Name ?? p.ID.ToString()}': sit-alone default not recorded: no sit seen in the last {PoseSitWindowS:0} s (menu header [{PoseCurrent(e.Message)}])");
                return;
            }
            string pathNow = string.Join(">", wLastPosePath);
            var curHdr = PoseCurrent(e.Message);
            bool changedSince = wDialogs.ToArray().Any(d => d.at > dialogAt && d.e != e && d.e.ObjectID == e.ObjectID && d.e.ButtonLabels != null
                && !string.Equals(PoseCurrent(d.e.Message), curHdr, StringComparison.OrdinalIgnoreCase));
            bool fromSeat = p != null && DialogFromSeat(e, p, sim);
            bool alone = p != null && !SeatHasOtherSitters(p) && !SeatHasDavid(p);
            var (rec, conclusive, cur, reason) = PoseSitDefaultCheck(true, (dialogAt - anchorAt).TotalSeconds, pathAtSit == pathNow, pathAtSit, pathNow,
                sitLocal != 0 && p != null, fromSeat, e.Message, e.ButtonLabels, alone, changedSince);
            string seatName = p?.Properties?.Name ?? e.ObjectName ?? "?";
            if (rec)
            {
                lock (poseSitLock) { if (gen != poseSitRetryGen || poseSitDefaultDone) return; poseSitDefaultDone = true; poseSitPending = null; poseSitRetryGen++; }
                if (PoseRecordSitDefault(p.ID.ToString(), cur, true, false, false))
                {
                    poseSitDefaultName[p.ID.ToString()] = seatName;
                    PoseSitDefaultSave();
                    WLog($"POSE '{seatName}': recorded sit-alone default pose [{cur}] from the first menu header after sitting ({anchorSrc}, {how}, {(dialogAt - anchorAt).TotalSeconds:0.0} s after the sit)");
                }
                return;
            }
            if (conclusive)
            {
                lock (poseSitLock) { if (gen != poseSitRetryGen || poseSitDefaultDone) return; poseSitDefaultDone = true; poseSitPending = null; poseSitRetryGen++; }
                WLog($"POSE '{seatName}': sit-alone default NOT recorded ({how}): {reason}");
                return;
            }
            // not conclusive: keep the menu and look again (sit state, seat-select -> pose menu ...)
            bool start = false;
            lock (poseSitLock)
            {
                if (poseSitPending == null || !ReferenceEquals(poseSitPending, e)) { poseSitPending = e; poseSitPendingAt = dialogAt; start = true; }
                if (reason != poseSitLastReason) { poseSitLastReason = reason; WLog($"POSE '{seatName}': sit-alone default not recorded yet ({how}): {reason}"); }
            }
            if (start && how == "dialog") PoseSitRetryStart(e, dialogAt, gen);
        }
        catch (Exception ex) { WLog("POSE sit-alone default hook error: " + ex.GetBaseException().Message); }
    }
    static void PoseSitRetryStart(ScriptDialogEventArgs e, DateTime dialogAt, int gen)
    {
        _ = Task.Run(async () =>
        {
            foreach (var ms in new[] { 400, 800, 1500, 2500, 3500 })
            {
                await Task.Delay(ms);
                lock (poseSitLock) { if (gen != poseSitRetryGen || poseSitDefaultDone || !ReferenceEquals(poseSitPending, e)) return; }
                PoseSitDefaultEval(e, dialogAt, "retry");
            }
            lock (poseSitLock)
            {
                if (gen != poseSitRetryGen || poseSitDefaultDone || !ReferenceEquals(poseSitPending, e)) return;
                poseSitDefaultDone = true; poseSitPending = null;
            }
            WLog($"POSE '{e.ObjectName}': sit-alone default NOT recorded: gave up after retries, last reason: {poseSitLastReason}");
        });
    }

    // ---- persistence: the recorded per-seat default survives relogs/restarts (seat root UUID -> pose) ----
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> poseSitDefaultName = new();
    static string PoseSitDefaultFile => Env("GT_SEAT_DEFAULTS", Path.Combine(Path.GetDirectoryName(LogPath) ?? "/tmp", "seat-default-poses.json"));
    internal static string PoseSitDefaultsToJson(IDictionary<string, string> poses, IDictionary<string, string> names)
    {
        var o = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var kv in poses) o[kv.Key] = new Dictionary<string, string> { ["pose"] = kv.Value, ["seat"] = names != null && names.TryGetValue(kv.Key, out var n) ? n : "" };
        return System.Text.Json.JsonSerializer.Serialize(o, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
    internal static Dictionary<string, (string pose, string seat)> PoseSitDefaultsFromJson(string json)
    {
        var res = new Dictionary<string, (string, string)>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var pr in doc.RootElement.EnumerateObject())
            {
                string pose = pr.Value.ValueKind == System.Text.Json.JsonValueKind.String ? pr.Value.GetString()
                    : pr.Value.TryGetProperty("pose", out var pe) ? pe.GetString() : null;
                string seat = pr.Value.ValueKind == System.Text.Json.JsonValueKind.Object && pr.Value.TryGetProperty("seat", out var se) ? se.GetString() : "";
                if (!string.IsNullOrWhiteSpace(pose) && PoseUsableAsDefault(pose)) res[pr.Name] = (PoseBare(pose), seat ?? "");
            }
        }
        catch { }
        return res;
    }
    static void PoseSitDefaultSave()
    {
        try
        {
            var f = PoseSitDefaultFile; Directory.CreateDirectory(Path.GetDirectoryName(f) ?? ".");
            var tmp = f + ".tmp"; File.WriteAllText(tmp, PoseSitDefaultsToJson(poseSitDefault, poseSitDefaultName)); File.Move(tmp, f, true);
        }
        catch (Exception ex) { WLog("POSE sit-alone default save failed: " + ex.Message); }
    }
    internal static void PoseSitDefaultSaveForTest(string key, string name) { poseSitDefaultName[key] = name; PoseSitDefaultSave(); }
    internal static void PoseSitDefaultLoad()
    {
        try
        {
            var f = PoseSitDefaultFile; if (!File.Exists(f)) return;
            int n = 0;
            foreach (var kv in PoseSitDefaultsFromJson(File.ReadAllText(f)))
            { poseSitDefault.TryAdd(kv.Key, kv.Value.pose); poseSitDefaultName[kv.Key] = kv.Value.seat; n++; }
            if (n > 0) WLog($"POSE loaded {n} recorded seat default pose(s) from {f}");
        }
        catch (Exception ex) { WLog("POSE sit-alone default load failed: " + ex.Message); }
    }

    static void WanderDialogIn(ScriptDialogEventArgs e)
    {
        wDialogs.Enqueue((DateTime.Now, e));
        PoseRecordSitDefaultFromDialog(e);
        if (PoseMenuHasSoloSubmenu(e.ButtonLabels))
        {
            poseSinglesSeen[e.ObjectID.ToString()] = true;
            if (!string.IsNullOrEmpty(e.ObjectName)) poseSinglesSeen["name:" + e.ObjectName] = true;
        }
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
    // David 2026-10-12 15:24 (Lalou Ultra Sofa: Solo > Solo Couch > Sexy sit picked by solo wander): sexual / suggestive
    // pose and menu NAMES are never auto-picked, even inside a Solo menu. Word list from the Lalou catalogue's sexual names;
    // strong words match inside run-together names too (FoldFuck, CreamyCunt, Analwork, DeepThroat).
    static bool PoseIsSexualNamed(string raw) => PoseRules.Current.IsSexualName(PoseBare(raw));
    static bool PoseIsAdultMenu(string raw) => PoseRules.Current.IsAdultMenuName(PoseBare(raw)) || PoseIsSexualNamed(raw);
    // 2026-10-16 David: conversation poses (Reiley Net Chair PG: SINGLE* > Talk) are for talking to him from the paired seat. Data: PoseRules.cs.
    internal static bool PoseIsConversation(string raw) => PoseRules.Current.IsConversationName(PoseBare(raw));
    internal const float DavidSeatedNearM = 3.5f;
    // pure: David sits close enough to be talked to (on my seat, or sitting somewhere within 3.5 m)
    internal static bool DavidSeatedNear(bool onMySeat, bool davidSitting, float distM) => onMySeat || (davidSitting && distM <= DavidSeatedNearM);
    static bool DavidSeatedNearNow()
    {
        try
        {
            if (DavidOnMySeatNow()) return true;
            var sim = client?.Network?.CurrentSim; if (sim == null) return false;
            foreach (var a in sim.ObjectsAvatars.Values)
                if (a != null && a.ID == DavidId) return DavidSeatedNear(false, a.ParentID != 0, Vector3.Distance(a.Position, client.Self.SimPosition));
        }
        catch { }
        return false;
    }
    static bool PoseIsMale(string raw) => PoseMaleSkip.IsMatch(PoseBare(raw));
    // 2026-10-16 13:43 (Mirage bed): "[<<]" / "[>>]" are the PAGE buttons of a long menu, not [BACK]. Treating "[<<]" as BACK pressed
    // "previous page" (CUDDLES page 1 -> page 2) and the page was mistaken for the root. Only a real [BACK] button goes up a level.
    internal static bool PoseIsBackButton(string raw)
    {
        var b = PoseBare(raw).Trim().TrimStart('[').TrimEnd(']').Trim();
        return b.Equals("BACK", StringComparison.OrdinalIgnoreCase);
    }
    // pure: index of the button that goes up one menu level, -1 = none (= this is the top-level menu).
    // [BACK] always; a bare "<<" only on a menu that has no ">>" (a "<<"/">>" pair is paging).
    internal static int PoseBackIndex(IReadOnlyList<string> labels)
    {
        if (labels == null) return -1;
        for (int i = 0; i < labels.Count; i++) if (PoseIsBackButton(labels[i])) return i;
        static bool Is(string l, string w) => (l ?? "").Trim().TrimStart('[').TrimEnd(']').Trim() == w;
        if (!labels.Any(l => Is(l, ">>"))) for (int i = 0; i < labels.Count; i++) if (Is(labels[i], "<<")) return i;
        return -1;
    }
    internal static bool PoseHasBack(IReadOnlyList<string> labels) => PoseBackIndex(labels) >= 0;
    // pure: is this the top-level pose menu? The category-root heuristic (Solo* + Couples* siblings, which may still show a [BACK] to the
    // seat-select), or simply a menu without any [BACK] that is not the seat-select chrome. Never by exact names.
    internal static bool PoseMenuIsTopLevel(IReadOnlyList<string> labels)
    {
        if (labels == null || labels.Count == 0) return false;
        if (PoseMenuLooksLikeRoot(labels)) return true;
        if (PoseMenuLooksLikeSeatSelect(labels)) return false;
        return !PoseHasBack(labels);
    }
    // pure core of 'go to the root': press [BACK] on the current menu (re-reading it each time) until it is the top-level menu.
    // press(index,label) returns the new menu labels (null = no dialog came back). Depth is NOT assumed: 0..cap levels all work.
    // atRoot=false => gave up (cap hit / stuck / no dialog); note says why.
    internal static async Task<(List<string> labels, bool atRoot, int presses, string note)> PoseBackToRootCore(
        List<string> labels, Func<int, string, Task<List<string>>> press, int cap = 8)
    {
        int presses = 0;
        for (int i = 0; i < cap; i++)
        {
            if (labels == null) return (labels, false, presses, "no menu");
            if (PoseMenuIsTopLevel(labels)) return (labels, true, presses, presses == 0 ? "already at the top-level menu" : $"top-level menu after {presses} BACK press(es)");
            int back = PoseBackIndex(labels);
            if (back < 0) return (labels, false, presses, "no [BACK] and not a pose menu (seat-select?)");
            var next = await press(back, labels[back]); presses++;
            if (next == null) return (labels, false, presses, "no dialog after pressing BACK");
            if (next.SequenceEqual(labels)) return (next, false, presses, "BACK did not change the menu (stuck)");
            labels = next;
        }
        return (labels, PoseMenuIsTopLevel(labels), presses, PoseMenuIsTopLevel(labels) ? "top-level menu" : $"gave up after {cap} BACK presses; menu is still not the top level");
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
    static bool SeatHasDavid(Primitive seat) => SitterListHasDavid(SeatSitterNames(seat)) || DavidOnMySeatNow();

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
        if (!was && davidHere)
        {
            Log("height", "pose: David sat on my seat (same linkset) — he chooses the pose; wander holds while he stays");
            DavidSeatHoldCheck(true, "he sat on the same linkset");   // StayWithDavid.cs
            // David 2026-10-13: shared furniture -> check roles (swaps only when clearly reversed: menu says so or he's >= 0.25 m above me)
            var seatNow = Sim?.ObjectsPrimitives.Values.FirstOrDefault(q => q != null && q.LocalID == RootSeatOf(client.Self.SittingOn, id => Sim.ObjectsPrimitives.TryGetValue(id, out var x) && x != null ? x.ParentID : 0));
            if (seatNow != null)
                _ = Task.Run(async () => { await Task.Delay(2000); await EnsureCoupleRoles(seatNow, seatNow.Properties?.Name ?? seatNow.ID.ToString(), "David joined my seat", false, CancellationToken.None); });
        }
        // 2026-10-13 David: react within ~2 s when he stands up from furniture we share (was: only after a couples path
        // she picked herself, else the 15 s grace + 8 s recovery = 24 s on the Reiley chair, 20:08:07 -> 20:08:31)
        if (was && !davidHere && client?.Self?.SittingOn != 0)
        {
            var gen = System.Threading.Interlocked.Increment(ref poseDavidLeftSwitchGen);
            Log("height", "pose: David left our seat — reacting in ~1.5 s (guard for an AVsitter re-seat/swap)");
            _ = Task.Run(() => ReactDavidLeftSeat(gen));
        }
    }

    // pure: which button to press (null = nothing suitable). submenu = AVsitter '*' label.
    // couplesMode: ONLY for explicit 'pose couples'. !couplesMode (default/recovery/wander): skip couples even if shared.
    // inSubmenu: already inside a submenu she chose (depth > 0); the "no clearly solo option" rule is for the top menu.
    internal static (string pick, bool submenu, string why) PickPoseButton(List<string> labels, string current, Random rnd, bool couplesMode = false, bool inCouplesMenu = false, bool preferPgSolo = false, bool inSubmenu = false, bool allowSexual = false, bool inSexualMenu = false, bool adultSeat = false, bool conversationOk = false)
    {
        var ok = new List<string>(); var skipped = new List<string>(); bool risky = false;
        bool seatSel = PoseMenuLooksLikeSeatSelect(labels);
        foreach (var raw in labels ?? new())
        {
            var l = raw ?? "";
            if (PoseIsControl(l) || PoseIsMale(l)) { skipped.Add(l); continue; }
            // Never pick AVsitter seat-select chrome (⊘sitter, bare F/M roles)
            if (l.TrimStart().StartsWith("⊘") || l.TrimStart().StartsWith("⊘")) { skipped.Add(l); continue; }
            if (seatSel) { skipped.Add(l); continue; }
            if (!conversationOk && PoseIsConversation(l)) { skipped.Add(l); continue; }   // Talk / Chat: only with David seated next to her
            // 2026-10-16: the android / alone rule holds in EVERY mode, explicit 'pose couples' included. Inside a sexual
            // menu (Fist*, Adult*, MF*...) nothing is picked, even entries whose own name looks harmless.
            if (!allowSexual && (inSexualMenu || PoseIsAdultMenu(l))) { skipped.Add(l); risky = true; continue; }
            // 2026-10-16 15:51 (Xplicit Makeup Table: Solo* > Masturbate* > Jiller picked as "pg-solo"): on an adult-looking seat a
            // submenu is only entered when its name is a known plain one (solo / sits / pictures / stand preen ...); an unknown
            // submenu name there is treated as sexual: skipped without permission, and with permission it is entered as a sexual path (naked first).
            if (adultSeat && !allowSexual && l.TrimEnd().EndsWith("*") && !PoseSubmenuTrustedPg(l)) { skipped.Add(l); risky = true; continue; }
            if (couplesMode)
            {
                if (!inCouplesMenu)
                {
                    if (PoseIsSoloMenu(l) || !PoseIsCouplesNamed(l)) { skipped.Add(l); continue; }
                }
            }
            else
            {
                // solo mode: never couples / adult / intimate names (19:39 Adults* > 5Adults)
                if (PoseIsCouplesNamed(l)) { skipped.Add(l); risky = true; continue; }
                if (PoseIsAdultMenu(l) && !allowSexual) { skipped.Add(l); risky = true; continue; }
            }
            ok.Add(l);
        }
        if (ok.Count == 0)
        {
            var mode = couplesMode ? (inCouplesMenu ? "couples submenu" : "couples (explicit)") : "solo";
            return (null, false, $"no suitable {mode} option (skipped: {string.Join(", ", skipped)})");
        }
        if (!couplesMode && !inCouplesMenu && !inSubmenu)
        {
            var soloAny = ok.Where(PoseIsSoloMenu).ToList();
            var soloSubs = soloAny.Where(l => l.TrimEnd().EndsWith("*")).ToList();
            // a menu mixing adult/couples options: only a clearly solo option, else stay in the default pose
            if (risky)
            {
                if (soloAny.Count == 0)
                    return (null, false, $"no clearly solo option next to adult/couples options (skipped: {string.Join(", ", skipped)}); staying in the default pose");
                ok = soloSubs.Count > 0 ? soloSubs : soloAny;
            }
            // prefer a Singles*/Solo*/Sit*/Relax* submenu at the top level when present
            else if (preferPgSolo && soloSubs.Count > 0) ok = soloSubs;
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
    static async Task<string> SeatPose(Primitive seat, string seatName, DateTime since, bool change, CancellationToken ct, bool explicitCouples = false, bool recovery = false, bool preferPgSolo = false, int overlapTry = 0)
    {
        // 2026-10-09 David: on a listed boat only the boat allowlist ever presses buttons (never SYSTEM*/drive controls)
        if (LoadHomeSeats().Any(i => i.Id == seat.ID && IsBoat(i)))
        {
            var (bok, bmsg) = await BoatPose(seat, seatName, ct);
            return $"'{seatName}' boat: {(bok ? "chose " : "no pose: ")}{bmsg}";
        }
        lastSeatPoseMenuAt = DateTime.Now; seatPoseMissingSince = null;
        bool shared = SeatHasOtherSitters(seat);
        bool davidHere = SeatHasDavid(seat);
        NotePoseSeatShared(shared, davidHere);
        bool couplesMode = AutoMayPickCouples(explicitCouples);
        bool adultSeat = PoseSeatIsAdult(seat.ID.ToString(), seatName);   // 2026-10-16: unclear submenu names on a seat with adult menus count as sexual
        if (recovery) couplesMode = false;
        // Wander / pose default / recovery: always prefer Solo*/SINGLE* and skip adult when a solo menu exists
        // (2026-10-06 Mirage Fist* was not couples-named and won over Solo* without preferPgSolo).
        if (!couplesMode) preferPgSolo = true;
        if (recovery && !davidHere && wLastPosePath.Count > 0 && !PosePathIsCouples(wLastPosePath))
        {
            var rest = await SeatPosePath(seat, seatName, wLastPosePath.ToList(), ct);
            if (rest.Contains("restored") || rest.Contains("already") || rest.Contains("chose"))
            { WLog("POSE recovery restore: " + rest); return rest; }
            WLog("POSE recovery: path restore failed (" + rest + "); falling back to solo/leave");
        }
        // ONLY while David himself shares the seat: he chooses poses (other sitters do not block solo)
        if (DavidChoosesPoses(davidHere, explicitCouples))   // 2026-10-09: recovery too (never re-assert her solo over his pick)
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
        // 2026-10-16: the pose showing on the initial, unchanged sit with nobody else on the seat = this seat's default (sit-alone) pose
        if (PoseRecordSitDefault(seat.ID.ToString(), current, !shared && !davidHere, change || recovery, PoseLeafIsCouples(current, wLastPosePath)))
        { poseSitDefaultName[seat.ID.ToString()] = seatName; PoseSitDefaultSave(); WLog($"POSE '{seatName}': recorded sit-alone default pose [{current}]"); }
        // 2026-10-16 13:28:32 (Mirage bed): a touch reopens the menu in the LAST submenu (CUDDLES*), and the solo pick only searched that
        // one ("no clearly solo option": hug you / in my eyes), never the SOLO F* submenu with selfie, champagne, texting, reading, laptop.
        // When the open menu is a couples / adult one, go BACK to the root first so the pick searches the whole tree like wander does.
        if (!couplesMode && PoseNeedsRootSearch(d.ButtonLabels, PoseLeafIsCouples(current, wLastPosePath)))
        {
            var rootD = await SeatPoseBackToRoot(seat, d, ct);
            if (rootD?.ButtonLabels != null && !PoseMenuIsTopLevel(rootD.ButtonLabels))
            {   // 2026-10-16: could not get to the real top level (cap / stuck / no dialog): do not search or pick from a middle page
                var mR = $"'{seatName}': could not reach the top-level menu from [{string.Join(" | ", d.ButtonLabels)}] (now [{string.Join(" | ", rootD.ButtonLabels)}]); keeping the current pose {current ?? "?"}";
                WLog("POSE " + mR); return mR;
            }
            if (rootD?.ButtonLabels != null && !ReferenceEquals(rootD, d))
            {
                WLog($"POSE '{seatName}': open menu is a couples/adult one [{string.Join(" | ", d.ButtonLabels)}]; searching the root menu [{string.Join(" | ", rootD.ButtonLabels)}] for a solo pose");
                d = rootD;
            }
        }
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
        bool allowSexual = SexualPosesAllowedNow();   // 2026-10-16: also for explicit 'pose couples' (it bypassed the android / alone rule)
        if (allowSexual) WLog($"POSE '{seatName}': sexual poses allowed (human outfit, only David around)");
        for (int depth = 0; depth < 3; depth++)
        {
            if (client.Self.SittingOn == 0 || (WanderOn && wanderPause != null)) { WLog($"POSE '{seatName}': stopped choosing (no longer seated or paused)"); return "aborted"; }
            shared = SeatHasOtherSitters(seat);
            davidHere = SeatHasDavid(seat);
            NotePoseSeatShared(shared, davidHere);
            if (DavidChoosesPoses(davidHere, explicitCouples))
            {
                var m = $"'{seatName}': David joined mid-pick — stopping (he chooses poses)";
                WLog("POSE " + m); return m;
            }
            var menuLabels = SeatPoseFilter(seat.ID, d.ButtonLabels);
            if (!couplesMode) menuLabels = PoseLabelsFree(seat, menuLabels, seatName);   // SeatOccupancy.cs
            if (PoseNoteSeatMenu(seat.ID.ToString(), d.ButtonLabels) && !adultSeat) { adultSeat = true; WLog($"POSE '{seatName}': a menu with an adult name was seen on this seat; its unclear submenu names count as adult"); }
            var (pick, sub, why) = PickPoseButton(menuLabels, current, wRnd, couplesMode, inCouples, preferPgSolo: recovery || preferPgSolo, inSubmenu: depth > 0, allowSexual: allowSexual, inSexualMenu: PosePathIsSexual(path, adultSeat), adultSeat: adultSeat, conversationOk: DavidSeatedNearNow());
            if (pick == null)
            {
                // 2026-10-16 David: never leave a couples pose running alone. No clearly solo option -> the pose the seat gave when I first sat alone.
                if (!couplesMode && PoseLeafIsCouples(current, wLastPosePath))
                {
                    var fb = await PoseSwitchToDefault(seat, seatName, d, current, allowSexual, why, ct);
                    if (fb != null) return fb;
                }
                if (!change && !recovery && !couplesMode && (adultSeat || SeatAdultName.IsMatch(seatName ?? "")) && !PoseSeatClearPgByName(false, current, path, adultSeat))
                {
                    var mS = $"'{seatName}': {why}; no clearly non-adult pose by name (showing [{current ?? "?"}]) {SkipSeatMarker}";
                    WLog("POSE " + mS); return mS;
                }
                var m = $"'{seatName}': {why}; keeping the current pose {current ?? "?"}";
                WLog("POSE " + m); return m;
            }
            if (PoseSexualRefused(path.Append(pick.Trim()), allowSexual, adultSeat))
            {   // backstop: never press a sexual pick when the rule says no (android / someone else around / not Peronaut)
                var mS = $"'{seatName}': refused sexual pick {string.Join(" > ", path.Append(pick.Trim()))} (android outfit / not alone with David); keeping {current ?? "?"}";
                WLog("POSE " + mS); return mS;
            }
            if (!sub && allowSexual && PosePathIsSexual(path.Append(pick.Trim()), adultSeat))
            {
                var full = path.Append(pick.Trim()).ToList();
                if (!SexualPosesAllowedNow()) { var m0 = $"'{seatName}': sexual pose {string.Join(" > ", full)} no longer allowed (someone else around / android); keeping {current ?? "?"}"; WLog("POSE " + m0); return m0; }
                WLog($"POSE '{seatName}': sexual pose {string.Join(" > ", full)}: stand, wear Naked, sit again first");
                var (nfAbort, r) = await NakedFirstThenPose(seat, seatName, full, ct);   // AdultRedress.cs: remembers the outfit to put back
                if (nfAbort != null) { WLog("POSE " + nfAbort); return nfAbort; }
                var m2 = $"'{seatName}' {seat.ID}: chose {string.Join(" > ", full)} (naked first; {why}): {r}";
                wLastPose = $"{DateTime.Now:HH:mm:ss} '{seatName}' {seat.ID}: {string.Join(" > ", full)} [solo, sexual allowed]";
                WLog("POSE " + m2); HandPoseForPathAsync(full, seatName); return m2;
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
                if (!couplesMode)
                {   // 2026-10-14: never on top of another sitter (SeatOccupancy.cs)
                    var ov = await PoseCheckOverlap(seat, pick, ct);
                    if (ov != null)
                    {
                        WLog($"POSE '{seatName}': overlap: {ov}; picking the next free pose ({overlapTry + 1}/{PoseOverlapRetries})");
                        if (overlapTry + 1 < PoseOverlapRetries)
                            return m + "; overlap -> " + await SeatPose(seat, seatName, DateTime.Now, true, ct, false, false, true, overlapTry + 1);
                        if (client.Self.SittingOn != 0) { WLog($"POSE '{seatName}': still overlapping after {PoseOverlapRetries} tries; standing up"); client.Self.Stand(); }
                        return m + $"; overlap ({ov}) - no free pose, stood up";
                    }
                }
                WLog("POSE " + m); HandPoseForPathAsync(path, $"{seatName}: {string.Join(" > ", path)}"); return m;
            }
            d = await WaitSeatDialog(seat, t1, 5000, ct);
            if (d == null) { var m = $"'{seatName}': submenu '{pick}' did not open"; WLog("POSE " + m); return m; }
        }
        WLog($"POSE '{seatName}': menu deeper than 3 levels ({string.Join(" > ", path)}); stopped");
        return "too deep";
    }

    // 2026-10-16 David (Mirage bed, CUDDLES* > in my eyes, he stood up; default pose names differ per seat, so it is the sit-alone pose):
    // BACK to the root menu, press the pose this seat gave when I first sat down alone (recorded per seat); else the first
    // non-sexual single pose on the menu (logged); else the last solo path. Returns the log message when it switched, null when nothing suitable was found.
    static async Task<string> PoseSwitchToDefault(Primitive seat, string seatName, ScriptDialogEventArgs d, string current, bool allowSexual, string why, CancellationToken ct)
    {
        try
        {
            var root = await SeatPoseBackToRoot(seat, d, ct);
            var labels = root?.ButtonLabels;
            if (labels == null || !PoseMenuIsTopLevel(labels))
            {
                WLog($"POSE '{seatName}': default-pose fallback: not at the top-level menu [{string.Join(" | ", labels ?? new List<string>())}]; keeping the current pose {current ?? "?"}");
                return null;
            }
            poseSitDefault.TryGetValue(seat.ID.ToString(), out var was);
            var (pick, how) = PickDefaultPoseButton(labels, was, allowSexual);
            if (pick != null)
            {
                var path = new List<string> { pick.Trim() };
                if (PoseSexualRefused(path, allowSexual, PoseSeatIsAdult(seat.ID.ToString(), seatName))) { WLog($"POSE '{seatName}': default pose {pick} refused (sexual rule)"); return null; }
                ClearKeptPoseCopies();
                client.Self.ReplyToScriptDialog(root.Channel, root.ButtonLabels.IndexOf(pick), pick, root.ObjectID);
                wLastPosePath = path; wLastSoloPosePath = path.ToList();
                NotePoseLeafMenu(path);
                wLastPose = $"{DateTime.Now:HH:mm:ss} '{seatName}' {seat.ID}: {pick.Trim()} [default, was couples {current}]";
                var m = $"'{seatName}' {seat.ID}: {why}; switched from couples pose [{current}] to the default pose {pick.Trim()} ({how})";
                WLog("POSE " + m); HandPoseForPathAsync(path, $"{seatName}: {pick.Trim()}");
                return m;
            }
            if (wLastSoloPosePath.Count > 0 && !PosePathIsCouples(wLastSoloPosePath))
            {
                var r = await SeatPosePath(seat, seatName, wLastSoloPosePath.ToList(), ct);
                if (r.Contains("restored") || r.Contains("already") || r.Contains("chose"))
                {
                    var m = $"'{seatName}': {why}; no usable default pose on the root menu [{string.Join(" | ", labels ?? new List<string>())}]; restored the last solo pose: {r}";
                    WLog("POSE " + m); return m;
                }
            }
            WLog($"POSE '{seatName}': no default pose found in [{string.Join(" | ", labels ?? new List<string>())}] (recorded sit-alone pose {was ?? "none"}); cannot leave the couples pose");
            return null;
        }
        catch (Exception ex) { WLog("POSE default-pose fallback error: " + ex.GetBaseException().Message); return null; }
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

    // Press [BACK] until the top-level pose menu (AVsitter reopens in the last submenu), whatever the number of levels (0..8).
    // Top level = no [BACK] button (or the Solo*+Couples* category root heuristic, whose [BACK] would go to seat-select).
    // "[<<]" / "[>>]" are page buttons and are never pressed. Logs when it gives up; the returned dialog is then the last one seen.
    static async Task<ScriptDialogEventArgs> SeatPoseBackToRoot(Primitive seat, ScriptDialogEventArgs d, CancellationToken ct)
    {
        if (d?.ButtonLabels == null) return d;
        var cur = d;
        // seat-select overlay: re-touch for the pose menu (and press F) before navigating
        if (PoseMenuLooksLikeSeatSelect(cur.ButtonLabels))
        {
            var tTouch = DateTime.Now; client.Self.Touch(seat.LocalID);
            var again = await WaitSeatDialog(seat, tTouch, 5000, ct);
            if (again == null) return d;
            cur = again;
            int fIdx = PoseMenuLooksLikeSeatSelect(cur.ButtonLabels) ? cur.ButtonLabels.FindIndex(b => PoseBare(b).Equals("F", StringComparison.OrdinalIgnoreCase)) : -1;
            if (fIdx >= 0)
            {
                var tF = DateTime.Now;
                client.Self.ReplyToScriptDialog(cur.Channel, fIdx, cur.ButtonLabels[fIdx], cur.ObjectID);
                again = await WaitSeatDialog(seat, tF, 5000, ct);
                if (again != null) cur = again;
            }
        }
        var res = await PoseBackToRootCore(cur.ButtonLabels.ToList(), async (idx, label) =>
        {
            var t1 = DateTime.Now;
            client.Self.ReplyToScriptDialog(cur.Channel, idx, label, cur.ObjectID);
            var next = await WaitSeatDialog(seat, t1, 5000, ct);
            if (next?.ButtonLabels == null) return null;
            cur = next;
            return next.ButtonLabels.ToList();
        });
        if (!res.atRoot) WLog($"POSE '{seat.Properties?.Name ?? seat.ID.ToString()}': could not reach the top-level menu: {res.note} [{string.Join(" | ", res.labels ?? new List<string>())}]");
        else if (res.presses > 0) WLog($"POSE '{seat.Properties?.Name ?? seat.ID.ToString()}': {res.note} [{string.Join(" | ", res.labels)}]");
        return cur;
    }

    // Press an exact AVsitter menu path. If the open menu already has the final leaf (e.g. Cross legs on Solo Couch*),
    // press it directly. Otherwise BACK to the real category root, then walk the path.
    static async Task<string> SeatPosePath(Primitive seat, string seatName, List<string> want, CancellationToken ct)
    {
        if (want == null || want.Count == 0) return "empty path";
        if (PoseSexualRefused(want, SexualPosesAllowedNow(), PoseSeatIsAdult(seat.ID.ToString(), seatName))) return $"'{seatName}': refused sexual path {string.Join(" > ", want)} (android outfit / not alone with David)";
        if (PosePathIsSexual(want, PoseSeatIsAdult(seat.ID.ToString(), seatName)) && !NakedWornNow()) return $"'{seatName}': refused sexual path {string.Join(" > ", want)} (not undressed: the Naked outfit is not on)";
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
                if (!PosePathIsCouples(path))
                {
                    var ov = await PoseCheckOverlap(seat, pick, ct);
                    if (ov != null)
                    {
                        WLog($"POSE '{seatName}': restored pose overlaps ({ov}); picking a free one instead");
                        return m + "; overlap -> " + await SeatPose(seat, seatName, DateTime.Now, true, ct, false, false, true, 1);
                    }
                }
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

    // pure: what to do when David stood up from our seat. dist0/dist1 = his distance from me at the guard's start/end (m),
    // seatedElsewhere = he sat on something else. -> "solo" (a solo pose here) | "stand_follow" (stand, go next to him)
    internal static (string action, string why) DavidLeftReaction(double dist0, double dist1, bool seatedElsewhere, double rnd)
    {
        if (seatedElsewhere) return dist1 <= 4 ? ("solo", $"he sat nearby ({dist1:F1} m)") : ("stand_follow", $"he sat {dist1:F1} m away");
        if (dist1 - dist0 > 0.4) return ("stand_follow", $"he is walking away ({dist0:F1} -> {dist1:F1} m)");
        if (dist1 > 3) return ("stand_follow", $"he is {dist1:F1} m away");
        return rnd < 0.3 ? ("stand_follow", $"he is close ({dist1:F1} m); joining him (random)") : ("solo", $"he is close ({dist1:F1} m), just adjusting");
    }

    static async Task ReactDavidLeftSeat(int gen)
    {
        try
        {
            double D() { var h = FindAvatarPos("David Nightingale"); return h == null ? 99 : Vector3.Distance(client.Self.SimPosition, h.Value.pos); }
            double d0 = D();
            await Task.Delay(1500);   // short guard: AVsitter re-seats on [SWAP]/role changes
            if (gen != System.Threading.Volatile.Read(ref poseDavidLeftSwitchGen) || poseDavidOnSeat || client?.Self?.SittingOn == 0) return;
            var dav = FindAvatarPos("David Nightingale");
            double d1 = D(); bool seated = dav != null && dav.Value.av.ParentID != 0;
            var (act, why) = DavidLeftReaction(d0, d1, seated, Random.Shared.NextDouble());
            Log("height", $"pose: David left our seat: {why} -> {act}");
            if (act == "solo") { await PoseSwitchToSoloAfterDavidLeft(gen, 0); return; }
            client.Self.Stand(); await Task.Delay(1200);
            if (dav != null && !WanderBlocksManualWalk)
                Log("height", "pose: David left: " + await NavCmds("goto_avatar", dav.Value.av.Name, dav.Value.av.Name.Split(' ')));
        }
        catch (Exception ex) { Log("height", "pose: David-left reaction error: " + ex.GetBaseException().Message); }
    }

    static async Task PoseSwitchToSoloAfterDavidLeft(int gen, int settleMs = 800)
    {
        try
        {
            await Task.Delay(settleMs); // let AVsitter finish its sit-leave shuffle
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
        // pose / pose change = solo only (preferPgSolo forced inside SeatPose); pose couples stays explicit
        if (couples)
        {   // David 2026-10-13: right roles before (and after) any couples pose
            var pre = await EnsureCoupleRoles(p, name, "pose couples (before)", false, cts.Token);
            var r = await SeatPose(p, name, DateTime.Now.AddSeconds(-20), true, cts.Token, explicitCouples: true, preferPgSolo: false);
            var post = await EnsureCoupleRoles(p, name, "pose couples (after)", false, cts.Token);
            return pre + "; " + r + "; " + post;
        }
        return await SeatPose(p, name, DateTime.Now.AddSeconds(-20), change || couples, cts.Token, explicitCouples: couples, preferPgSolo: !couples);
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
            // 2026-10-06 Mirage bed: Fist* > fist 2 looked like [solo] in the log but was a couples anim
            bool named = PoseIsCouplesNamed("Fist*") && PoseIsCouplesNamed("fist 2") && !PoseIsCouplesNamed("F waits")
                      && !PoseIsSoloMenu("Fist*") && PoseIsSoloMenu("Solo*");
            bool good = named;
            for (int i = 0; i < 40; i++)
            {
                var (p, _, why) = PickPoseButton(new() { "Fist*", "Solo*", "Couples PG*", "Adult*", "[ADJUST]", "F waits" },
                    "F waits", rnd, couplesMode: false, preferPgSolo: true);
                if (p != "Solo*") good = false;
            }
            if (good) pass++; else fail++;
            lines.Add($"{(good ? "PASS" : "FAIL")} Mirage Fist*: couples-named + preferPgSolo forces Solo* (never Fist*)");
        }
        {
            bool back = PoseIsBackButton("[BACK]") && PoseIsBackButton("BACK") && PoseIsBackButton("[ BACK ]")
                     && !PoseIsBackButton("<<") && !PoseIsBackButton("[>>]") && PoseBackIndex(new[] { "x", "<<" }) == 1 && PoseBackIndex(new[] { "[<<]", "[>>]", "[BACK]" }) == 2 && PoseBackIndex(new[] { "[<<]", "[>>]", "a" }) < 0 && !PoseIsBackButton("Solo*") && !PoseIsBackButton("[ADJUST]");
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
        {
            // 2026-10-07 19:39 KraftWork Beach Hammock ADULT: Adults* > 5Adults was picked as [solo]
            var hammock = new List<string> { "Adults*", "[ADJUST]", "[SWAP]", "SinglesSet*", "Mirror*", "Cuddles*" };
            bool good = true;
            for (int i = 0; i < 40; i++) good &= PickPoseButton(hammock, null, rnd, preferPgSolo: true).pick == "SinglesSet*";
            good &= PickPoseButton(new() { "Adults*", "[ADJUST]", "Mirror*", "Cuddles*", "Clean*" }, null, rnd, preferPgSolo: true).pick == null;
            good &= SoloSeatNameOk("KraftWork Summer Shack . Beach Hammock ADULT", false) && SoloSeatNameOk("KraftWork Summer Shack . Beach Hammock ADULT", true);
            if (good) pass++; else fail++;
            lines.Add($"{(good ? "PASS" : "FAIL")} Beach Hammock ADULT: SinglesSet* only; no solo option -> default pose; ADULT seat needs a singles menu");
        }
        var cur = PoseCurrent("AVsitter™2.1\n\n [Onlegs 4]"); bool c = cur == "Onlegs 4"; if (c) pass++; else fail++;
        lines.Add($"{(c ? "PASS" : "FAIL")} current pose parsed from the menu text: '{cur}'");
        bool sh = PoseIsCouplesNamed("COUPLES*") && PoseIsSoloMenu("SINGLE*") && !PoseIsCouplesNamed("Indeed") && !PoseIsSoloMenu("Snuggle")
               && PoseIsCouplesNamed("Fist*") && !PoseIsCouplesNamed("Cross legs");
        if (sh) pass++; else fail++;
        lines.Add($"{(sh ? "PASS" : "FAIL")} COUPLES*/SINGLE*/Fist* classifiers");
        return $"seat pose selftest: {pass} pass, {fail} fail (offline; no dialog pressed)\n" + string.Join("\n", lines);
    }
}
