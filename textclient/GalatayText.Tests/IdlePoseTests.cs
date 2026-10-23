using System;
using System.Linq;
using GalatayText;
using Xunit;
public class IdlePoseTests
{
    static Program.IdleIn S(double idle = 400, bool collar = true, bool chain = false, bool leashed = false, bool mapped = true,
        bool following = false, bool sitObj = false, bool moving = false, bool forceSat = false, bool sitLocked = false, bool driving = false, bool busy = false, bool on = true, bool dancing = false)
        => new(on, leashed, driving, following, sitObj, moving, forceSat, sitLocked, busy, dancing, idle, 300, collar, chain, mapped);
    [Fact] public void RotationRandom3to10MinCollarOnlyNotWhileDancing()
    {
        Assert.Equal(180, Program.IdleRotateSec(0)); Assert.Equal(600, Program.IdleRotateSec(1));
        Assert.True(Program.IdleShouldRotate("collar", 400, 300, false));
        Assert.False(Program.IdleShouldRotate("collar", 200, 300, false));
        Assert.False(Program.IdleShouldRotate("collar", 400, 300, true));
        Assert.False(Program.IdleShouldRotate("groundsit", 900, 300, false));
        for (int i = 0; i < 50; i++) Assert.NotEqual("nadu", Program.PickIdlePose("nadu", System.Random.Shared.NextDouble()));
    }
    [Fact] public void UnleashWhileKneelingWithCollarVisibleStaysKneeling()
    {
        Assert.Null(Program.IdleMustExit("collar", false, false, false, false, false, false, false));   // just unleashed
        Assert.Null(Program.IdleMustExit("collar", false, false, false, false, true, false, false));    // just leashed
        Assert.Null(Program.IdleSwitch("collar", true, false, false, true));
        Assert.Equal("groundsit", Program.IdleSwitch("collar", false, false, false, true));  // collar hidden, no leash
        Assert.Null(Program.IdleSwitch("collar", false, false, true, true));                 // invisible leash: keep kneeling
        Assert.Equal("collar", Program.IdleSwitch("groundsit", false, true, true, true));    // chain appears
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
        Assert.Null(Program.IdleMustExit("groundsit", false, false, false, false, true, false, false));
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
        Assert.True(face > 0 && face < body.IndexOf("CollarPosePlay(") && face < body.IndexOf("AoGroundSitToggle("));
    }
    static string Root() { var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
}
