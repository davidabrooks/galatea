using System.Text.Json;
using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-10-28 (David): the feet fit is judged on the FOOT, not the boot, from evidence (heel-foot animations + a feet close-up), never from feet-state.txt or the press log.
public class FeetFitTests
{
    static readonly UUID Boots = UUID.Parse("810c862e-8d2d-3d28-82a5-19e9fd216ce9");
    static readonly UUID Hud = UUID.Parse("961d2d4b-4948-3bac-ab88-0a7ba3b99302");
    static string Img(bool ok = true, double luma = 0.4, double p95 = 0.9, double skin = 0.0, string why = "ok") =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["ok"] = ok, ["mean_luma"] = luma, ["p95_luma"] = p95, ["skin_foot_frac"] = skin, ["why"] = why, ["image"] = "/x/feet.jpg" });
    static FeetAuto.FeetImage Parsed(string j) => FeetAuto.ParseFeetImage(j);

    // ---- the pure verdict ----
    [Fact] public void HeeledShoe_NoHeelAnimations_IsAFlatFootBesideHeeledBoots_EvenWithACleanPicture()
    {
        var v = FeetAuto.FeetFit(true, false, Parsed(Img()));
        Assert.Equal(FeetAuto.FeetKind.Failed, v.Kind); Assert.Contains("flat beside heeled boots", v.Why);
    }
    [Fact] public void FlatWanted_HeelAnimationsPlaying_Fails()
        => Assert.Equal(FeetAuto.FeetKind.Failed, FeetAuto.FeetFit(false, true, Parsed(Img())).Kind);
    [Fact] public void SkinInTheFootZone_IsABareFoot_NeverExplainedAway()
    {
        var v = FeetAuto.FeetFit(true, true, Parsed(Img(skin: 0.02)));
        Assert.Equal(FeetAuto.FeetKind.Failed, v.Kind); Assert.Contains("bare foot / toes visible outside the shoe", v.Why);
        Assert.Equal(FeetAuto.FeetKind.Failed, FeetAuto.FeetFit(true, true, Parsed(Img(skin: FeetAuto.FeetSkinMaxFrac + 0.001))).Kind);   // just over the limit still fails
    }
    [Fact] public void NearBlackOrUnreadableOrMissingCloseUp_IsInvalid_NotAPass()
    {
        Assert.Equal(FeetAuto.FeetKind.Invalid, FeetAuto.FeetFit(true, true, Parsed(Img(ok: false, luma: 0.02, p95: 0.09, why: "near-black render"))).Kind);
        Assert.Equal(FeetAuto.FeetKind.Invalid, FeetAuto.FeetFit(true, true, Parsed(Img(luma: 0.05))).Kind);      // dark even though flagged ok
        Assert.Equal(FeetAuto.FeetKind.Invalid, FeetAuto.FeetFit(true, true, Parsed(Img(p95: 0.1))).Kind);
        Assert.Equal(FeetAuto.FeetKind.Invalid, FeetAuto.FeetFit(true, true, null).Kind);
        Assert.Equal(FeetAuto.FeetKind.Invalid, FeetAuto.FeetFit(true, true, Parsed("garbage")).Kind);
    }
    [Fact] public void CleanCloseUp_AndMatchingAnimations_IsVerified()
    {
        Assert.Equal(FeetAuto.FeetKind.Verified, FeetAuto.FeetFit(true, true, Parsed(Img(skin: 0.001))).Kind);
        Assert.Equal(FeetAuto.FeetKind.Verified, FeetAuto.FeetFit(false, false, Parsed(Img())).Kind);
    }
    [Fact] public void ParseFeetImage_ReadsTheAnalyzerLine()
    {
        var i = Parsed(Img(skin: 0.0123)); Assert.True(i.Readable); Assert.Equal(0.0123, i.SkinFootFrac, 4); Assert.Equal("/x/feet.jpg", i.Path);
        Assert.False(Parsed(null).Readable);
    }
    [Theory]
    [InlineData(2, false, 10, false, false)]    // outfit changed 2 s ago
    [InlineData(20, true, 10, false, false)]    // pose transition in progress
    [InlineData(20, false, 1, false, false)]    // posture changed a moment ago
    [InlineData(20, false, 10, true, false)]    // sitting
    [InlineData(20, false, 10, false, true)]
    public void PressReady(double stable, bool busy, double settled, bool sitting, bool ok) => Assert.Equal(ok, FeetAuto.FeetPressReady(stable, busy, settled, sitting).ok);
    [Theory]
    [InlineData(0, 0, true)] [InlineData(1, 30, false)] [InlineData(1, 100, true)] [InlineData(3, 1000, false)]
    public void RetryIsBounded(int rounds, double since, bool allowed) => Assert.Equal(allowed, FeetAuto.FeetRetryAllowed(rounds, since));

    // ---- the whole flow through a fake host ----
    sealed class RefCmp : IEqualityComparer<Primitive> { public bool Equals(Primitive a, Primitive b) => ReferenceEquals(a, b); public int GetHashCode(Primitive p) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p); }
    sealed class Host : IFeetHost
    {
        public string Dir; public DateTime T = new(2026, 10, 28, 11, 45, 0);
        public List<Primitive> Worn = new(); public Dictionary<Primitive, UUID> Ids = new(new RefCmp());
        public List<UUID> Anims = new(); public bool Busy, Sitting; public double Settled = 60;
        public Queue<string> Closeups = new(); public string DefaultCloseup = null; public List<double> Exposures = new();
        public List<string> Touches = new(), Logs = new();
        public Action OnTouch;
        public GridClient Client => null; public Simulator Sim => null;
        public void Log(string k, string m) => Logs.Add(k + ": " + m);
        public string RouteDir => Dir;
        public List<Primitive> WornPrims() => HudHiddenAtFirst ? Worn.Where(p => Ids[p] != Hud).ToList() : new(Worn);   // the sim's list is not complete yet right after login
        public UUID AttachItemId(Primitive p) => Ids.TryGetValue(p, out var u) ? u : UUID.Zero;
        public Task<string> AttachItem(UUID item, string pointArg) { if (!HudHiddenAtFirst) throw new InvalidOperationException("HUD is already worn in these tests"); HudHiddenAtFirst = false; return Task.FromResult("already worn: item " + item + " (found after re-requesting objects; nothing attached)"); }
        public List<string> DetachLog; public bool HudHiddenAtFirst;
        public Task<string> DetachItemAsync(UUID item, string why) { DetachLog?.Add(why); return Task.FromResult("ok"); }
        public List<Primitive> LinkPrims(Primitive root) => new();
        public Task EnsureProperties(Simulator sim, List<Primitive> prims) => Task.CompletedTask;
        public Task<string> TouchAttachment(string rest) { Touches.Add(rest); OnTouch?.Invoke(); return Task.FromResult("touched f3"); }
        public List<UUID> SelfAnims() => new(Anims);
        public bool PoseBusy => Busy; public double PoseSettledSec => Settled; bool IFeetHost.Sitting => Sitting;
        public Task<string> FeetCloseupJson(double exposure) { Exposures.Add(exposure); return Task.FromResult(Closeups.Count > 0 ? Closeups.Dequeue() : DefaultCloseup); }
        public void Wear(UUID item) { var p = new Primitive(); Worn.Add(p); Ids[p] = item; }
    }
    static (FeetAuto feet, Host host, string state) Make(bool bootsWorn = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "feet-fit-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "_feet-rules.json"), "{\"default\":\"f1\",\"footwear\":[{\"item\":\"" + Boots + "\",\"name\":\"Moca\",\"feet\":\"f3\"}]}");
        var h = new Host { Dir = dir };
        h.Wear(Hud); for (int i = 0; i < 4; i++) h.Wear(UUID.Random()); if (bootsWorn) h.Wear(Boots);
        var f = new FeetAuto(h) { StatePath = Path.Combine(dir, "run", "feet-state.txt") };
        var now = h.T; f.Now = () => now; f.Delay = _ => Task.CompletedTask;
        f.NewLogin();
        return (f, h, f.StatePath);
    }
    static async Task Settle(FeetAuto f, Host h) { await f.FeetTick(); Advance(f, h, 50); }   // the first poll after login sees the worn set; 50 s later it is stable and the login has settled
    static void Advance(FeetAuto f, Host h, double sec) { h.T = h.T.AddSeconds(sec); var t = h.T; f.Now = () => t; }

    [Fact] public async Task NoPressWhileTheOutfitIsChanging_ThenItPressesOnceStable()
    {
        var (f, h, _) = Make(); h.Anims.AddRange(FeetAuto.HeelFeetAnims); h.DefaultCloseup = Img();
        Assert.StartsWith("waiting: login settling", await f.FeetTick()); Advance(f, h, 45); h.Wear(UUID.Random());   // past the login grace; an item arrives
        Assert.StartsWith("waiting: outfit change in progress", await f.FeetTick());
        h.Wear(UUID.Random()); Advance(f, h, 4);                       // another item arrives mid swap
        Assert.StartsWith("waiting: outfit change in progress", await f.FeetTick());
        Assert.Empty(h.Touches);
        Advance(f, h, 9);                                              // stable now
        await f.FeetTick(); Advance(f, h, 4); await f.FeetTick();
        Assert.True(h.Touches.Count <= 1);
    }
    [Fact] public async Task NoPressWhilePoseChangesOrSitting()
    {
        var (f, h, _) = Make(); h.DefaultCloseup = Img(); await Settle(f, h);
        h.Busy = true; Assert.Contains("pose / sit transition", await f.FeetTick());
        h.Busy = false; h.Settled = 1; Assert.Contains("posture changed a moment ago", await f.FeetTick());
        h.Settled = 60; h.Sitting = true; Assert.Contains("sitting", await f.FeetTick());
        Assert.Empty(h.Touches);
    }
    [Theory] [InlineData(5, false)] [InlineData(39, false)] [InlineData(41, true)]
    public void NoHudRoundTripBeforeTheLoginHasSettled(double sinceLogin, bool ok) => Assert.Equal(ok, FeetAuto.FeetPressReady(60, false, 60, false, sinceLogin).ok);
    [Fact] public void AnAlreadyWornHud_IsRecognisedFromTheAttachResult()
    {
        Assert.True(FeetAuto.AttachResultAlreadyWorn("already worn: item 961d2d4b (found after re-requesting objects; nothing attached)"));
        Assert.False(FeetAuto.AttachResultAlreadyWorn("attached 961d2d4b to HUD Top Left")); Assert.False(FeetAuto.AttachResultAlreadyWorn(null));
    }
    [Fact] public void TheHudIsNotTheBody_AttachingOrDetachingItIsNotABodyChange()
    {
        var body = UUID.Random(); var hud = UUID.Random();
        var a = FeetAuto.FeetBodySignature(new[] { (body, "Maitreya Mesh Body - LaraX V1.1", 40) });
        var b = FeetAuto.FeetBodySignature(new[] { (body, "Maitreya Mesh Body - LaraX V1.1", 40), (hud, "Maitreya Mesh Body - LaraX HUD V1.1", 60) });
        Assert.NotEqual("", a); Assert.Equal(a, b);
    }
    [Fact] public async Task TheSimRestoredHud_WornAllAlong_IsNeverDetachedAfterAPress()
    {
        var (f, h, _) = Make(); var detached = new List<string>(); h.DetachLog = detached; h.HudHiddenAtFirst = true;
        h.DefaultCloseup = Img(); h.OnTouch = () => { h.Anims.Clear(); h.Anims.AddRange(FeetAuto.HeelFeetAnims); };
        await Settle(f, h); await f.FeetTick(); Advance(f, h, 4); await f.FeetTick();
        Assert.Empty(detached);   // AttachItem answered "already worn": the HUD belongs to the outfit
        Assert.StartsWith("feet fit verified", f.LastFitText);
    }
    [Fact] public async Task LoginClearsTheStaleState()
    {
        var (f, h, state) = Make(); Directory.CreateDirectory(Path.GetDirectoryName(state)!); File.WriteAllText(state, "f3");
        f.NewLogin(); Assert.False(File.Exists(state));
        Assert.Contains(h.Logs, l => l.Contains("new login: last pressed feet pose cleared"));
        await Task.CompletedTask;
    }
    [Fact] public async Task StaleF3State_IsNotTrusted_ToesPokingOut_ForcesAPressAndFailsLoudlyWhenItNeverFits()
    {
        var (f, h, state) = Make(); Directory.CreateDirectory(Path.GetDirectoryName(state)!); File.WriteAllText(state, "f3");   // the stale file says f3 is pressed
        h.Anims.AddRange(FeetAuto.HeelFeetAnims); h.DefaultCloseup = Img(skin: 0.03);                                          // but toes are out in every close-up
        await Settle(f, h); await f.FeetTick();
        Assert.Equal(FeetAuto.FeetPressAttempts, h.Touches.Count);                                    // bounded
        Assert.False(File.Exists(state));                                                              // never written as "done"
        Assert.StartsWith("FEET NOT VERIFIED", f.LastFitText); Assert.Contains(h.Logs, l => l.StartsWith("feet: feet NOT verified"));
        Assert.Contains("never call this finished", f.LastFitText);
        // cooling down, then bounded rounds
        Advance(f, h, 4); Assert.Contains("cooling down", await f.FeetTick());
        for (int round = 1; round < FeetAuto.FeetGiveUpRounds; round++) { Advance(f, h, 100); await f.FeetTick(); }
        Advance(f, h, 1000); Assert.Contains("gave up", await f.FeetTick());
        Assert.Equal(FeetAuto.FeetPressAttempts * FeetAuto.FeetGiveUpRounds, h.Touches.Count);
    }
    [Fact] public async Task PressThatFits_IsVerifiedFromEvidence_AndOnlyThenRemembered()
    {
        var (f, h, state) = Make(); h.DefaultCloseup = Img(skin: 0.0);
        h.OnTouch = () => { h.Anims.Clear(); h.Anims.AddRange(FeetAuto.HeelFeetAnims); };       // the body restarts its heel-foot animations
        await Settle(f, h); await f.FeetTick(); Advance(f, h, 4); await f.FeetTick();
        Assert.Single(h.Touches); Assert.Equal("f3", File.ReadAllText(state));
        Assert.StartsWith("feet fit verified", f.LastFitText); Assert.Contains(h.Logs, l => l.StartsWith("feet: feet fit verified"));
    }
    [Fact] public async Task FirstPressMissesTheBoots_SecondFits()
    {
        var (f, h, state) = Make(); int n = 0; h.DefaultCloseup = Img();
        h.OnTouch = () => { if (++n == 2) h.Anims.AddRange(FeetAuto.HeelFeetAnims); };         // the first press landed mid swap and did not take
        await Settle(f, h); await f.FeetTick(); Advance(f, h, 4); await f.FeetTick();
        Assert.Equal(2, h.Touches.Count); Assert.Equal("f3", File.ReadAllText(state));
    }
    [Fact] public async Task NearBlackCloseUp_IsRetakenBrighter_ThenNotVerifiedIfItStaysDark()
    {
        var (f, h, state) = Make(); h.Anims.AddRange(FeetAuto.HeelFeetAnims);
        h.DefaultCloseup = Img(ok: false, luma: 0.02, p95: 0.09, why: "near-black render");
        await Settle(f, h); await f.FeetTick(); Advance(f, h, 4); await f.FeetTick();
        Assert.False(File.Exists(state)); Assert.StartsWith("FEET NOT VERIFIED", f.LastFitText);
        Assert.Equal(FeetAuto.FeetRetakeExposure.Take(FeetAuto.FeetRetakes), h.Exposures.Take(FeetAuto.FeetRetakes));   // retakes get brighter
        Assert.True(FeetAuto.FeetRetakeExposure[1] > FeetAuto.FeetRetakeExposure[0]);
    }
    [Fact] public async Task NearBlackFirstThenUsable_IsVerifiedFromTheRetake()
    {
        var (f, h, state) = Make(); h.Anims.AddRange(FeetAuto.HeelFeetAnims);
        h.Closeups.Enqueue(Img(ok: false, luma: 0.02, p95: 0.09, why: "near-black render")); h.DefaultCloseup = Img(skin: 0.0);
        await Settle(f, h); await f.FeetTick(); Advance(f, h, 4); await f.FeetTick();
        Assert.StartsWith("feet fit verified", f.LastFitText); Assert.Single(h.Touches);
        Assert.Equal(2, h.Exposures.Count); Assert.True(h.Exposures[1] > h.Exposures[0]);   // the dark first take was retaken brighter and the retake decided
    }
    [Fact] public async Task NoHeelAnimations_WithBootsOn_IsFlatFootBesideBoots_EvenIfThePictureIsClean()
    {
        var (f, h, _) = Make(); h.DefaultCloseup = Img();   // no heel anims in h.Anims, boots worn, picture clean
        await Settle(f, h); await f.FeetTick();
        Assert.StartsWith("FEET NOT VERIFIED", f.LastFitText); Assert.Contains("flat beside heeled boots", f.LastFitText);
    }
    [Fact] public async Task StatusReportsTheFitText()
    {
        var (f, h, _) = Make();
        Assert.Contains("not checked yet", await f.FeetCmd(new[] { "feet" }));
        await Task.CompletedTask;
    }
}
