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
}
