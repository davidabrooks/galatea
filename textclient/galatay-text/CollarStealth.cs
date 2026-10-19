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

    static readonly SemaphoreSlim stealthLock = new(1, 1);
    static async Task<string> CollarStealthOn(string why)
    {
        if (!await stealthLock.WaitAsync(0)) return "collar stealth already running";
        try
        {
            var prev = lastDialog;
            var t = await Exec("touch-attachment \"Premium Collar\" 1");
            var main = await CollarDialogAfter(prev, 10000);
            if (main == null || !(main.ButtonLabels ?? new()).Contains("About")) return $"collar menu did not open ({t})";
            await Exec("dialog About");
            var about = await CollarDialogAfter(main, 10000);
            if (about == null) return "collar About menu did not open";
            var (label, on) = StealthButton(about.ButtonLabels);
            if (label == null) return "no Stealth button in the collar's About menu";
            if (on) { Log("collar", $"stealth ({why}): already on ('{label}'), nothing pressed"); return "collar already invisible (Stealth on)"; }
            await Exec("dialog " + label);
            var after = await CollarDialogAfter(about, 8000);
            var (_, nowOn) = StealthButton(after?.ButtonLabels);
            Log("collar", $"stealth ({why}): pressed '{label}' -> {(after == null ? "no menu back" : nowOn ? "on" : "still off")}");
            return after == null ? $"pressed '{label}' (no menu came back to confirm)" : nowOn ? "collar now invisible (Stealth on)" : "pressed Stealth but it still reads off";
        }
        finally { stealthLock.Release(); }
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
