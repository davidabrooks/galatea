using System;
using System.Linq;
using GalatayText;
using Xunit;
public class IdlePoseTests
{
    static Program.IdleIn S(double idle = 400, bool collar = true, bool chain = false, bool leashed = false, bool mapped = true,
        bool following = false, bool sitObj = false, bool moving = false, bool forceSat = false, bool sitLocked = false, bool driving = false, bool busy = false, bool on = true, bool dancing = false, bool android = false, bool leashHold = false)
        => new(on, leashed, driving, following, sitObj, moving, forceSat, sitLocked, busy, dancing, idle, 300, collar, chain, mapped, android, leashHold);
    [Fact] public void RotationRandom3to10MinCollarOnlyNotWhileDancing()
    {
        Assert.Equal(180, Program.IdleRotateSec(0)); Assert.Equal(600, Program.IdleRotateSec(1));
        Assert.True(Program.IdleShouldRotate("collar", 400, 300, false));
        Assert.False(Program.IdleShouldRotate("collar", 200, 300, false));
        Assert.False(Program.IdleShouldRotate("collar", 400, 300, true));
        Assert.True(Program.IdleShouldRotate("groundsit", 900, 300, false));   // 2026-10-23: AO ground sits rotate too
        Assert.False(Program.IdleShouldRotate("groundsit", 900, 300, true));
        for (int i = 0; i < 50; i++) Assert.NotEqual("nadu", Program.PickIdlePose("nadu", System.Random.Shared.NextDouble()));
    }
    [Fact] public void UnleashWhileKneelingWithCollarVisibleStaysKneeling()
    {
        Assert.Null(Program.IdleMustExit("collar", false, false, false, false, false, false, false));   // just unleashed
        Assert.Null(Program.IdleMustExit("collar", false, false, false, false, true, false, false));    // just leashed
        Assert.Null(Program.IdleSwitch("collar", true, false, false, true));
        Assert.Equal("groundsit", Program.IdleSwitch("collar", false, false, false, true));  // collar hidden, no leash
        Assert.Null(Program.IdleSwitch("collar", false, false, true, true));                 // invisible leash: keep kneeling
        Assert.Null(Program.IdleSwitch("groundsit", false, true, true, true));               // chain appears while leashed: exit only (IdleMustExit "leashed"), never a kneel (2026-10-26)
        Assert.Equal("walker", Program.IdleMustExit("collar", false, false, false, false, true, false, true));
        Assert.Null(Program.IdleMustExit("collar", false, false, false, false, false, true, false));   // David walking away alone: stay
        Assert.Equal("follow", Program.IdleMustExit("collar", true, false, false, false, false, true, false));
        Assert.Equal("moving", Program.IdleMustExit("groundsit", false, false, true, false, false, false, false));
        Assert.False(Program.LeashIdleMustRise(true, false, 3f, 1f));    // he stands still
        Assert.False(Program.LeashIdleMustRise(true, true, 0.8f, 1f));   // he moves nearby without pulling
        Assert.False(Program.LeashIdleMustRise(false, true, 5f, 1f));    // not leashed
        Assert.True(Program.LeashIdleMustRise(true, true, 1.3f, 1f));    // he walks off: real pull
    }
    [Fact] public void DancingIsNeverIdle()
    {
        Assert.Null(Program.IdleEnter(S(dancing: true, idle: 9999)));
        Assert.Null(Program.IdleEnter(S(dancing: true, collar: false, idle: 9999)));
        Assert.True(Program.IdleDancing(false, new[] { "STAND", "Dance Ball 2.0" }));
        Assert.True(Program.IdleDancing(false, new[] { "attachment 'VISTA ... DANCES'" }));
        Assert.True(Program.IdleDancing(true, new string[0]));
        Assert.False(Program.IdleDancing(false, new[] { "STAND", "attachment 'VISTA ANIMATIONS *HUD 6.3*MARTHA STS BENTO AO-V1.7'", "The V - Bento" }));
    }
    [Fact] public void NeverGroundSitWhenCollarOrLeashVisible()
    {
        foreach (var leashed in new[] { false, true }) foreach (var mapped in new[] { false, true })
        {
            Assert.Equal("collar", Program.IdleEnter(S(collar: true, chain: false, leashed: leashed, mapped: mapped)));
            Assert.Equal("collar", Program.IdleEnter(S(collar: false, chain: true, leashed: leashed, mapped: mapped)));
            Assert.Equal("collar", Program.IdleEnter(S(collar: true, chain: true, leashed: leashed, mapped: mapped)));
        }
        Assert.Equal("collar", Program.IdlePoseKind(true, false)); Assert.Equal("collar", Program.IdlePoseKind(false, true));
    }
    [Fact] public void ChoiceAlwaysFromTheListNoRepeat()
    {
        var allowed = new[] { "cutie", "coy", "kneel", "nadu", "pretty", "shy", "tower" };
        Assert.Equal(allowed, Program.IdleCollarPoses);
        string last = null; var rnd = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            var p = Program.PickIdlePose(last, rnd.NextDouble());
            Assert.Contains(p, allowed); Assert.NotEqual(last, p); last = p;
        }
        Assert.Equal("cutie", Program.PickIdlePose(null, 0)); Assert.Equal("tower", Program.PickIdlePose(null, 1));
        Assert.Equal("coy", Program.PickIdlePose("cutie", 0));
    }
    [Fact] public void VisibleLeashOrCollar_CollarPose_ElseAoGroundSit()
    {
        Assert.Equal("collar", Program.IdlePoseKind(true, false));     // human outfit, collar visible
        Assert.Equal("collar", Program.IdlePoseKind(false, true));     // collar hidden but the chain is visible
        Assert.Equal("collar", Program.IdlePoseKind(true, true));
        Assert.Equal("groundsit", Program.IdlePoseKind(false, false)); // Android*, Stealth on, no visible leash
        Assert.Equal("collar", Program.IdleEnter(S(leashed: true, chain: true, collar: false)));
        Assert.Null(Program.IdleEnter(S(collar: false, leashed: true)));     // invisible leash + hidden collar: never AO ground sit on a leash
        Assert.Equal("groundsit", Program.IdleEnter(S(collar: false)));
        Assert.Null(Program.IdleEnter(S(collar: false, mapped: false)));     // AO button not mapped: nothing
    }
    [Fact] public void EnterOnlyAfterIdle()
    {
        Assert.Null(Program.IdleEnter(S(idle: 100)));
        Assert.Equal("collar", Program.IdleEnter(S(idle: 301)));
        foreach (var s in new[] { S(following: true), S(sitObj: true), S(moving: true), S(forceSat: true), S(sitLocked: true), S(driving: true), S(busy: true), S(on: false) })
            Assert.Null(Program.IdleEnter(s));
        for (int i = 0; i <= 10; i++) Assert.InRange(Program.IdleThresholdSec(240, i / 10.0), 90, 300);
        Assert.Equal(90, Program.IdleThresholdSec(240, 0)); Assert.Equal(300, Program.IdleThresholdSec(240, 1));
        var xs = Enumerable.Range(0, 400).Select(_ => Program.IdleThresholdSec(240, System.Random.Shared.NextDouble())).ToList();
        Assert.True(xs.Max() - xs.Min() > 150, "real variance, not small jitter");
    }
    [Fact] public void ExitBeforeEveryMovement()
    {
        Assert.Null(Program.IdleMustExit("collar", false, false, false, false, true, false, false));   // kneeling on a visible leash is fine
        Assert.Equal("follow", Program.IdleMustExit("collar", true, false, false, false, false, false, false));
        Assert.Equal("sit", Program.IdleMustExit("collar", false, true, false, false, false, false, false));
        Assert.Equal("sit", Program.IdleMustExit("groundsit", false, false, false, true, false, false, false));
        Assert.Equal("moving", Program.IdleMustExit("collar", false, false, true, false, false, false, false));
        Assert.Null(Program.IdleMustExit("collar", false, false, false, false, true, true, false));
        Assert.Equal("walker", Program.IdleMustExit("collar", false, false, false, false, true, false, true));
        Assert.Equal("leashed", Program.IdleMustExit("groundsit", false, false, false, false, true, false, false));   // a leash ends the AO ground sit
        Assert.Null(Program.IdleMustExit(null, true, true, true, true, true, true, true));
    }
    [Fact] public void CollarMenuParsing()
    {
        Assert.Equal("discuss", Program.CollarPlaying("\nPlaying: discuss\n"));
        Assert.Null(Program.CollarPlaying("\nPlaying: (none)\n"));
        Assert.Null(Program.CollarPlaying("RLV: on"));
        var p2 = new[] { "◂", "▸", "▴", "↑", "↓", "☐ Crawl", "doormat", "kneel", "STOP", "discuss", "display", "doggy" };
        Assert.Equal("kneel", Program.CollarPoseButton(p2, "KNEEL"));
        Assert.Null(Program.CollarPoseButton(p2, "nadu"));
        Assert.Equal((1, 4, "0.3,0.7"), Program.AoGroundSitSpot("4 0.3,0.7\n"));
        Assert.Equal((2, 4, "0.88,0.50"), Program.AoGroundSitSpot("2 4 0.88,0.50\n"));
        Assert.Null(Program.AoGroundSitSpot(""));
    }
    // 2026-10-23 regression: stood up on David's word, sat right back down twice
    [Fact] public void ExplicitStandBlocksIdleSitFor30Min()
    {
        Assert.Equal(1800, Program.IdleStandCooldownSec);
        var t = new System.DateTime(2026, 10, 23, 9, 0, 0); var until = t.AddSeconds(Program.IdleStandCooldownSec);
        Assert.True(Program.IdleCooldownActive(t.AddMinutes(1), until));
        Assert.True(Program.IdleCooldownActive(t.AddMinutes(29), until));
        Assert.False(Program.IdleCooldownActive(t.AddMinutes(31), until));
        Assert.False(Program.IdleCooldownActive(t, System.DateTime.MinValue));   // never stood explicitly: no block
    }
    [Fact] public void AoForceSitStateFromPlayingAnimsNeverBlindToggle()
    {
        var ground = new[] { LibreMetaverse.Animations.STAND, LibreMetaverse.Animations.SIT_GROUND_staticRAINED };
        Assert.True(Program.AoForceSitOn(ground, false));
        Assert.True(Program.AoShouldToggleOff(Program.AoForceSitOn(ground, false)));
        Assert.False(Program.AoForceSitOn(ground, true));                                   // on a seat: not the AO
        Assert.False(Program.AoForceSitOn(new[] { LibreMetaverse.Animations.STAND }, false));  // already off: don't touch (would turn it ON)
        Assert.False(Program.AoShouldToggleOff(Program.AoForceSitOn(new LibreMetaverse.UUID[0], false)));
        Assert.False(Program.AoForceSitOn(null, false));
    }
    [Fact] public void LogoutAndStandUseExplicitStandPath()
    {
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/Program.cs"));
        var sd = src[src.IndexOf("public static async Task Shutdown(")..];
        Assert.True(sd.IndexOf("IdleExplicitStand(\"logout\")") is > 0 and var i && i < sd.IndexOf("Network.Logout()"));
        var st = src[src.IndexOf("case \"stand\":")..]; Assert.Contains("IdleExplicitStand(\"stand command\")", st[..1200]);
        var ip = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/IdlePose.cs"));
        Assert.Contains("case \"stop\": case \"stand\": return await IdleExplicitStand(", ip);
        Assert.Contains("login: \" + await AoGroundSitEnsureOff(", ip);
    }
    [Fact] public void FaceHim_HolderFirst_ThenDavid_OnlyNearby()
    {
        var me = new LibreMetaverse.Vector3(10, 10, 20);
        var holder = new LibreMetaverse.Vector3(12, 10, 20); var david = new LibreMetaverse.Vector3(10, 14, 20);
        Assert.Equal(holder, Program.IdleFaceTarget(me, holder, david));
        Assert.Equal(david, Program.IdleFaceTarget(me, null, david));
        Assert.Equal(david, Program.IdleFaceTarget(me, new LibreMetaverse.Vector3(60, 60, 20), david));   // holder too far
        Assert.Null(Program.IdleFaceTarget(me, null, new LibreMetaverse.Vector3(40, 10, 20)));           // nobody nearby
        Assert.Null(Program.IdleFaceTarget(me, LibreMetaverse.Vector3.Zero, null));                      // unknown position
    }
    [Fact] public void FaceHim_BeforeEitherPose()
    {
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/IdlePose.cs"));
        var body = src[src.IndexOf("static async Task<string> IdlePoseEnter(")..];
        int face = body.IndexOf("await IdleFaceHim();");
        Assert.True(face > 0 && face < body.IndexOf("CollarPosePlay(") && face < body.IndexOf("GroundSitEntry.Run("));
    }
    [Fact] public void AoGroundSitChooser_ParseAndPick()
    {
        var msg = "Select the Sitting on Ground animation to use:\n\n\n1. VAMOSITG11\n2. VAMOSITG12\n3. VAMOSITG13\n4. VAMOSITG21\n5. VAMOSITG22\n6. VAMOSITG23\n7. VAMOSITG24\n8. VAMOSITG25";
        var e = Program.AoSitEntries(msg);
        Assert.Equal(8, e.Count); Assert.Equal((1, "VAMOSITG11"), e[0]); Assert.Equal((8, "VAMOSITG25"), e[7]);
        Assert.Equal(new[] { (9, "VAMOSITG26"), (10, "VAMOSITG27") }, Program.AoSitEntries("Select the Sitting on Ground animation to use:\n\n\n9. VAMOSITG26\n10. VAMOSITG27").ToArray());
        for (int cur = 0; cur <= 10; cur++)
            foreach (var r in new[] { 0.0, 0.3, 0.6, 0.99, 1.0 })
            {
                int p = Program.AoSitPick(10, cur, r);
                Assert.InRange(p, 1, 10); Assert.NotEqual(cur, p);   // always a different sit
            }
        Assert.Equal(1, Program.AoSitPick(1, 1, 0.5));
    }
    [Fact] public void StandCooldown_OnlyHisStand_NotLogoutOrDeploy_NotAcrossRelog()   // 2026-10-23 regression
    {
        Assert.True(Program.IdleStandStartsCooldown("stand command"));
        Assert.True(Program.IdleStandStartsCooldown("manual stop"));
        Assert.False(Program.IdleStandStartsCooldown("logout"));
        Assert.False(Program.IdleStandStartsCooldown("shutdown"));
        Assert.False(Program.IdleCooldownActive(System.DateTime.Now, Program.IdleCooldownAfterLogin()));
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/IdlePose.cs"));
        var login = src[src.IndexOf("static void IdlePoseLoginStart()")..]; login = login[..login.IndexOf("Task.Run")];
        Assert.Contains("idleNoSitUntil = IdleCooldownAfterLogin()", login);
        var prog = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/Program.cs"));
        Assert.Contains("IdleExplicitStand(\"logout\")", prog);   // the logout stand passes "logout" (no cooldown)
    }
    [Fact] public void CollarVisibility_UnknownAfterRelog_IsHidden()   // 2026-10-23 regression: hidden collar read as visible after relog
    {
        Assert.False(Program.CollarVisibleFrom(0, null));                                   // collar prims not arrived yet
        Assert.False(Program.CollarVisibleFrom(1, new[] { (float[])null, null }));         // TextureEntry not arrived yet
        Assert.False(Program.CollarVisibleFrom(1, new[] { new[] { 0f, 0f }, new[] { 0f } })); // Stealth: every face alpha 0
        Assert.True(Program.CollarVisibleFrom(1, new[] { new[] { 0f }, new[] { 1f, 0f } }));  // a visible face
        Assert.False(Program.CollarVisibleFrom(2, new[] { new[] { 1f } }));                // ambiguous match: unknown
        Assert.Equal("groundsit", Program.IdlePoseKind(Program.CollarVisibleFrom(0, null), false));
    }
    [Fact] public void AoGroundSitSwitch_KnowsTheCurrentSit_AfterRelog()   // 2026-10-23 regression
    {
        Assert.Equal("VAMOSITG26", Program.AoSitConfirmName("Current Sitting on Ground Animation::VAMOSITG26"));
        Assert.Null(Program.AoSitConfirmName("Current Walking Animation::X"));
        var all = new System.Collections.Generic.List<(int, string)> { (1, "VAMOSITG11"), (2, "VAMOSITG12"), (9, "VAMOSITG26"), (10, "VAMOSITG27") };
        Assert.Equal(9, Program.AoSitCurrentNumber(all, "VAMOSITG26", 0));   // relog: our memory is 0, the AO's line says 9
        Assert.Equal(9, Program.AoSitCurrentNumber(all, "VAMOSITG26", 2));   // the AO wins over stale memory
        Assert.Equal(2, Program.AoSitCurrentNumber(all, null, 2));
        foreach (var r in new[] { 0.0, 0.5, 0.99 }) Assert.NotEqual(9, Program.AoSitPick(10, Program.AoSitCurrentNumber(all, "VAMOSITG26", 0), r));
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/IdlePose.cs"));
        var sw = src[src.IndexOf("static async Task<string> AoGroundSitSwitch()")..]; sw = sw[..sw.IndexOf("\n    }\n")];
        Assert.DoesNotContain("SitOnGround", sw); Assert.DoesNotContain("Stand()", sw); Assert.DoesNotContain("AoGroundSitToggle", sw);   // never re-sits
    }
    [Fact] public void AndroidDefault_NeverVTechUnlessAskedByName()   // 2026-10-23 David: "never the vtech outfit unless I ask"
    {
        Assert.Equal("Android Base", Program.OutfitGuardResolve("Android", "Android"));          // the V-Tech folder is named 'Android'
        Assert.Equal("Android Base", Program.OutfitGuardResolve("android form", null));
        Assert.Equal("Android Base", Program.OutfitGuardResolve("andr", "Android"));             // partial match landing on V-Tech
        Assert.Equal("Android", Program.OutfitGuardResolve("Android VTech", null));              // explicit V-Tech request
        Assert.Equal("Android", Program.OutfitGuardResolve("v-tech", null));
        Assert.Null(Program.OutfitGuardResolve("Android Base", "Android Base"));
        Assert.Null(Program.OutfitGuardResolve("Beach", "Beach"));
        Assert.True(Program.IsVTechOutfit("Android")); Assert.False(Program.IsVTechOutfit("Android Base"));
    }
    [Fact] public void GroundSitStart_RandomPickAndAndroidHover()   // 2026-10-23: first sit was always the last one; 2026-10-28: and the pick comes BEFORE the sit
    {
        var all = new System.Collections.Generic.List<(int, string)> { (1, "VAMOSITG11"), (8, "VAMOSITG25") };
        Assert.NotEqual(8, Program.AoSitPick(10, Program.AoSitCurrentNumber(all, "VAMOSITG25", 0), 0.99));   // last used (saved) is never the start pick
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/IdlePose.cs"));
        var enter = src[src.IndexOf("static async Task<string> IdlePoseEnter(")..]; enter = enter[..enter.IndexOf("finally { idleLock.Release();")];
        Assert.DoesNotContain("AoGroundSitSwitch", enter);   // 2026-10-28: no pick after the sit any more (it is GroundSitEntry's first step)
    }
    [Fact] public void BlockedApproach_GivesUp_DoorUnknown_StaysPut()   // 2026-10-23 14:57 corner grind after login
    {
        Assert.False(Program.LoginJoinStalled(new[] { (2.0, 8f), (0.0, 7.9f) }));            // < 3 s of data
        Assert.True(Program.LoginJoinStalled(new[] { (4.0, 6f), (3.0, 6f), (0.0, 5.9f) }));  // stuck at the wall
        Assert.False(Program.LoginJoinStalled(new[] { (4.0, 8f), (3.0, 7f), (0.0, 5f) }));   // closing in
        Assert.Equal(Program.GuardStep.Wait, Program.DoorUnknownStep(500, false));
        Assert.Equal(Program.GuardStep.GiveUp, Program.DoorUnknownStep(2600, false));        // never "walking on"
        Assert.Equal(Program.GuardStep.Touch, Program.DoorUnknownStep(100, true));
    }
    [Fact] public void PoseHover_NeverStandsOrResits()
    {
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/PoseHover.cs"));
        foreach (var bad in new[] { "Stand()", "SitOnGround", "AoGroundSitToggle", "AoGroundSitSwitch", "RequestSit", "touch" })
            Assert.DoesNotContain(bad, src.Split("using LibreMetaverse;")[1]);
    }
    [Fact] public void PoseHover_NothingSavedUntilFormKnown()   // 2026-10-23 15:19 human|stand misread after login
    {
        Assert.False(Hover.FormCertain(false, 12, 3));    // names still arriving: unknown
        Assert.False(Hover.FormCertain(false, 0, 0));     // nothing worn seen yet
        Assert.True(Hover.FormCertain(false, 12, 12));
        Assert.True(Hover.FormCertain(true, 12, 0));      // the outfit name already says android
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/PoseHover.cs"));
        var body = src[src.IndexOf("async Task<string> PoseHoverCheck(")..];
        Assert.True(body.IndexOf("FormCertainNow()") < body.IndexOf("PostHover"));
    }
    [Fact] public void UnexpectedDisconnect_InMcpConnector_RelogsInProcess()   // 2026-10-23 15:34 offline 23 min
    {
        Assert.Equal("relogin", Program.DisconnectAction(exitOnLogout: false, clientInitiated: false, shuttingDown: false));
        Assert.Equal("exit-relaunch", Program.DisconnectAction(true, false, false));
        Assert.Equal("none", Program.DisconnectAction(false, true, false));
        Assert.Equal("none", Program.DisconnectAction(false, false, true));   // our own logout/deploy: never relogin
        Assert.Equal(30, Program.EmbeddedReloginDelay(1, false).TotalSeconds);
        Assert.Equal(300, Program.EmbeddedReloginDelay(5, false).TotalSeconds);
        Assert.True(Program.EmbeddedReloginDelay(1, true).TotalMinutes >= 10);
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/Program.cs"));
        Assert.Contains("ScheduleEmbeddedRelogin(", src);
    }
    [Fact] public void Leash_StandsWhenHeWalks_ActivityResets_PoseHoverGuards()   // 2026-10-23 16:57 live test
    {
        Assert.True(Program.LeashMustStand(true, true, true, 3.2f, 2.1f, 1.5f));   // he walks off at 3.2 m/s
        Assert.True(Program.LeashMustStand(true, true, false, 1.0f, 0.7f, 1.5f));  // walking speed even before the gap grows
        Assert.False(Program.LeashMustStand(true, true, false, 0.1f, 0.7f, 1.5f)); // he shuffles in place
        Assert.False(Program.LeashMustStand(false, true, true, 3f, 3f, 1.5f));
        Assert.False(Program.LeashMustStand(true, false, true, 3f, 3f, 1.5f));
        Assert.True(Program.GroundSitStandNeeded(new[] { LibreMetaverse.Animations.SIT_GROUND_staticRAINED }, false));
        Assert.False(Program.GroundSitStandNeeded(new LibreMetaverse.UUID[0], false));
        var lw = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/LeashWalk.cs"));
        Assert.Contains("else if (o.act == \"stop\") { IdleNoteActivity();", lw);
        Assert.DoesNotContain("IdleExplicitStand", lw);                           // never the 30-min cooldown
    }
    [Fact] public void HoverCheckRender_AppliesHoverAndDrawsFloor()   // 2026-10-23 17:15 David "Fix it"
    {
        var env = Hover.HoverCheckEnv(1.566, -0.11, -0.055);
        Assert.Equal("1", env["GT_HOVER_CHECK"]); Assert.Equal("-0.110", env["GT_HOVER"]); Assert.Equal("1.566", env["GT_AGENT_SIZE"]);
        Assert.Equal(-0.003, Hover.HoverCheckGap("hover check gap -0.003 m (hover -0.110, size 1.566, floor 28.033)\n/x.jpg").Value, 3);
        Assert.Null(Hover.HoverCheckGap("look failed"));
        Assert.Equal(-0.107, Hover.PoseHoverFromGap(0.02, 0.127), 3);   // VAMOSITG27 17:20: floating 12.7 cm
        Assert.False(Hover.PoseGapSane(3.3)); Assert.True(Hover.PoseGapConfirmed(-0.003));
        var root = Root();
        var rm = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "vision/cloud-render/render_mesh.py"));
        Assert.Contains("z0 = agent_z + off + hov", rm);                          // Firestorm root + the hover, no per-pose bias (2026-10-31)
        Assert.Contains("HC = hover_check_z0(os.environ, g[2], lo_c, fl, M.get(\"agent_off\"))", rm);   // not snapped; gap vs the scene floor
        Assert.Contains("if view == \"side\":", rm);
        var lk = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "vision/look.py"));
        Assert.Contains("a[1] == \"hovercheck\": a = [a[0], \"view\"", lk);    // scene mode: the real floor geometry is meshed
    }
    [Fact] public void PoseHover_PerPoseBias_PhotoIsTruth()   // 2026-10-23 17:33 VAMOSITG22 floated ~7 cm while the render said contact
    {
        var n = new System.Collections.Generic.Dictionary<string, double> { ["bias|android|sit"] = -0.055, ["bias|android|VAMOSITG22"] = 0.137 };
        Assert.Equal(0.137, Hover.PoseBiasFor(n, true, "sit", "VAMOSITG22"), 3);     // its own bias wins
        Assert.Equal(-0.055, Hover.PoseBiasFor(n, true, "sit", "VAMOSITG27"), 3);    // else the (form, kind) bias
        Assert.Equal(0.2315, Hover.PoseBiasFor(n, true, "stand", "stand:x"), 4);     // else the default
        Assert.Equal(Hover.HumanSitBiasDefault, Hover.PoseBiasFor(new System.Collections.Generic.Dictionary<string, double>(), false, "sit", "q"), 3);   // human form: its own default (2026-10-29)
        // render said gap +0.267 at bias -0.055; the photo shows ~0.075 really: bias must rise by 0.192
        Assert.Equal(0.137, Hover.PoseBiasFromPhoto(-0.055, 0.267, 0.075), 3);
        // with the lowest body point (hand/back/thigh) the gap is already the minimum over every avatar vertex
        var py = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "vision/cloud-render/render_mesh.py"));
        Assert.Contains("z0 + lo_z - floor_z", py);
    }
    [Fact] public void CollarMenu_OneSessionAtATime_RightPageWaited()   // 2026-10-26 08:58 magic leash + pose switch crossed
    {
        Assert.True(Program.CollarLooksLikePoseMenu(new[] { "◂", "▸", "▴", "☐ Crawl", "cutie", "STOP", "beg" }));
        Assert.False(Program.CollarLooksLikePoseMenu(new[] { "Title", "Tracker", "Magic Leash", "Bell" }));
        Assert.True(Program.CollarLooksLikeMainMenu(new[] { "RLV", "Extras", "About", "Leash", "Strip", "Pose" }));
        Assert.False(Program.CollarLooksLikeMainMenu(new[] { "Title", "Tracker", "Magic Leash" }));
        Assert.Equal("Magic Leash", Program.MenuPick(new[] { "Title", "Tracker", "▴", "Little Hearts", "Magic Leash", "Sobriquet" }, "Magic"));
        var root = Root();
        var cs = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "textclient/galatay-text/CollarStealth.cs"));
        var ip = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "textclient/galatay-text/IdlePose.cs"));
        Assert.DoesNotContain("stealthLock.WaitAsync(0)", cs);                       // never skip: wait for the other session
        Assert.Contains("if (!await CollarMenuEnter()) return (\"collar menu busy for 90 s\", null);", cs);   // Stealth takes it
        Assert.Contains("await CollarMenuEnter()) return \"collar menu busy for 90 s\"", cs);                  // Magic Leash takes it
        Assert.Contains("try { return await CollarPosePlayLocked(pose); } finally { stealthLock.Release(); }", ip);  // pose switch too
        Assert.Contains("try { return await CollarPoseStopLocked(anims); } finally { stealthLock.Release(); }", ip);
        Assert.Contains("CollarLooksLikePoseMenu(d.ButtonLabels), 10000)", ip);       // 'Pose' waits for the Pose page, not any dialog
        Assert.Contains("CollarLooksLikeMainMenu(d.ButtonLabels), 10000)", cs);       // Magic Leash starts from the real main menu
    }
    // ---- 2026-10-26 09:54 leash test: groundsit -> leash chain appears -> she stood, then entered the collar kneel 'pretty' ----
    [Fact] public void LeashDuringGroundSit_ExitsAndNeverReEnters_Timeline()
    {
        // android, collar hidden (Stealth), AO ground sit, then the leash chain shows up (LeashParticleTarget != null => leashed)
        Assert.Equal("leashed", Program.IdleMustExit("groundsit", false, false, false, false, true, false, false, true));
        Assert.Null(Program.IdleSwitch("groundsit", false, true, true, true, true));       // the old code returned "collar" here
        Assert.Null(Program.IdleSwitch("groundsit", false, true, true, true, false));      // human form too: leashed => no switch/enter
        // after the stand, still leashed: nothing enters, even after the idle timer (leashHold) and in either form
        Assert.Null(Program.IdleEnter(S(collar: false, chain: true, leashed: true, android: true, leashHold: true)));
        Assert.Null(Program.IdleEnter(S(collar: true, chain: true, leashed: true, leashHold: true)));
        // leash released: back to the normal idle timer (an android sits on the ground, never kneels)
        Assert.Null(Program.IdleEnter(S(idle: 10, collar: false, android: true)));
        Assert.Equal("groundsit", Program.IdleEnter(S(idle: 400, collar: false, android: true)));
    }
    [Fact] public void AndroidNeverUsesCollarKneel_LeashChainVisibleCollarHidden()
    {
        Assert.Equal("groundsit", Program.IdlePoseKind(false, true, true));     // chain visible, collar hidden, android
        Assert.Equal("groundsit", Program.IdlePoseKind(true, true, true));
        Assert.Equal("collar", Program.IdlePoseKind(false, true, false));       // human unchanged
        Assert.Equal("groundsit", Program.IdleEnter(S(collar: false, chain: true, android: true)));
        Assert.Equal("groundsit", Program.IdleEnter(S(collar: true, chain: true, android: true)));
        Assert.Null(Program.IdleEnter(S(collar: false, chain: true, android: true, mapped: false)));   // never a kneel as the fallback
        Assert.Null(Program.IdleEnter(S(collar: false, chain: true, leashed: true, android: true)));    // leashed: stand only
        // a leftover collar kneel in android form: switched to the ground sit, or (leashed) just exited
        Assert.Equal("groundsit", Program.IdleSwitch("collar", true, true, false, true, true));
        Assert.Null(Program.IdleSwitch("collar", true, true, true, true, true));
        Assert.Equal("leashed", Program.IdleMustExit("collar", false, false, false, false, true, false, false, true));
        Assert.Null(Program.IdleMustExit("collar", false, false, false, false, true, false, false, false));   // human kneel on a leash is still fine
        // every entry path (tick, `idlepose now collar`, rotation) goes through the same guards
        Assert.Equal("groundsit", Program.IdleEnterKindFor("collar", true, false, false));
        Assert.Null(Program.IdleEnterKindFor("collar", true, true, false));
        Assert.Null(Program.IdleEnterKindFor("groundsit", false, true, false));
        Assert.Equal("collar", Program.IdleEnterKindFor("collar", false, true, false));
        Assert.Null(Program.IdleEnterKindFor("collar", false, true, true));
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/IdlePose.cs"));
        var enter = src[src.IndexOf("static async Task<string> IdlePoseEnter(")..]; enter = enter[..enter.IndexOf("finally { idleLock.Release(); }")];
        Assert.Contains("IdleEnterKindFor(kind, IdleAndroidNow()", enter);
        Assert.True(enter.IndexOf("IdleEnterKindFor(") < enter.IndexOf("CollarPosePlay("));
        var rot = src[src.IndexOf("static async Task<string> IdlePoseRotate(")..]; rot = rot[..rot.IndexOf("CollarPosePlay(")];
        Assert.Contains("IdleAndroidNow()", rot);
        Assert.Contains("IdlePoseKind(CollarVisibleNow(), LeashParticleTarget() != null, IdleAndroidNow())", src);
    }
    // 2026-10-26 16:10 (David): after a relog the AO's sit-name hook was gone (a static flag, but a NEW GridClient per login): the sit name stayed stale and no hover check followed a ground-sit switch
    [Fact] public void AoSitHookIsPerClientNotOncePerProcess()
    {
        var first = new object(); var relogged = new object();
        Assert.True(Program.AoSitHookNeeded(null, first));
        Assert.False(Program.AoSitHookNeeded(first, first));
        Assert.True(Program.AoSitHookNeeded(first, relogged));
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/IdlePose.cs"));
        Assert.DoesNotContain("aoSitHooked,", src);
        Assert.Contains("AoSitHookNeeded(aoSitHookedClient, client)", src);
        Assert.Contains("HoverCheckRequest(\"sit\")", src);   // the AO's own confirmation line asks for the visual check
    }
    // 2026-10-26 (David: "disable the automatic switch when you're doing tests")
    [Fact] public void IdleRotationCanBePausedForTests()
    {
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/IdlePose.cs"));
        Assert.Contains("!idleRotatePaused && IdleShouldRotate(", src);
        Assert.Contains("case \"rotate\" when a.Length == 2", src);
    }
    // 2026-10-26 (David): a pose that needs another hover: pose FIRST, hover checked/fixed afterwards, never before or during the entry
    [Fact] public void PoseEntryNeverTouchesHoverBeforeThePose()
    {
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/IdlePose.cs"));
        var enter = src[src.IndexOf("static async Task<string> IdlePoseEnter(")..]; enter = enter[..enter.IndexOf("finally { idleLock.Release();")];
        Assert.DoesNotContain("PostHover(", enter); Assert.DoesNotContain("hover set", enter);
        Assert.True(enter.IndexOf("CollarPosePlay(") < enter.IndexOf("AutoHoverKick(\"kneel\")"));
        Assert.True(enter.IndexOf("GroundSitEntry.Run(") < enter.IndexOf("AutoHoverKick(\"ground sit\")"));   // sit (pose chosen first, ONE sit, ONE press) before any hover work
        var exit = src[src.IndexOf("static async Task<string> IdlePoseExit(")..]; exit = exit[..exit.IndexOf("finally { idleLock.Release(); }")];
        Assert.True(exit.IndexOf("client.Self.Stand()") < exit.IndexOf("PostHover("));   // stand first, hover afterwards
        // the hover is checked/fixed by the requested visual check, a few seconds AFTER the pose started
        var ph = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/PoseHover.cs"));
        Assert.Contains("int delayMs = 4000", ph);
        Assert.True(ph.IndexOf("await Task.Delay(delayMs)") < ph.IndexOf("await PoseHoverCheck(kind, pose, why)"));
        // the collar's own @adjustheight (sent while the pose starts) is not applied
        var rlv = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/RlvAdjustHeight.cs"));
        Assert.True(rlv.IndexOf("RlvAdjustHeightApplies(") < rlv.IndexOf("PostHover(v"));
    }
    [Fact] public void CollarAdjustHeightIsIgnoredByDefault_KeepsPinAndAutoHoverIntact()
    {
        Assert.False(Program.RlvAdjustHeightApplies(null)); Assert.False(Program.RlvAdjustHeightApplies("")); Assert.False(Program.RlvAdjustHeightApplies("off"));
        Assert.True(Program.RlvAdjustHeightApplies("apply"));
        var rlv = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/RlvAdjustHeight.cs"));
        var ign = rlv[rlv.IndexOf("if (!RlvAdjustHeightApplies(")..]; ign = ign[..ign.IndexOf("return $\"adjustheight")];
        Assert.DoesNotContain("rlvHoverOverride", ign); Assert.DoesNotContain("hoverManualAt", ign); Assert.DoesNotContain("PostHover", ign);
        // so the pin stays the file value and the 10-min "manual hover wins" window never opens
        Assert.Equal(0.0, Program.PinnedHoverFrom(null, null, "0"), 3);
        Assert.Null(Hover.AutoHoverDecide("groundsit", false, null, 0.0, 0.0, 1e9));   // no per-pose offset any more
        Assert.Equal(0.105, Hover.AutoHoverDecide("stand", false, -0.045, 0.06, 0.0, 1e9)!.Value, 3);   // sinking into the floor: the problem-fix path still works
    }
    static string Root() { var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
    [Fact] public void IdleSit_AfterLogin_WaitsTwiceTheIdleTime()   // 2026-10-31 18:07 regression: auto-sat 106 s after a slow login's veil-off
    {
        var login = new System.DateTime(2026, 10, 31, 18, 5, 24); var veil = login.AddSeconds(17);
        Assert.True(Program.IdleLoginHoldActive(login.AddSeconds(600), login, System.DateTime.MaxValue, 94));   // veil still on
        Assert.True(Program.IdleLoginHoldActive(veil.AddSeconds(106), login, veil, 94));                          // the live case: blocked
        Assert.True(Program.IdleLoginHoldActive(veil.AddSeconds(187), login, veil, 94));
        Assert.False(Program.IdleLoginHoldActive(veil.AddSeconds(189), login, veil, 94));
        Assert.True(Program.IdleLoginHoldActive(login.AddSeconds(500), login, System.DateTime.MinValue, 300));     // no veil: from login
        Assert.False(Program.IdleLoginHoldActive(login.AddSeconds(601), login, System.DateTime.MinValue, 300));
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/IdlePose.cs"));
        Assert.Contains("IdleLoginHoldActive(DateTime.Now, idleLoginAt, loginVeilOffAt, idleThreshold)) return;", src);
    }
}
