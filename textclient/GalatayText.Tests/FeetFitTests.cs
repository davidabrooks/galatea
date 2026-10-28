using System.Text.Json;
using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-10-28 (David): the feet fit is proven by the body's own foot mesh (the visible pair b46/47 = f1 .. b54/55 = f5), never from feet-state.txt, the press log, animations or a skin-colour guess; the close-up is informational.
public class FeetFitTests
{
    static readonly UUID Boots = UUID.Parse("810c862e-8d2d-3d28-82a5-19e9fd216ce9");
    static readonly UUID Hud = UUID.Parse("961d2d4b-4948-3bac-ab88-0a7ba3b99302");
    static string Img(bool ok = true, double luma = 0.4, double p95 = 0.9, string why = "ok") =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["ok"] = ok, ["mean_luma"] = luma, ["p95_luma"] = p95, ["why"] = why, ["image"] = "/x/feet.jpg" });
    static List<(string, float)> Mesh(params (string, float)[] over)
    {
        var l = new List<(string, float)>(); for (int n = 46; n <= 55; n++) l.Add(("b" + n, 0f));
        foreach (var o in over) { var i = l.FindIndex(x => x.Item1 == o.Item1); if (i >= 0) l[i] = o; else l.Add(o); }
        return l;
    }
    static List<(string, float)> Shows(string feet) { int k = int.Parse(feet[1..]); int b = 46 + (k - 1) * 2; return Mesh(("b" + b, 1f), ("b" + (b + 1), 1f)); }
    static FeetAuto.FeetImage Parsed(string j) => FeetAuto.ParseFeetImage(j);

    // ---- the mesh proof: which foot pair is visible ----
    [Theory] [InlineData("f1")] [InlineData("f2")] [InlineData("f3")] [InlineData("f4")] [InlineData("f5")]
    public void ExactlyOneVisiblePair_MapsToItsButton(string feet) => Assert.Equal((feet, "ok"), FeetAuto.FeetShownFromMesh(Shows(feet)));
    [Fact] public void MeshProof_IsMissingNotGuessed()
    {
        Assert.Null(FeetAuto.FeetShownFromMesh(null).feet);
        Assert.Null(FeetAuto.FeetShownFromMesh(Mesh()).feet);                                                              // no pair visible
        Assert.Contains("no foot mesh pair", FeetAuto.FeetShownFromMesh(Mesh()).why);
        Assert.Equal("f3", FeetAuto.FeetShownFromMesh(Shows("f3").Concat(new[] { ("b46", 1f) })).feet);                    // half of another pair (b46 alone) is not a second foot
        var two = Mesh(("b46", 1f), ("b47", 1f), ("b50", 1f), ("b51", 1f)); Assert.Contains("several", FeetAuto.FeetShownFromMesh(two).why);
        Assert.Null(FeetAuto.FeetShownFromMesh(two).feet);
        Assert.Null(FeetAuto.FeetShownFromMesh(Shows("f3").Where(x => x.Item1 != "b52")).feet);                            // a prim missing: cannot rule out another pair
        Assert.Contains("b52", FeetAuto.FeetShownFromMesh(Shows("f3").Where(x => x.Item1 != "b52")).why);
        Assert.Null(FeetAuto.FeetShownFromMesh(Mesh(("b50", 1f))).feet);                                                   // half a pair is not a foot
    }

    // ---- the pure verdict ----
    [Fact] public void ShownFootIsTheWantedOne_IsVerified_WhateverThePictureSays()
    {
        Assert.Equal(FeetAuto.FeetKind.Verified, FeetAuto.FeetFit("f3", "f3", "ok", Parsed(Img())).Kind);
        Assert.Equal(FeetAuto.FeetKind.Verified, FeetAuto.FeetFit("f3", "f3", "ok", null).Kind);                    // no close-up at all: the mesh is the proof
        Assert.Equal(FeetAuto.FeetKind.Verified, FeetAuto.FeetFit("f1", "f1", "ok", Parsed(Img(ok: false, luma: 0.01, p95: 0.02))).Kind);
        Assert.Contains("informational", FeetAuto.FeetFit("f3", "f3", "ok", Parsed(Img())).Why);
    }
    [Fact] public void FlatFootShown_WithHeeledBootsWanted_Fails_EvenWithAPrettyPicture()
    {
        var v = FeetAuto.FeetFit("f3", "f1", "ok", Parsed(Img()));
        Assert.Equal(FeetAuto.FeetKind.Failed, v.Kind); Assert.Contains("shows foot f1 but f3 is wanted", v.Why);
    }
    [Fact] public void HeeledFootShown_WithFlatWanted_Fails() => Assert.Equal(FeetAuto.FeetKind.Failed, FeetAuto.FeetFit("f1", "f3", "ok", Parsed(Img())).Kind);
    [Fact] public void MissingMeshProof_NeverVerifies_EvenWithACleanPicture()
    {
        var v = FeetAuto.FeetFit("f3", null, "no foot mesh pair is visible", Parsed(Img()));
        Assert.Equal(FeetAuto.FeetKind.Failed, v.Kind); Assert.Contains("no proof", v.Why);
    }
    [Fact] public void ParseFeetImage_ReadsTheAnalyzerLine()
    {
        var i = Parsed(Img(luma: 0.3)); Assert.True(i.Readable); Assert.Equal(0.3, i.MeanLuma, 4); Assert.Equal("/x/feet.jpg", i.Path);
        Assert.False(Parsed(null).Readable); Assert.True(FeetAuto.FeetImageInvalid(Parsed("garbage"))); Assert.True(FeetAuto.FeetImageInvalid(Parsed(Img(luma: 0.05))));
    }
    [Fact] public void ThePageOpenTouchIsOnTheHandsFeetTab_NotTheAlphaTab()
    {
        Assert.Equal(0.70f, FeetAuto.FeetTabSt.X, 3); Assert.Equal(0.5f, FeetAuto.FeetTabSt.Y, 3); Assert.Equal(4, FeetAuto.FeetTabFace);
        Assert.True(FeetAuto.FeetTabSt.X > 0.5f);   // u 0.1 was the ALPHA tab: the foot press did nothing
    }
    [Fact] public void FootButtonsAreMappedToTheirMeshPairs() =>
        Assert.Equal(new[] { "f1:b46/b47", "f2:b48/b49", "f3:b50/b51", "f4:b52/b53", "f5:b54/b55" }, FeetAuto.FootMeshPairs.Select(p => $"{p.feet}:{p.a}/{p.b}").ToArray());
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
        public string Shown = "f1"; public bool MeshUnreadable; public bool Busy, Sitting; public double Settled = 60;
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
        public List<Primitive> HudPrims = new(); public List<(Primitive prim, Vector3 st, int face)> PrimTouches = new();
        public List<Primitive> LinkPrims(Primitive root) => ReferenceEquals(root, HudRoot) ? HudPrims : new();
        public Primitive HudRoot => Worn.FirstOrDefault(p => Ids[p] == Hud);
        public Task<List<(string name, float alpha)>> FootMeshAlphas() => Task.FromResult(MeshUnreadable ? null : Shows(Shown).Select(x => (x.Item1, x.Item2)).ToList());
        public Task TouchPrim(Primitive prim, Vector3 st, int face) { PrimTouches.Add((prim, st, face)); OnTabTouch?.Invoke(); return Task.CompletedTask; }
        public Action OnTabTouch;
        public Task EnsureProperties(Simulator sim, List<Primitive> prims) => Task.CompletedTask;
        public Task<string> TouchAttachment(string rest) { Touches.Add(rest); OnTouch?.Invoke(); return Task.FromResult("touched f3"); }
        public bool PoseBusy => Busy; public double PoseSettledSec => Settled; bool IFeetHost.Sitting => Sitting;
        public Task<string> FeetCloseupJson(double exposure) { Exposures.Add(exposure); return Task.FromResult(Closeups.Count > 0 ? Closeups.Dequeue() : DefaultCloseup); }
        public Primitive Wear(UUID item, string name = null) { var p = new Primitive { ID = UUID.Random() }; if (name != null) p.Properties = new Primitive.ObjectProperties { Name = name }; Worn.Add(p); Ids[p] = item; return p; }
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
        var (f, h, _) = Make(); h.Shown = "f3"; h.DefaultCloseup = Img();
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
        var body = UUID.Random(); var hud = UUID.Random(); var obj = UUID.Random();
        var a = FeetAuto.FeetBodySignature(new[] { (body, "Maitreya Mesh Body - LaraX V1.1", obj) });
        var b = FeetAuto.FeetBodySignature(new[] { (body, "Maitreya Mesh Body - LaraX V1.1", obj), (hud, "Maitreya Mesh Body - LaraX HUD V1.1", UUID.Random()) });
        Assert.NotEqual("", a); Assert.Equal(a, b);
    }
    [Fact] public async Task TheSimRestoredHud_WornAllAlong_IsNeverDetachedAfterAPress()
    {
        var (f, h, _) = Make(); var detached = new List<string>(); h.DetachLog = detached; h.HudHiddenAtFirst = true;
        h.DefaultCloseup = Img(); h.OnTouch = () => { h.Shown = "f3"; };
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
    [Fact] public async Task StaleF3State_IsNotTrusted_FlatFootShowing_ForcesAPressAndFailsLoudlyWhenItNeverFits()
    {
        var (f, h, state) = Make(); Directory.CreateDirectory(Path.GetDirectoryName(state)!); File.WriteAllText(state, "f3");   // the stale file says f3 is pressed
        h.DefaultCloseup = Img();   // the body shows a flat foot (f1) in every poll: the press never takes
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
        var (f, h, state) = Make(); h.DefaultCloseup = Img();
        h.OnTouch = () => { h.Shown = "f3"; };       // the press takes: the body now shows the f3 foot
        await Settle(f, h); await f.FeetTick(); Advance(f, h, 4); await f.FeetTick();
        Assert.Single(h.Touches); Assert.Equal("f3", File.ReadAllText(state));
        Assert.StartsWith("feet fit verified", f.LastFitText); Assert.Contains(h.Logs, l => l.StartsWith("feet: feet fit verified"));
    }
    [Fact] public async Task FirstPressMissesTheBoots_SecondFits()
    {
        var (f, h, state) = Make(); int n = 0; h.DefaultCloseup = Img();
        h.OnTouch = () => { if (++n == 2) h.Shown = "f3"; };         // the first press landed mid swap and did not take
        await Settle(f, h); await f.FeetTick(); Advance(f, h, 4); await f.FeetTick();
        Assert.Equal(2, h.Touches.Count); Assert.Equal("f3", File.ReadAllText(state));
    }
    [Fact] public async Task NearBlackCloseUp_IsRetakenBrighter_ButTheMeshAloneDecides()
    {
        var (f, h, state) = Make(); h.Shown = "f3";
        h.DefaultCloseup = Img(ok: false, luma: 0.02, p95: 0.09, why: "near-black render");
        await Settle(f, h); await f.FeetTick(); Advance(f, h, 4); await f.FeetTick();
        Assert.StartsWith("feet fit verified", f.LastFitText); Assert.Contains("no usable close-up (informational only)", f.LastFitText);   // the picture is not a proof
        Assert.Equal(FeetAuto.FeetRetakeExposure.Take(FeetAuto.FeetRetakes), h.Exposures.Take(FeetAuto.FeetRetakes));   // retakes get brighter
        Assert.True(FeetAuto.FeetRetakeExposure[1] > FeetAuto.FeetRetakeExposure[0]);
    }
    [Fact] public async Task NearBlackFirstThenUsable_IsVerifiedFromTheRetake()
    {
        var (f, h, state) = Make(); h.Shown = "f3";
        h.Closeups.Enqueue(Img(ok: false, luma: 0.02, p95: 0.09, why: "near-black render")); h.DefaultCloseup = Img();
        await Settle(f, h); await f.FeetTick(); Advance(f, h, 4); await f.FeetTick();
        Assert.StartsWith("feet fit verified", f.LastFitText); Assert.Single(h.Touches);
        Assert.Equal(2, h.Exposures.Count); Assert.True(h.Exposures[1] > h.Exposures[0]);   // the dark first take was retaken brighter and the retake decided
    }
    [Fact] public async Task FlatFootMeshShowing_WithBootsOn_IsNotVerified_EvenIfThePictureIsClean()
    {
        var (f, h, _) = Make(); h.DefaultCloseup = Img();   // the flat foot (f1) shows, boots worn, picture clean
        await Settle(f, h); await f.FeetTick();
        Assert.StartsWith("FEET NOT VERIFIED", f.LastFitText); Assert.Contains("shows foot f1 but f3 is wanted", f.LastFitText);
    }
    [Fact] public async Task UnreadableBodyMesh_IsNeverAFit_EvenWithACleanPicture_AndPressesStayBounded()
    {
        var (f, h, state) = Make(); h.Shown = "f3"; h.MeshUnreadable = true; h.DefaultCloseup = Img();   // f3 may well be on, but nothing proves it
        await Settle(f, h); await f.FeetTick();
        Assert.StartsWith("FEET NOT VERIFIED", f.LastFitText); Assert.Contains("no proof of the foot pose", f.LastFitText);
        Assert.False(File.Exists(state)); Assert.Equal(FeetAuto.FeetPressAttempts, h.Touches.Count);
        for (int i = 0; i < 20; i++) { Advance(f, h, 100); await f.FeetTick(); }
        Assert.Equal(FeetAuto.FeetPressAttempts * FeetAuto.FeetGiveUpRounds, h.Touches.Count);
    }
    [Fact] public async Task TheFeetPageIsOpenedOnTheHandsFeetTab_BeforeTheButtonIsPressed()
    {
        var (f, h, state) = Make();
        Primitive Prim(string name, float z, uint local) => new Primitive { ID = UUID.Random(), LocalID = local, Position = new Vector3(0, 0, z), Properties = new Primitive.ObjectProperties { Name = name } };
        var tabsLow = Prim("tabs", 0.2f, 11); var btn = Prim("f3", 0.1f, 12); var tabsHigh = Prim("tabs", 0.9f, 13);   // pages stack in z: the tab bar just above f3 is the lowest one above it
        h.HudPrims.AddRange(new[] { Prim("root", 0f, 10), btn, tabsHigh, tabsLow });
        var order = new List<string>(); h.OnTabTouch = () => order.Add("tab"); h.OnTouch = () => { order.Add("press"); h.Shown = "f3"; };
        h.DefaultCloseup = Img();
        await Settle(f, h); await f.FeetTick(); Advance(f, h, 4); await f.FeetTick();
        Assert.Equal(new[] { "tab", "press" }, order);
        var t = Assert.Single(h.PrimTouches);
        Assert.Equal(11u, t.prim.LocalID); Assert.Equal(0.70f, t.st.X, 3); Assert.Equal(4, t.face); Assert.NotEqual(0.1f, t.st.X, 3);
        Assert.Contains(h.Logs, l => l.Contains("opened the HANDS/FEET tab"));
        Assert.StartsWith("feet fit verified", f.LastFitText);
    }
    [Fact] public async Task StatusReportsTheFitText()
    {
        var (f, h, _) = Make();
        Assert.Contains("not checked yet", await f.FeetCmd(new[] { "feet" }));
        await Task.CompletedTask;
    }

    // ---- 2026-10-28 13:30 live loop: the signature flapped every poll, "worn body changed" every ~2 s, f3 re-pressed every 12 s ----
    [Fact] public void FlappingChildPrimCounts_AreNotABodyChange()
    {
        var item = UUID.Random(); var obj = UUID.Random(); var sigs = new HashSet<string>();
        foreach (var kids in new[] { 12, 40, 7, 63, 0, 41, 5 })   // prims streaming in and out; the old signature had the count in it
            sigs.Add(FeetAuto.FeetBodySignature(new[] { (item, "Maitreya Mesh Body - LaraX V1.1", obj), (UUID.Random(), "Maitreya Mesh Body - LaraX HUD V1.1", UUID.Random()) }));
        Assert.Single(sigs);
    }
    [Fact] public async Task VerifiedFit_SurvivesHudAttachDetachChurn_NoClearNoPress()
    {
        var (f, h, state) = Make(); var body = h.Wear(UUID.Random(), "Maitreya Mesh Body - LaraX V1.1");
        h.DefaultCloseup = Img(); h.OnTouch = () => { h.Shown = "f3"; };
        await Settle(f, h); await f.FeetTick(); Advance(f, h, 4); await f.FeetTick();
        Assert.Equal("f3", File.ReadAllText(state)); int presses = h.Touches.Count; h.Logs.Clear();
        var hud = h.Worn.First(p => h.Ids[p] == Hud);
        for (int i = 0; i < 20; i++)   // the HUD (and other prims) come and go every poll
        {
            if (i % 2 == 0) h.Worn.Remove(hud); else h.Worn.Add(hud);
            Advance(f, h, 4); await f.FeetTick();
        }
        Assert.Equal(presses, h.Touches.Count); Assert.Equal("f3", File.ReadAllText(state));
        Assert.DoesNotContain(h.Logs, l => l.Contains("worn body changed"));
    }
    [Fact] public async Task ARealBodySwap_IsOneChange_AndTheFitIsVerifiedAgain()
    {
        var (f, h, state) = Make(); var body = h.Wear(UUID.Random(), "Maitreya Mesh Body - LaraX V1.1");
        h.DefaultCloseup = Img(); h.OnTouch = () => { h.Shown = "f3"; };
        await Settle(f, h); await f.FeetTick(); Advance(f, h, 4); await f.FeetTick(); h.Logs.Clear();
        body.ID = UUID.Random();   // re-attached: a new attachment object id
        Advance(f, h, 4); await f.FeetTick(); Advance(f, h, 4); await f.FeetTick();
        Assert.Single(h.Logs, l => l.Contains("worn body changed"));
    }
    [Theory] [InlineData("f3", "f3", 10, false)] [InlineData("f3", "f3", 59, false)] [InlineData("f3", "f3", 61, true)] [InlineData("f3", "f1", 5, true)] [InlineData(null, "f3", 5, true)]
    public void SamePoseIsNotPressedAgainWithinTheInterval(string last, string wanted, double since, bool ok) => Assert.Equal(ok, FeetAuto.FeetPressRateOk(last, wanted, since));
    [Fact] public void OnlyTheFirstLoopStartRuns_LoginAsyncStartsOnePerLogin()
    {
        var (f, _, _) = Make(); Assert.True(f.ClaimLoop()); Assert.False(f.ClaimLoop()); Assert.False(f.ClaimLoop());
    }
}
