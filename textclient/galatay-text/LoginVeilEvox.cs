// LoginVeilEvox.cs (2026-10-30, David after the 16:0x relog: "your ears were floating along with your nails and eyelashes"). Since #353 the
// EvoX head stays on through logout and login, and its ears and lashes are non-BOM faces the full-avatar alpha veil cannot hide.
// The EvoX 4.0 HUD hides them in the head itself (the setting is stored in the head, so it survives the logout):
//   'EarRL:rbl:txt**ar' face 1 = hide ears BOTH (toggle) -> head prim 'HeadHD' faces 1, 2 go alpha 0
//   'LashesTB:rbl:txt**ar' face 4 = hide all lashes (toggle) -> head prims 'ESL' / 'ESR' faces 4, 5 go alpha 0
// (checked in-world 16:1x: the HUD reacts with its Head/Lashes page closed; a second press shows them again).
// Logout: after the hair-first wave, before the collar stealth and the veil. Login: right after the head parts are on (veil-off), and a
// safety net later in any case, so she is never left earless. Only what WE hid is shown again (marker file): the android look hides
// her ears on purpose and keeps them hidden.
using LibreMetaverse;

namespace GalatayText;

internal static class EvoxHide
{
    internal const string EarsButton = "EarRL:rbl:txt**ar", LashesButton = "LashesTB:rbl:txt**ar";
    internal const int EarsBothFace = 1, LashesAllFace = 4;
    internal const double SafetyNetSec = 150;
    internal sealed record Parts(bool Ears, bool Lashes);

    /// <summary>Pure (tested): which parts are hidden now, from the head's per-face alphas (prim name, face, alpha). Null when the
    /// head's ear or lash prims are not there (another head, or properties not loaded): then nothing is pressed.</summary>
    internal static Parts Hidden(IEnumerable<(string prim, int face, float alpha)> faces)
    {
        var l = (faces ?? Enumerable.Empty<(string, int, float)>()).ToList();
        float? A(string p, int f) { var m = l.Where(x => x.prim == p && x.face == f).ToList(); return m.Count == 0 ? null : m[0].alpha; }
        var ears = new[] { A("HeadHD", 1), A("HeadHD", 2) };
        var lashes = new[] { A("ESL", 4), A("ESL", 5), A("ESR", 4), A("ESR", 5) };
        if (ears.Any(a => a == null) || lashes.Any(a => a == null)) return null;
        return new Parts(ears.All(a => a < 0.5f), lashes.All(a => a < 0.5f));
    }

    /// <summary>Pure (tested): the toggle presses that take <paramref name="now"/> to <paramref name="want"/> (buttons toggle, so a part
    /// already in the wanted state is never pressed).</summary>
    internal static List<(string button, int face, string what)> Presses(Parts now, Parts want)
    {
        var r = new List<(string, int, string)>();
        if (now == null || want == null) return r;
        if (now.Ears != want.Ears) r.Add((EarsButton, EarsBothFace, "ears"));
        if (now.Lashes != want.Lashes) r.Add((LashesButton, LashesAllFace, "lashes"));
        return r;
    }

    /// <summary>Pure (tested): the logout marker = the parts that were visible before and that we hid (never the android's hidden ears).</summary>
    internal static string Marker(Parts before, Parts after)
    {
        if (before == null || after == null) return "";
        var l = new List<string>();
        if (!before.Ears && after.Ears) l.Add("ears");
        if (!before.Lashes && after.Lashes) l.Add("lashes");
        return string.Join(",", l);
    }
    /// <summary>Pure (tested): the login target from the marker and the current state: show again only what the marker names.</summary>
    internal static Parts ShowTarget(string marker, Parts now)
    {
        if (now == null) return null;
        var m = (marker ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new Parts(m.Contains("ears") ? false : now.Ears, m.Contains("lashes") ? false : now.Lashes);
    }
    /// <summary>Pure (tested): safety net: restore once the veil is gone, or after SafetyNetSec whatever happens (never left earless).</summary>
    /// <summary>Pure (tested): the HUD's own failure line. 2026-10-30 16:44/16:48/17:00/17:20 logouts: the HUD, attached for the job, said
    /// "Hello Galatea, please wear your head to activate me." ~2 s after attaching (its head handshake failed) and ignored the presses, so
    /// ears/lashes were NOT confirmed. Only 17:03 (no such line) worked.</summary>
    internal static bool IsNoHeadLine(string msg) => (msg ?? "").IndexOf("please wear your head", StringComparison.OrdinalIgnoreCase) >= 0;
    /// <summary>Pure (tested): attach the HUD again and retry? Not confirmed (or the HUD said no head), tries left, time left.</summary>
    internal static bool RetryDue(bool confirmed, int attempt, int maxAttempts, double elapsedSec, double budgetSec) => !confirmed && attempt < maxAttempts && elapsedSec < budgetSec;
    internal const int MaxAttempts = 3; internal const double BudgetSec = 22;
    internal static bool SafetyRestoreDue(bool markerExists, bool veilWorn, double secsSinceLogin) => markerExists && (!veilWorn || secsSinceLogin >= SafetyNetSec);
}

public static partial class Program
{
    internal static readonly UUID EvoxHudItem = new("80597a5b-5254-386b-ad5e-6c260f65c8eb");   // '/ HUD / lel evox (f) 4.0' (Avalon 4.0 box)
    static string LoginVeilEvoxFile => Env("GT_VEIL_EVOX_FILE", "/home/box/viewers/textclient/run/login-veil-evox.txt");
    static readonly SemaphoreSlim evoxGate = new(1, 1);
    static InventoryItem evoxHudItemCache;   // 17:20 logout: logout -> HUD attached took 6 s, mostly the item fetch
    static async Task WaitNotWorn(UUID item, int ms) { for (int i = 0; i < ms / 100 && WornPrims().Any(p => AttachItemId(p) == item); i++) await Task.Delay(100); }

    static Primitive EvoxHeadRoot() => WornPrims().FirstOrDefault(p => LoginOrder.IsRealHead(p.Properties?.Name ?? "") && (p.Properties?.Name ?? "").Contains("lel evox", StringComparison.OrdinalIgnoreCase));
    static async Task<EvoxHide.Parts> EvoxReadHidden()
    {
        var head = EvoxHeadRoot(); if (head == null) return null;
        var prims = LinkPrims(head); await EnsureProperties(Sim, prims);
        var faces = new List<(string, int, float)>();
        foreach (var p in prims)
        {
            var te = p.Textures; if (te == null) continue; var n = p.Properties?.Name ?? "";
            for (int f = 0; f < 8; f++) { var fe = te.FaceTextures[f] ?? te.DefaultTexture; if (fe != null) faces.Add((n, f, fe.RGBA.A)); }
        }
        return EvoxHide.Hidden(faces);
    }

    /// <summary>Set ears/lashes through the EvoX HUD (attached for the job if it is not worn, detached after). Returns (before, after).</summary>
    static async Task<(EvoxHide.Parts before, EvoxHide.Parts after, string msg)> EvoxSet(Func<EvoxHide.Parts, EvoxHide.Parts> target, string why)
    {
        if (!LoggedIn) return (null, null, "not logged in");
        if (!await evoxGate.WaitAsync(20000)) return (null, null, "busy");
        bool attached = false; InventoryItem item = null;
        try
        {
            var before = await EvoxReadHidden(); if (before == null) return (null, null, "EvoX head (ear / lash prims) not worn");
            var want = target(before); var presses = EvoxHide.Presses(before, want);
            if (presses.Count == 0) return (before, before, "nothing to change");
            using var cts = new CancellationTokenSource(25000); var t0 = DateTime.Now;
            item = evoxHudItemCache ?? await FetchItemRO(EvoxHudItem, cts.Token); if (item != null) evoxHudItemCache = item;
            EvoxHide.Parts after = before; bool ok = false; int attempt = 0; var notes = new List<string>();
            while (true)
            {
                attempt++;
                var hud = WornPrims().FirstOrDefault(p => AttachItemId(p) == EvoxHudItem);
                DateTime noHeadAt = DateTime.MinValue;
                EventHandler<ChatEventArgs> onChat = (s2, e) =>
                {
                    try { if (e.Type == ChatType.OwnerSay && EvoxHide.IsNoHeadLine(e.Message) && (e.FromName ?? "").IndexOf("lel evox", StringComparison.OrdinalIgnoreCase) >= 0) noHeadAt = DateTime.Now; } catch { }
                };
                client.Self.ChatFromSimulator += onChat;
                try
                {
                    if (hud == null)
                    {
                        if (item == null) return (before, before, "EvoX HUD not in inventory");
                        client.Appearance.Attach(item, AttachmentPoint.Default, false); attached = true;
                        hud = await WaitWornItem(EvoxHudItem, 15000, cts.Token); if (hud == null) { notes.Add($"try {attempt}: HUD did not attach"); goto next; }
                        // its scripts start and handshake with the head; the failure line comes ~2 s after the attach
                        for (int i = 0; i < 10 && noHeadAt == DateTime.MinValue; i++) await Task.Delay(200, cts.Token);
                    }
                    if (noHeadAt != DateTime.MinValue) { notes.Add($"try {attempt}: HUD said no head"); goto next; }
                    var prims = LinkPrims(hud); await EnsureProperties(Sim, prims);
                    var now = await EvoxReadHidden() ?? after; var pr = EvoxHide.Presses(now, want);
                    foreach (var (button, face, what) in pr)
                    {
                        var b = prims.FirstOrDefault(p => p.Properties?.Name == button);
                        if (b == null) { Log("login-veil", $"evox: HUD button '{button}' not found ({what})"); continue; }
                        GrabAt(b, face, 0.5f, 0.5f); await Task.Delay(300, cts.Token);
                    }
                    for (int i = 0; i < 12; i++) { await Task.Delay(250, cts.Token); after = await EvoxReadHidden() ?? after; if (after == want) break; }
                    ok = after == want; if (!ok) notes.Add($"try {attempt}: {(noHeadAt != DateTime.MinValue ? "HUD said no head" : "no change seen")}");
                }
                finally { client.Self.ChatFromSimulator -= onChat; }
            next:
                if (ok || !EvoxHide.RetryDue(ok, attempt, EvoxHide.MaxAttempts, (DateTime.Now - t0).TotalSeconds, EvoxHide.BudgetSec)) break;
                // a fresh attach makes the HUD handshake with the head again
                if (item != null && WornPrims().Any(p => AttachItemId(p) == EvoxHudItem)) { try { client.Appearance.Detach(item); } catch { } attached = true; await WaitNotWorn(EvoxHudItem, 4000); }
                await Task.Delay(500, cts.Token);
            }
            var what2 = string.Join(", ", EvoxHide.Presses(before, want).Select(p => p.what));
            return (before, after, $"{what2}: {(ok ? "done" : "NOT confirmed")} in {(DateTime.Now - t0).TotalSeconds:F1} s, {attempt} attach(es){(notes.Count > 0 ? " (" + string.Join("; ", notes) + ")" : "")}");
        }
        catch (Exception ex) { return (null, null, "failed: " + ex.GetBaseException().Message); }
        finally
        {
            if (attached && item != null) { try { await Task.Delay(300); client.Appearance.Detach(item); } catch { } }
            evoxGate.Release();
        }
    }

    /// <summary>Logout, after the hair-first wave: hide ears and lashes; the marker names only what was visible and is now hidden.</summary>
    static async Task LoginVeilEvoxHideForLogout()
    {
        if (Env("GT_VEIL_EVOX", "1") == "0" || !LoggedIn || LoginVeilItem() == UUID.Zero) return;
        try
        {
            var (before, after, msg) = await EvoxSet(_ => new EvoxHide.Parts(true, true), "logout veil");
            var m = EvoxHide.Marker(before, after);
            if (m.Length > 0) File.WriteAllText(LoginVeilEvoxFile, m + "\n"); else { try { File.Delete(LoginVeilEvoxFile); } catch { } }
            Log("login-veil", $"evox for logout: {msg} (show at login: {(m.Length > 0 ? m : "nothing")})");
        }
        catch (Exception ex) { Log("login-veil", "evox hide for logout failed: " + ex.GetBaseException().Message); }
    }

    /// <summary>Login, veil off (or the safety net): show what the logout hid. The marker is removed only once it is confirmed.</summary>
    static async Task LoginVeilEvoxShowAfterLogin(string why)
    {
        try
        {
            var f = LoginVeilEvoxFile; if (!File.Exists(f) || !LoggedIn) return;
            var broken = LoginBrokenStop("evox"); if (broken != null) { Log("login-veil", broken + "; marker kept"); return; }
            var m = File.ReadAllText(f).Trim();
            if (m.Length == 0) { File.Delete(f); return; }
            for (int i = 0; i < 40 && EvoxHeadRoot() == null; i++) await Task.Delay(250);   // the head first
            var (_, after, msg) = await EvoxSet(now => EvoxHide.ShowTarget(m, now), why);
            bool ok = after != null && EvoxHide.Presses(after, EvoxHide.ShowTarget(m, after)).Count == 0;
            if (ok) { try { File.Delete(f); } catch { } }
            Log("login-veil", $"evox after login ({why}): {msg}{(ok ? "" : "; marker kept, the safety net retries")}");
        }
        catch (Exception ex) { Log("login-veil", "evox show after login failed: " + ex.GetBaseException().Message); }
    }

    /// <summary>Every login: whatever the veil path does (no veil, kept on, error), the ears and lashes come back.</summary>
    static void LoginVeilEvoxSafetyNet(DateTime t0)
    {
        if (!File.Exists(LoginVeilEvoxFile)) return;
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 3; i++)
            {
                while (LoggedIn && !EvoxHide.SafetyRestoreDue(File.Exists(LoginVeilEvoxFile), LoginVeilWorn(LoginVeilItem()), (DateTime.Now - t0).TotalSeconds)
                       && File.Exists(LoginVeilEvoxFile)) await Task.Delay(2000);
                if (!LoggedIn || !File.Exists(LoginVeilEvoxFile)) return;
                await Task.Delay(5000);   // the veil-off path normally gets there first
                if (!File.Exists(LoginVeilEvoxFile)) return;
                await LoginVeilEvoxShowAfterLogin("safety net");
                if (!File.Exists(LoginVeilEvoxFile)) return;
                await Task.Delay(30000);
            }
        });
    }
}
