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
        for (int i = 0; i < ms / 250; i++)
        {
            var d = lastDialog;
            if (d != null && !ReferenceEquals(d, prev) && d.ObjectName == "Collar" && d.OwnerID == client.Self.AgentID) return d;
            await Task.Delay(250);
        }
        return null;
    }

    // pure (tested): should the Stealth button be pressed to reach `want`? (null = no button / unknown -> never press)
    internal static bool StealthNeedsPress(string label, bool on, bool want) => label != null && on != want;
    static readonly SemaphoreSlim stealthLock = new(1, 1);
    static Task<string> CollarStealthOn(string why) => CollarStealthSet(true, why).ContinueWith(t => t.Result.result);
    // collar main menu -> About -> press "Stealth" only if its state differs from `want`. wasOn = state before (null = unknown)
    static async Task<(string result, bool? wasOn)> CollarStealthSet(bool want, string why)
    {
        if (!await stealthLock.WaitAsync(0)) return ("collar stealth already running", null);
        try
        {
            var prev = lastDialog;
            var t = await Exec("touch-attachment \"Premium Collar\" 1");
            var main = await CollarDialogAfter(prev, 10000);
            if (main == null || !(main.ButtonLabels ?? new()).Contains("About")) return ($"collar menu did not open ({t})", null);
            await Exec("dialog About");
            var about = await CollarDialogAfter(main, 10000);
            if (about == null) return ("collar About menu did not open", null);
            var (label, on) = StealthButton(about.ButtonLabels);
            if (label == null) return ("no Stealth button in the collar's About menu", null);
            string W(bool x) => x ? "invisible (Stealth on)" : "visible (Stealth off)";
            if (!StealthNeedsPress(label, on, want)) { Log("collar", $"stealth {(want ? "on" : "off")} ({why}): already ('{label}'), nothing pressed"); return ($"collar already {W(on)}", on); }
            await Exec("dialog " + label);
            var after = await CollarDialogAfter(about, 8000);
            var (_, nowOn) = StealthButton(after?.ButtonLabels);
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
        if (!await stealthLock.WaitAsync(0)) return "collar menu busy";
        try
        {
            var prev = lastDialog;
            var t = await Exec("touch-attachment \"Premium Collar\" 1");
            var main = await CollarDialogAfter(prev, 10000);
            if (main == null) return $"collar menu did not open ({t})";
            Log("collar", $"magic leash ({why}): main menu [{string.Join("|", main.ButtonLabels ?? new())}]");
            var cur = main; var ml = MenuPick(cur.ButtonLabels, "Magic");
            foreach (var parent in MagicLeashParents)
            {
                if (ml != null) break;
                var pb = MenuPick(main.ButtonLabels, parent); if (pb == null) continue;
                await Exec("dialog " + pb); var sub = await CollarDialogAfter(cur, 8000); if (sub == null) continue;
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
