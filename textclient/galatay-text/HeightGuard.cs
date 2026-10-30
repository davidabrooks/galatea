// HeightGuard: keeps Galatay seated at a normal height (added 2026-09-25).
// - diagnostics: "height" (alias "diag") shows sim-side size, hover, animations, seat offset
// - hover: pinned value from GT_HOVER or hover.txt (default 0.0 = the Firestorm default), POSTed to the
//   AgentPreferences cap (hover_height only) on login and region change, exactly as the SL viewer does
// - appearance: after login, verifies the sim sent us a baked appearance (visual params, sane size); else rebakes
// - sit guard: ~6 s after every sit, checks the seat's pose animation is playing and hover is pinned;
//   re-sits once if only default sit/stand animations are playing
using System.Globalization;
using System.Text;
using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.StructuredData;

namespace GalatayText;

public static partial class Program
{
    static readonly string HoverFile = Env("GT_HOVER_FILE", "/home/box/viewers/textclient/hover.txt");
    static readonly string HoverHistory = Env("GT_HOVER_HISTORY", "/workspace/secondlife/hover-history.log");
    static bool sitGuard = Env("GT_SIT_GUARD", "1") != "0";
    static int sitGen;
    static string lastHeightCheck = "-";

    static readonly Dictionary<UUID, string> BuiltinAnims = typeof(Animations)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(UUID))
        .GroupBy(f => (UUID)f.GetValue(null)!).ToDictionary(g => g.Key, g => g.First().Name);
    static readonly HashSet<UUID> SitAnims = new() { Animations.SIT, Animations.SIT_GENERIC, Animations.SIT_FEMALE, Animations.SIT_GROUND, Animations.SIT_GROUND_staticRAINED, Animations.SIT_TO_STAND };

    internal static string I(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // pure (tested): the pin. @adjustheight (RlvAdjustHeight.cs, the collar) wins until logout, then GT_HOVER, then hover.txt, else 0
    internal static double PinnedHoverFrom(double? rlvOverride, string env, string fileText)
    {
        if (rlvOverride is double rh) return rh;
        string s = !string.IsNullOrWhiteSpace(env) ? env : fileText;
        if (s != null && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return Math.Clamp(v, -2.0, 2.0);
        return 0.0;
    }
    // pure (tested): `hover set <v>` must change the REAL pin: 2026-10-26 it wrote hover.txt only while the collar's in-memory @adjustheight
    // override (0.02) kept winning in PinnedHover(), so `hover get` said 0.02 and every "back to normal hover" put 0.02 back
    internal static double? HoverSetOverride(double? currentOverride, double v) => currentOverride is null ? null : v;
    static double PinnedHover()
    {
        string file = null; try { if (File.Exists(HoverFile)) file = File.ReadLines(HoverFile).FirstOrDefault()?.Trim(); } catch { }
        return PinnedHoverFrom(rlvHoverOverride, Environment.GetEnvironmentVariable("GT_HOVER"), PinTextFor(FootwearKeyNow(), LoadFootwearHovers(), file));   // HoverFootwear.cs
    }

    static Avatar SelfAv()
    {
        var sim = client?.Network?.CurrentSim; if (sim == null) return null;
        return sim.ObjectsAvatars.Values.FirstOrDefault(a => a != null && a.ID == client.Self.AgentID);
    }

    static string AnimName(UUID id) => BuiltinAnims.TryGetValue(id, out var n) ? n : id.ToString();
    static List<UUID> SelfAnims() => client.Self.SignaledAnimations.Keys.ToList();
    static bool HasPoseAnim(List<UUID> anims) => anims.Any(a => !BuiltinAnims.ContainsKey(a));

    // hover_height as our OWN AvatarAppearance reports it, or null if not received. 2026-10-26: this is NOT the hover David's viewer shows:
    // after a POST (-0.186) it stayed 0 ("sim applies 0") while his viewer drew her sunk by the AgentPreferences value. Never use it to
    // judge what is in force (EffectiveHoverNow / EffectiveHoverFresh below); it is only the last-resort fallback and a diagnostic.
    static double? SimHover() { var av = SelfAv(); return av != null && av.VisualParameters.Length > 0 ? av.HoverHeight.Z : null; }

    // The hover IN FORCE = what the AgentPreferences cap holds = what David's viewer applies to her (observed 2026-10-26: prefs -0.186,
    // his viewer showed her sunk, our own appearance still said 0). Sources, best first: a fresh AgentPreferences GET, the value the
    // cap answered to our last POST, our own appearance (stale, last resort).
    static double? hoverInForce; static DateTime hoverInForceAt = DateTime.MinValue;
    internal static (double? v, string src) EffectiveHoverPick(double? prefsGet, double? lastPost, double? simAppearance)
        => prefsGet is double g ? (g, "AgentPreferences GET") : lastPost is double p ? (p, "last POST answered by the cap") : simAppearance is double s ? (s, "own appearance (may be stale)") : (null, "unknown");
    static double? EffectiveHoverNow() => EffectiveHoverPick(null, hoverInForce, SimHover()).v;
    static async Task<double?> PrefsHoverValue()
    {
        try { using var t = new CancellationTokenSource(10000); var m = await client.Self.GetAgentPreferencesAsync(t.Token); return m == null ? null : (double?)m.HoverHeight; }
        catch { return null; }
    }
    static async Task<(double? v, string src)> EffectiveHoverFresh()
    {
        var g = await PrefsHoverValue();
        if (g is double x) { if (hoverInForce is double old && Math.Abs(old - x) > 0.002) Log("height", $"hover in force {I(x)} (AgentPreferences GET) differs from the last POST answer {I(old)}: using the GET"); hoverInForce = x; hoverInForceAt = DateTime.Now; }
        return EffectiveHoverPick(g, hoverInForce, SimHover());
    }

    static async Task<(bool ok, string info)> PostHover(double v, string why)
    {
        var cap = client?.Network?.CurrentSim?.Caps?.CapabilityURI("AgentPreferences");
        if (cap == null) return (false, "AgentPreferences cap not available");
        using var t = new CancellationTokenSource(15000);
        try
        {
            var (resp, data) = await client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, new OSDMap { ["hover_height"] = OSD.FromReal(v) }, t.Token);
            string back = "?"; double? backV = null;
            try { if (data != null && OSDParser.Deserialize(data) is OSDMap m && m.ContainsKey("hover_height")) { backV = m["hover_height"].AsReal(); back = I(backV.Value); } } catch { }
            if (resp.IsSuccessStatusCode) { hoverInForce = backV ?? v; hoverInForceAt = DateTime.Now; }   // what David's viewer follows (see EffectiveHoverPick)
            var info = $"POST hover_height={I(v)} ({why}): HTTP {(int)resp.StatusCode}, server says {back}";
            Log("height", info);
            return (resp.IsSuccessStatusCode, info);
        }
        catch (Exception ex) { var info = $"POST hover_height failed ({why}): {ex.GetBaseException().GetType().Name}"; Log("height", info); return (false, info); }
    }

    static async Task<string> PrefsHover()
    {
        try
        {
            using var t = new CancellationTokenSource(10000);
            var m = await client.Self.GetAgentPreferencesAsync(t.Token);
            return m == null ? "n/a (GET not answered)" : I(m.HoverHeight);
        }
        catch (Exception ex) { return "n/a (" + ex.GetBaseException().GetType().Name + ")"; }
    }
    // pure (tested): the one-line state `hover get` prints. "in force" is what the viewers draw; "own appearance" is the stale self value
    internal static string HoverGetLine(double pinned, double? inForce, string src, double? ownAppearance)
        => $"hover in force {(inForce is double f ? I(f) : "unknown")} ({src}; this is what David's viewer applies, the renders use it too); pinned {I(pinned)}; own appearance says {(ownAppearance is double h ? I(h) : "?")} (stale, ignored)";

    static async Task<string> HeightDiag()
    {
        var sb = new StringBuilder();
        var sim = client.Network.CurrentSim; var av = SelfAv();
        sb.AppendLine($"pinned hover (GT_HOVER / {HoverFile}): {I(PinnedHover())}   sit guard: {(sitGuard ? "on" : "off")}");
        sb.AppendLine($"hover our own AvatarAppearance reports (AppearanceHover.Z; stale after a POST, not what viewers draw): {(SimHover() is double h ? I(h) : "no appearance received yet")}");
        sb.AppendLine($"hover in force from AgentPreferences GET (what David's viewer applies): {await PrefsHover()}");
        if (av == null) sb.AppendLine("own avatar object: not in the object list");
        else
            sb.AppendLine($"own avatar: size(scale)={I(av.Scale.X)} x {I(av.Scale.Y)} x {I(av.Scale.Z)} m, visual params={av.VisualParameters.Length}, appearance v{av.AppearanceVersion} COF v{av.COFVersion}, parent localid={av.ParentID}");
        int wearables = 0; try { wearables = client.Appearance.GetWearables().Count(); } catch { }
        sb.AppendLine($"wearables known to client: {wearables}");
        var on = client.Self.SittingOn;
        if (on != 0 && sim != null && sim.ObjectsPrimitives.TryGetValue(on, out var seat) && seat != null)
        {
            var sp = PositionHelper.GetPrimPosition(sim, seat);
            var rel = client.Self.RelativePosition;
            sb.AppendLine($"seat {seat.ID}: pos {Fmt(sp)} size {I(seat.Scale.X)} x {I(seat.Scale.Y)} x {I(seat.Scale.Z)}; my offset from seat root <{I(rel.X)}, {I(rel.Y)}, {I(rel.Z)}>; my pelvis z {I(client.Self.SimPosition.Z)} vs seat top z ~{I(sp.Z + seat.Scale.Z / 2)}");
        }
        else sb.AppendLine("not seated");
        var anims = SelfAnims();
        sb.AppendLine($"animations the sim says I play ({anims.Count}): " + string.Join(", ", anims.Select(AnimName)));
        sb.AppendLine($"pose animation from seat present: {(HasPoseAnim(anims) ? "yes" : "NO (only built-in anims)")}");
        sb.Append($"last automatic check: {lastHeightCheck}");
        return sb.ToString();
    }

    // login: pin hover like the viewer does, then verify our appearance arrived (sane size), else rebake
    static async Task AfterLoginHeight()
    {
        try
        {
            // wait for our own AvatarAppearance so the pre-existing hover value is recorded before anything is changed
            for (int i = 0; i < 30 && SelfAv()?.VisualParameters.Length is null or 0; i++) await Task.Delay(1000);
            var av = SelfAv(); var pin = PinnedHover();
            var old = av != null && av.VisualParameters.Length > 0 ? I(av.HoverHeight.Z) : "unknown (no appearance yet)";
            try { File.AppendAllText(HoverHistory, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} login: sim hover before pin {old}, pinned {I(pin)}\n"); } catch { }
            bool bad = av == null || av.VisualParameters.Length == 0 || av.Scale.Z < 1.0f;
            Log("height", $"login appearance check: {(av == null ? "own avatar not seen" : $"size z {I(av.Scale.Z)}, visual params {av.VisualParameters.Length}, sim hover {old}")}{(bad ? " -> requesting rebake" : " OK")}");
            // the SL viewer POSTs its hover on every login; do the same (hover_height only)
            await PostHover(pin, "login");
            if (bad) { try { await client.Appearance.RequestSetAppearance(true); } catch (Exception ex) { Log("height", "rebake failed: " + ex.GetBaseException().Message); } }
        }
        catch (Exception ex) { Log("height", "login check error: " + ex.GetBaseException().Message); }
    }

    // after each sit: pose animation playing? hover pinned? else correct (re-sit at most once per sit command)
    static async Task SitGuard(UUID seatId, int gen, bool mayResit)
    {
        try
        {
            await Task.Delay(6000);
            if (gen != sitGen || !LoggedIn) return;
            if (client.Self.SittingOn == 0) { lastHeightCheck = $"{DateTime.Now:HH:mm:ss} not seated after sit"; Log("height", "sit check: not seated"); return; }
            var anims = SelfAnims(); var pose = HasPoseAnim(anims);
            var hv = EffectiveHoverNow(); var pin = PinnedHover();
            var av = SelfAv();
            var msg = $"sit check {seatId}: pose anim {(pose ? "yes" : "NO")} [{string.Join(", ", anims.Select(AnimName))}], hover in force {(hv is double h ? I(h) : "?")} (pinned {I(pin)}), size z {(av != null ? I(av.Scale.Z) : "?")}, offset z {I(client.Self.RelativePosition.Z)}";
            if (hv is double h2 && Math.Abs(h2 - pin) > 0.02) { msg += " -> re-pin hover"; await PostHover(pin, "sit check"); }
            if (av != null && (av.VisualParameters.Length == 0 || av.Scale.Z < 1.0f)) { msg += " -> rebake"; _ = client.Appearance.RequestSetAppearance(true); }
            if (!pose && sitGuard && mayResit)
            {
                msg += " -> re-sit once";
                Log("height", msg); lastHeightCheck = $"{DateTime.Now:HH:mm:ss} {msg}";
                client.Self.Stand(); await Task.Delay(1500);
                if (gen != sitGen) return;
                client.Self.RequestSit(seatId, Vector3.Zero); await Task.Delay(1200); client.Self.Sit();
                _ = SitGuard(seatId, gen, false);
                return;
            }
            Log("height", msg + (pose ? " OK" : ""));
            lastHeightCheck = $"{DateTime.Now:HH:mm:ss} {msg}";
        }
        catch (Exception ex) { Log("height", "sit check error: " + ex.GetBaseException().Message); }
    }
    static void ScheduleSitGuard(UUID seatId) { if (animLogUntil < DateTime.Now.AddMinutes(10)) animLogUntil = DateTime.Now.AddMinutes(10); var g = Interlocked.Increment(ref sitGen); _ = Task.Run(() => SitGuard(seatId, g, true)); }

    internal const double HoverApplyMax = 0.5;
    static async Task<string> HoverCmd(string[] a)
    {
        if (a.Length == 0 || a[0] == "get")
        { var (ef, src) = await EffectiveHoverFresh(); return HoverGetLine(PinnedHover(), ef, src, SimHover()); }
        if (a[0] == "set" && a.Length == 2 && double.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            v = Math.Clamp(v, -2.0, 2.0);
            var old = $"pinned {I(PinnedHover())}, in force {(EffectiveHoverNow() is double h ? I(h) : "?")}";
            try { File.AppendAllText(HoverHistory, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} old: {old} -> new {I(v)}\n"); } catch { }
            var (ok, info) = await PostHover(v, "hover set");
            if (ok) { try { File.WriteAllText(HoverFile, I(v) + "\n"); } catch { } SaveVerifiedStandHover(v, "hover set"); rlvHoverOverride = HoverSetOverride(rlvHoverOverride, v); }
            return $"old: {old}; {info}; {(ok ? $"pinned value saved to {HoverFile}" : "NOT saved")}";
        }
        // `hover apply <m>`: POST only. Nothing is written to hover.txt (unlike `set`, which pins the value for every later login and region change).
        // 2026-10-26: this is the fix David asked for: a value is applied right then, never remembered.
        if (a[0] == "apply" && a.Length == 2 && double.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var av2))
        {
            if (Math.Abs(av2) > HoverApplyMax) return $"hover apply: {I(av2)} m is beyond +-{HoverApplyMax} m";
            var old = $"in force {(EffectiveHoverNow() is double h ? I(h) : "?")}";
            try { File.AppendAllText(HoverHistory, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} apply (not saved): {old} -> {I(av2)}\n"); } catch { }
            var (ok2, info2) = await PostHover(av2, "hover apply (not saved)");
            var (eff, src) = await EffectiveHoverFresh();
            return $"{old}; {info2}; hover in force now {(eff is double e ? I(e) : "unknown")} ({src}); not saved to hover.txt";
        }
        if (a[0] == "calibrate") return await hoverSvc.HoverCalibrate(a[1..]);
        if (a[0] == "repin") return (await PostHover(PinnedHover(), "hover repin")).info;
        if (a[0] == "check")
        {
            // waits for the check and returns its one line: the render count and the path of the contact close-up (feet standing, hips sitting) to show David
            var t = hoverSvc.HoverCheckRequest("manual", 500);
            return await Task.WhenAny(t, Task.Delay(120000)) == t ? await t : "visual hover check requested but still running after 120 s (see the posehover log for its close-up path)";
        }
        return "usage: hover [get] | hover set <meters -2..2> (pins it in hover.txt) | hover apply <meters> (not saved) | hover repin | hover check | hover calibrate stand|sit|here <cm> | show | reset stand|sit|here";
    }

    static string AnimCmd(string[] a)
    {
        if (a.Length == 0 || a[0] == "list") { Dictionary<UUID, (int seq, UUID src)> cur; lock (animLock) cur = ownAnims; return string.Join("\n", cur.Select(kv => $"{kv.Key} {AnimName(kv.Key)} seq {kv.Value.seq} from {SrcDesc(kv.Value.src)}")); }
        if (a.Length == 2 && (a[0] == "stop" || a[0] == "start") && UUID.TryParse(a[1], out var id))
        { if (a[0] == "stop") client.Self.AnimationStop(id, true); else client.Self.AnimationStart(id, true); return $"{a[0]} sent for {AnimName(id)}"; }
        return "usage: anim [list] | anim stop <uuid> | anim start <uuid>";
    }
}
