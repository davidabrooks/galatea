// CollarStealth.cs (2026-10-19, David): changing into an android outfit (Android, Android Base, any outfit named Android*)
// makes the N° 9 collar invisible. The N° 9 Premium HUD only opens the wearer's own collar menu (its README), so this opens
// the same menu by touching the worn collar: main menu -> About -> "☐ Stealth" pressed only when not already "☑ Stealth".
// Nothing else in the collar is changed; the NX Remote is never involved. 'collar stealth' runs it by hand.
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal static bool IsAndroidOutfit(string name) => (name ?? "").Trim().StartsWith("Android", StringComparison.OrdinalIgnoreCase);

    // pure (tested): the Stealth button in the collar's About menu: (label, on). label null = no Stealth button
    internal static (string label, bool on) StealthButton(IEnumerable<string> buttons)
    {
        var b = (buttons ?? Array.Empty<string>()).FirstOrDefault(x => x != null && x.Contains("Stealth", StringComparison.OrdinalIgnoreCase));
        return (b, b != null && (b.Contains('☑') || b.Contains('☒') || b.Contains('■')));
    }

    static async Task<ScriptDialogEventArgs> CollarDialogAfter(ScriptDialogEventArgs prev, int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < ms / 250; i++)
        {
            var d = lastDialog;
            if (d != null && !ReferenceEquals(d, prev) && d.ObjectName == "Collar" && d.OwnerID == client.Self.AgentID) { CollarLatencyNote((int)sw.ElapsedMilliseconds); return d; }
            await Task.Delay(250);
        }
        return null;
    }

    // 2026-11-02 16:24:53 logout: the About page came ~1 s after 'dialog About' but the logout waited only 1000 ms, gave up, and went to the
    // veil with Stealth unconfirmed ("was unknown"). Waits now come from the collar's observed answer times (this session's, plus the
    // log's history: About answered in 0-2 s 88 of 91 times, worst 7 s), and the last state the collar SHOWED us is kept.
    static readonly object collarLatLock = new();
    static readonly Queue<int> collarLatencies = new();
    static void CollarLatencyNote(int ms) { lock (collarLatLock) { collarLatencies.Enqueue(ms); while (collarLatencies.Count > 20) collarLatencies.Dequeue(); } }
    static int[] CollarLatencySnapshot() { lock (collarLatLock) return collarLatencies.ToArray(); }
    internal const int CollarWaitDefaultMs = 2500, CollarWaitMinMs = 1500, CollarWaitMaxMs = 4000;
    /// <summary>Pure (tested): one collar menu wait from observed answer times: 1.5x the slowest recent answer, within 1.5-4 s
    /// (default 2.5 s until 3 answers are seen: the log's About answers were 0-2 s at 1 s resolution). A slower page is caught by the retry.</summary>
    internal static int CollarWaitMs(IEnumerable<int> observedMs)
    {
        var l = (observedMs ?? Array.Empty<int>()).Where(x => x >= 0).ToList();
        if (l.Count < 3) return CollarWaitDefaultMs;
        return Math.Clamp((int)Math.Round(l.Max() * 1.5), CollarWaitMinMs, CollarWaitMaxMs);
    }
    /// <summary>The collar's Stealth state as last READ from its About page (or confirmed after a press), by any flow: the collar_stealth
    /// command, the leash show/hide, outfits, login. Null = never seen this session.</summary>
    internal static volatile CollarSeen collarSeen;
    internal sealed record CollarSeen(bool On, DateTime At, string Why);
    static void CollarSeenSet(bool on, string why) => collarSeen = new CollarSeen(on, DateTime.Now, why);
    internal static void CollarSeenReset() => collarSeen = null;
    /// <summary>Pure (tested): the "was" state for the login marker: the page read now, else the last state the collar showed us.</summary>
    internal static bool? CollarWasOn(bool? readNow, CollarSeen seen) => readNow ?? seen?.On;

    // pure (tested): should the Stealth button be pressed to reach `want`? (null = no button / unknown -> never press)
    internal static bool StealthNeedsPress(string label, bool on, bool want) => label != null && on != want;
    // 2026-10-26 08:58: ONE collar menu session at a time. The leash handler (Stealth: About; Magic Leash: Extras) and the idle
    // collar-pose switch (Pose) all drive the same dialogs through the shared `lastDialog`, so they crossed: Magic Leash pressed
    // "Extras" on the Pose menu (read back [Crawl|cutie|...|STOP]) and gave up; the pose switch got the About page and could not find 'nadu'.
    static readonly SemaphoreSlim stealthLock = new(1, 1);
    internal const int CollarMenuWaitMs = 90000;
    static async Task<bool> CollarMenuEnter() => await stealthLock.WaitAsync(CollarMenuWaitMs);
    // pure (tested): which collar menu is this? (the Pose list has STOP; the main menu has About + Extras)
    internal static bool CollarLooksLikePoseMenu(IEnumerable<string> b) => (b ?? Array.Empty<string>()).Any(x => x != null && x.Equals("STOP", StringComparison.OrdinalIgnoreCase));
    internal static bool CollarLooksLikeMainMenu(IEnumerable<string> b) { var l = (b ?? Array.Empty<string>()).ToList(); return l.Contains("About") && l.Contains("Extras"); }
    // wait for a NEW collar dialog the predicate accepts (others, e.g. a stray page from another flow, are skipped)
    static async Task<ScriptDialogEventArgs> CollarDialogWhere(ScriptDialogEventArgs prev, Func<ScriptDialogEventArgs, bool> ok, int ms)
    {
        var seen = prev;
        for (int i = 0; i < ms / 250; i++)
        {
            var d = lastDialog;
            if (d != null && !ReferenceEquals(d, seen) && d.ObjectName == "Collar" && d.OwnerID == client.Self.AgentID)
            { if (ok(d)) return d; seen = d; }
            await Task.Delay(250);
        }
        return null;
    }
    internal const int CollarTouchTries = 4;
    /// <summary>Pure (tested): touch the collar again? Only when the touch missed it by name (properties not loaded), within the tries.</summary>
    internal static bool CollarTouchRetry(string touchResult, int attempt) => attempt < CollarTouchTries && (touchResult ?? "").StartsWith("no single worn attachment matches");
    /// <summary>A collar About page opened ahead of time (login, under the veil) holding the menu lock; CollarStealthSet presses on it.</summary>
    internal sealed record CollarPrepared(ScriptDialogEventArgs About, string Label, bool On, bool Want, DateTime At);
    static CollarPrepared collarPrepared;
    internal const double CollarPrepMaxAgeSec = 45;
    /// <summary>Pure (tested): use the pre-opened About page? Same wanted state, fresh enough for the collar's own menu timeout.</summary>
    internal static bool CollarPrepUsable(CollarPrepared p, bool want, DateTime now) => p != null && p.Want == want && p.Label != null && (now - p.At).TotalSeconds < CollarPrepMaxAgeSec;
    static async Task<string> CollarStealthPrepare(bool want, string why)
    {
        if (LoginBrokenStop("collar") is string broken) return broken;
        if (!await CollarMenuEnter()) return "collar menu busy";
        bool keep = false;
        try
        {
            string t = ""; ScriptDialogEventArgs main = null;
            for (int attempt = 1; ; attempt++)
            {
                var prev = lastDialog; t = await Exec("touch-attachment \"Premium Collar\" 1");
                if (CollarTouchRetry(t, attempt)) { await Task.Delay(1500); continue; }
                main = await CollarDialogAfter(prev, 8000); break;
            }
            if (main == null || !(main.ButtonLabels ?? new()).Contains("About")) return $"menu did not open ({t}); the reveal opens it";
            await Exec("dialog About");
            var about = await CollarDialogAfter(main, 8000);
            var (label, on) = StealthButton(about?.ButtonLabels);
            if (label == null) return "About page not ready; the reveal opens it";
            if (!StealthNeedsPress(label, on, want)) return $"already '{label}'";
            collarPrepared = new CollarPrepared(about, label, on, want, DateTime.Now); keep = true;
            var mine = collarPrepared;
            _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(CollarPrepMaxAgeSec)); if (Interlocked.CompareExchange(ref collarPrepared, null, mine) == mine) stealthLock.Release(); });
            return $"About page open, '{label}' ready to press at the reveal";
        }
        finally { if (!keep) stealthLock.Release(); }
    }
    static Task<string> CollarStealthOn(string why) => CollarStealthSet(true, why).ContinueWith(t => t.Result.result);
    // collar main menu -> About -> press "Stealth" only if its state differs from `want`. wasOn = state before (null = unknown)
    static async Task<(string result, bool? wasOn)> CollarStealthSet(bool want, string why, int menuWaitMs = 10000, int menuTouches = 1, int aboutTries = 1)
    {
        var broken = LoginBrokenStop("collar");
        if (broken != null) { Log("collar", broken); return (broken, null); }
        var prep = collarPrepared;
        if (prep != null && Interlocked.CompareExchange(ref collarPrepared, null, prep) == prep)
        {   // we now own the lock the prep held
            try
            {
                if (CollarPrepUsable(prep, want, DateTime.Now))
                {
                    var idx = prep.About.ButtonLabels.FindIndex(b => b == prep.Label);
                    client.Self.ReplyToScriptDialog(prep.About.Channel, idx, prep.Label, prep.About.ObjectID);
                    var back = await CollarDialogAfter(prep.About, 3000);
                    var (_, nowOn2) = StealthButton(back?.ButtonLabels);
                    if (back != null && nowOn2 == want)
                    {
                        CollarSeenSet(nowOn2, why);
                        Log("collar", $"stealth {(want ? "on" : "off")} ({why}): pressed '{prep.Label}' on the page opened under the veil -> {(nowOn2 ? "on" : "off")}");
                        string W2(bool x) => x ? "invisible (Stealth on)" : "visible (Stealth off)";
                        return ($"collar now {W2(want)}", prep.On);
                    }
                    Log("collar", $"stealth ({why}): the pre-opened page did not answer; full menu path");
                }
            }
            finally { stealthLock.Release(); }
        }
        if (!await CollarMenuEnter()) return ("collar menu busy for 90 s", null);
        try
        {
            // 17:34:04 login: the touch found no collar by name (worn prim names not loaded yet: "worn: ?, ..."), so the collar stayed
            // invisible. A name miss is retried after the properties load.
            string t = ""; ScriptDialogEventArgs main = null;
            for (int attempt = 1; ; attempt++)
            {
                var prev = lastDialog;
                t = await Exec("touch-attachment \"Premium Collar\" 1");
                if (CollarTouchRetry(t, attempt)) { await Task.Delay(2000); continue; }
                // every retry used and still no name resolved: the session has no inventory (13:57 seed 404), say so plainly
                if (WornNamesUnresolved(t)) { var m = $"collar stopped: no worn attachment name has resolved after {attempt} tries ({t}); the session has no inventory, a fresh login is needed"; Log("collar", m); return (m, null); }
                main = await CollarDialogAfter(prev, menuWaitMs);
                // 2026-11-02 (David): at logout the menu gets one quick second touch instead of a 10 s wait (23:17:29 Oct 31: no menu, 11 s lost)
                if ((main == null || !(main.ButtonLabels ?? new()).Contains("About")) && attempt < menuTouches) { Log("collar", $"stealth ({why}): no menu after {menuWaitMs} ms, touching again"); continue; }
                break;
            }
            if (main == null || !(main.ButtonLabels ?? new()).Contains("About")) return ($"collar menu did not open ({t})", null);
            // the About page: wait, then wait once more for a late page (16:24:54 it came just after the wait), then press About again
            ScriptDialogEventArgs about = null;
            for (int a = 1; a <= aboutTries && about == null; a++)
            {
                if (a == 1 || a % 2 == 1) await Exec("dialog About");
                about = await CollarDialogWhere(main, d => StealthButton(d.ButtonLabels).label != null, menuWaitMs);
                if (about == null && a < aboutTries) Log("collar", $"stealth ({why}): no About page after {menuWaitMs} ms (try {a}/{aboutTries}), {(a % 2 == 1 ? "waiting again" : "pressing About again")}");
            }
            if (about == null) return ($"collar About menu did not open ({aboutTries} tries of {menuWaitMs} ms)", null);
            var (label, on) = StealthButton(about.ButtonLabels);
            if (label == null) return ("no Stealth button in the collar's About menu", null);
            CollarSeenSet(on, why + " (read)");
            string W(bool x) => x ? "invisible (Stealth on)" : "visible (Stealth off)";
            if (!StealthNeedsPress(label, on, want)) { Log("collar", $"stealth {(want ? "on" : "off")} ({why}): already ('{label}'), nothing pressed"); return ($"collar already {W(on)}", on); }
            await Exec("dialog " + label);
            var after = await CollarDialogAfter(about, Math.Max(menuWaitMs, 8000));
            var (_, nowOn) = StealthButton(after?.ButtonLabels);
            if (after != null && StealthButton(after.ButtonLabels).label != null) CollarSeenSet(nowOn, why + " (pressed)");
            Log("collar", $"stealth {(want ? "on" : "off")} ({why}): pressed '{label}' -> {(after == null ? "no menu back" : nowOn ? "on" : "off")}");
            return (after == null ? $"pressed '{label}' (no menu came back to confirm)" : nowOn == want ? $"collar now {W(want)}" : $"pressed Stealth but it still reads {(nowOn ? "on" : "off")}", on);
        }
        finally { stealthLock.Release(); }
    }

    // 2026-10-23 (David): what the leash does to the collar, by form.
    //   human: Stealth off so the collar shows (and it stays visible after the unleash).
    //   android (any Android* outfit): the collar stays invisible (Stealth on), and the chain is made invisible with the
    //   collar's Magic Leash; on the unleash the Magic Leash is RESET to the normal (visible) chain.
    internal readonly record struct LeashCollarPlan(bool stealthWant, bool chainInvisible);
    internal static LeashCollarPlan LeashCollarOnLeash(bool android) => android ? new(true, true) : new(false, false);
    internal static bool LeashChainResetOnUnleash(bool android, string mode, string why) =>
        android && mode == "leash" && !(why ?? "").StartsWith("replaced", StringComparison.OrdinalIgnoreCase);
    static bool AndroidFormNow() => IsAndroidOutfit(currentOutfit) || AndroidWorn();   // the outfit name, else the android body actually worn
    // pure (tested): first button whose label contains any of the words (case-insensitive), null = none
    internal static string MenuPick(IEnumerable<string> buttons, params string[] words) =>
        (buttons ?? Array.Empty<string>()).FirstOrDefault(b => b != null && words.Any(w => b.Contains(w, StringComparison.OrdinalIgnoreCase)));
    // Magic Leash menu (README "Magic Leash": shapes, silk, invisible; RESET restores the original chain). Its place in the
    // menu tree is undocumented: try the main menu, then the usual submenus. Every menu seen is logged for the live check.
    internal static readonly string[] MagicLeashParents = { "Apps", "Features", "Extras", "Leash", "Settings", "Options" };
    static async Task<string> MagicLeashSet(bool invisible, string why)
    {
        if (!await CollarMenuEnter()) return "collar menu busy for 90 s";
        try
        {
            ScriptDialogEventArgs main = null; string t = "";
            for (int tryN = 1; tryN <= 2 && main == null; tryN++)
            {
                var prev = lastDialog;
                t = await Exec("touch-attachment \"Premium Collar\" 1");
                main = await CollarDialogWhere(prev, d => CollarLooksLikeMainMenu(d.ButtonLabels), 10000);
            }
            if (main == null) return $"collar main menu did not open ({t})";
            Log("collar", $"magic leash ({why}): main menu [{string.Join("|", main.ButtonLabels ?? new())}]");
            var cur = main; var ml = MenuPick(cur.ButtonLabels, "Magic");
            foreach (var parent in MagicLeashParents)
            {
                if (ml != null) break;
                var pb = MenuPick(main.ButtonLabels, parent); if (pb == null) continue;
                await Exec("dialog " + pb); var sub = await CollarDialogWhere(cur, d => !CollarLooksLikePoseMenu(d.ButtonLabels) && !CollarLooksLikeMainMenu(d.ButtonLabels) || MenuPick(d.ButtonLabels, "Magic") != null, 8000); if (sub == null) continue;
                Log("collar", $"magic leash ({why}): '{pb}' menu [{string.Join("|", sub.ButtonLabels ?? new())}]");
                cur = sub; ml = MenuPick(sub.ButtonLabels, "Magic");
                if (ml == null) { var back = MenuPick(sub.ButtonLabels, "BACK", "◄", "<<"); if (back == null) break; await Exec("dialog " + back); var m2 = await CollarDialogAfter(cur, 8000); if (m2 == null) break; cur = m2; }
            }
            if (ml == null) return "no Magic Leash button found in the collar menu";
            await Exec("dialog " + ml); var mm = await CollarDialogAfter(cur, 8000);
            if (mm == null) return $"'{ml}' menu did not open";
            Log("collar", $"magic leash ({why}): '{ml}' menu [{string.Join("|", mm.ButtonLabels ?? new())}]");
            var pick = invisible ? MenuPick(mm.ButtonLabels, "Invisible", "Invis", "None") : MenuPick(mm.ButtonLabels, "RESET", "Default", "Chain");
            if (pick == null) return $"no {(invisible ? "Invisible" : "RESET")} button in '{ml}'";
            await Exec("dialog " + pick);
            Log("collar", $"magic leash ({why}): pressed '{pick}'");
            return $"Magic Leash: pressed '{pick}' ({(invisible ? "chain invisible" : "chain visible again")})";
        }
        finally { stealthLock.Release(); }
    }
    static void LeashCollarOnLeashRun()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500);
                bool android = AndroidFormNow(); var plan = LeashCollarOnLeash(android);
                var (r, _) = await CollarStealthSet(plan.stealthWant, android ? "leashed (android: stays hidden)" : "leashed (human: show it)");
                RlvLog($"leash: {(android ? "android" : "human")} form: collar {r}");
                if (plan.chainInvisible) RlvLog("leash: android form: " + await MagicLeashSet(true, "leashed while android"));
            }
            catch (Exception ex) { Log("collar", "leash collar check failed: " + ex.GetBaseException().Message); }
        });
    }
    static void LeashCollarOnUnleash(string mode, string why)
    {
        if (!LeashChainResetOnUnleash(AndroidFormNow(), mode, why)) return;   // human: the collar simply stays visible
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(1500); RlvLog("leash: unleashed in android form: " + await MagicLeashSet(false, "unleashed")); }
            catch (Exception ex) { Log("collar", "magic leash reset failed: " + ex.GetBaseException().Message); }
        });
    }

    static void CollarStealthAfterOutfit(string outfit)
    {
        if (!IsAndroidOutfit(outfit)) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(6000); Log("collar", $"android outfit '{outfit}': " + await CollarStealthOn("outfit " + outfit)); }
            catch (Exception ex) { Log("collar", "stealth after outfit failed: " + ex.GetBaseException().Message); }
        });
    }
}
