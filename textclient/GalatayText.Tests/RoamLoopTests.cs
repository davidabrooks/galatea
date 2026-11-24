using GalatayText;
using LibreMetaverse;
using Xunit;
using Book = GalatayText.Program.RoamLegBook;
using Stop = GalatayText.Program.RoamStop;
using Seated = GalatayText.Program.RoamSeated;

// 2026-11-23: the roam main loop (Roam.cs RoamLoop, RoamLife.cs sit detour). Roaming has not been watched in-world since
// PR 489, so these pin only invariants that should survive tuning. Tuning values (pause and sit durations, sit odds,
// variety weights, radii, margins, tolerances, retry counts) are read from the Program constants, never written here.
public class RoamLoopTests
{
    // ---- stop conditions (checked at the top of every pass) ------------------------------------------------------------
    static Stop S(bool loggedIn = true, bool sitting = false, bool ownSit = false, bool follow = false, bool leash = false,
                  bool region = true, bool inArea = true) => Program.RoamStopCheck(loggedIn, sitting, ownSit, follow, leash, region, inArea);

    [Fact]
    public void Every_outside_stop_ends_the_loop_and_nothing_else_does()
    {
        Assert.Equal(Stop.None, S());
        Assert.Equal(Stop.LoggedOut, S(loggedIn: false));
        Assert.Equal(Stop.SatDown, S(sitting: true));          // someone or something sat her
        Assert.Equal(Stop.Follow, S(follow: true));            // follow / "come here" style movers
        Assert.Equal(Stop.Leashed, S(leash: true));
        Assert.Equal(Stop.NoRegion, S(region: false));
        Assert.Equal(Stop.LeftArea, S(inArea: false));         // a teleport (or any unexpected region) leaves the roam area
        Assert.Equal(Stop.None, S(sitting: true, ownSit: true)); // roam's own sit detour is not a stop
        // a logout wins over everything else
        Assert.Equal(Stop.LoggedOut, S(loggedIn: false, sitting: true, follow: true, leash: true, region: false, inArea: false));
    }

    // ---- refusing to start ---------------------------------------------------------------------------------------------
    [Fact]
    public void Roam_does_not_start_without_the_AO_and_reads_it_only_when_needed()
    {
        int aoReads = 0, statusReads = 0;
        string Refuse(bool loggedIn = true, bool on = false, bool wander = false, bool home = false, bool sit = false, bool leash = false, bool ao = true) =>
            Program.RoamStartRefusal(loggedIn, on, () => { statusReads++; return "roam on; ..."; }, wander, home, sit, leash,
                () => { aoReads++; return (ao, "no AO HUD worn"); });

        Assert.Null(Refuse());
        var no = Refuse(ao: false);
        Assert.NotNull(no);
        Assert.Contains("AO not active", no);
        Assert.Contains("no AO HUD worn", no);   // says why
        aoReads = 0;
        foreach (var r in new[] { Refuse(loggedIn: false, ao: false), Refuse(wander: true, ao: false), Refuse(home: true, ao: false),
                                  Refuse(sit: true, ao: false), Refuse(leash: true, ao: false) })
            Assert.NotNull(r);
        Assert.Equal(0, aoReads);                // an earlier refusal never consults the AO
        Assert.Equal(0, statusReads);
        Assert.StartsWith("roam already running", Refuse(on: true));
        Assert.Equal(1, statusReads);
    }

    // ---- re-planning: eject, failed legs, give up ----------------------------------------------------------------------
    static readonly DateTime T0 = new(2026, 11, 23, 17, 20, 0);

    [Fact]
    public void An_eject_re_plans_once_and_does_not_loop_on_the_same_eject()
    {
        var b = new Book(T0);
        Assert.False(b.ReplanDue(T0));             // the eject seen before roam started is not new
        var eject = T0.AddSeconds(41);             // 17:20:41 Peacock Park: "You have been ejected from this land."
        Assert.True(b.ReplanDue(eject));
        Assert.True(b.Ejected(eject));
        b.EjectHandled(eject);
        Assert.False(b.ReplanLimitHit());
        b.Replanned();
        Assert.False(b.ReplanDue(eject));          // the same eject never triggers again (no walk-back / re-plan loop)
        Assert.False(b.Ejected(eject));
        var second = eject.AddMinutes(3);
        Assert.True(b.ReplanDue(second) && b.Ejected(second));   // a later eject does
    }

    [Fact]
    public void Failed_legs_in_a_row_re_plan_and_a_good_leg_resets_the_count()
    {
        var b = new Book(T0);
        for (int i = 1; i < Book.ReplanAfterFails; i++) { b.LegFailed(); Assert.False(b.ReplanDue(T0)); }
        b.LegFailed();
        Assert.True(b.ReplanDue(T0));
        Assert.False(b.Ejected(T0));               // re-plan reason: failed legs, not an eject
        Assert.False(b.ReplanLimitHit());
        b.Replanned();
        Assert.Equal(0, b.Fails);
        Assert.False(b.ReplanDue(T0));

        b.LegFailed(); b.LegOk();
        Assert.Equal(0, b.Fails); Assert.Equal(0, b.Replans);
    }

    [Fact]
    public void Re_planning_without_a_good_leg_gives_up_after_the_limit_and_a_good_leg_restores_it()
    {
        var b = new Book(T0);
        for (int i = 0; i < Book.MaxReplans; i++) { Assert.False(b.ReplanLimitHit()); b.Replanned(); }
        Assert.True(b.ReplanLimitHit());           // the next re-plan stops the roam instead
        var c = new Book(T0);
        for (int i = 0; i < Book.MaxReplans; i++) c.ReplanLimitHit();
        c.LegOk();
        Assert.False(c.ReplanLimitHit());
    }

    // the loop's control flow (Roam.cs RoamLoop) with every leg failing / every pass ejected: it must always end
    static (int legs, string why) Drive(Func<int, DateTime> ejectAt, int maxPasses = 10_000)
    {
        var b = new Book(T0); int legs = 0;
        for (int pass = 0; pass < maxPasses; pass++)
        {
            var e = ejectAt(pass);
            if (b.ReplanDue(e))
            {
                if (b.Ejected(e)) b.EjectHandled(e);
                if (b.ReplanLimitHit()) return (legs, "replans");
                b.Replanned();
            }
            legs++; b.LegFailed();
            if (b.TooManyFails) return (legs, "fails");
        }
        return (legs, "never");
    }

    [Fact]
    public void The_loop_terminates_when_every_leg_fails_or_every_pass_is_ejected()
    {
        var allFail = Drive(_ => T0);
        Assert.NotEqual("never", allFail.why);
        Assert.True(allFail.legs <= (Book.MaxReplans + 1) * Book.ReplanAfterFails + Book.MaxFails, $"{allFail.legs} legs");

        var ejectStorm = Drive(p => T0.AddSeconds(p + 1));
        Assert.Equal("replans", ejectStorm.why);
        Assert.Equal(Book.MaxReplans, ejectStorm.legs);   // one leg per eject, then the re-plan limit stops it
    }

    // ---- the seat stop -------------------------------------------------------------------------------------------------
    [Fact]
    public void A_roam_sit_never_keeps_an_adult_pose_and_ends_on_logout_or_an_outside_stand()
    {
        double after = Program.RoamSitAdultGraceS + 1, during = Program.RoamSitAdultGraceS * 0.5;
        Assert.Equal(Seated.AdultPose, Program.RoamSeatedCheck(true, true, after, () => true));
        Assert.Equal(Seated.Stay, Program.RoamSeatedCheck(true, true, after, () => false));
        bool asked = false;
        Assert.Equal(Seated.Stay, Program.RoamSeatedCheck(true, true, during, () => asked = true));
        Assert.False(asked);                                   // the seat's own animation settles first
        Assert.Equal(Seated.StoodUp, Program.RoamSeatedCheck(true, false, after, () => true));
        Assert.Equal(Seated.LoggedOut, Program.RoamSeatedCheck(false, false, after, () => true));
    }

    [Fact]
    public async Task Standing_up_waits_for_a_seat_adjust_to_finish_but_not_forever()
    {
        int busyFor = 3, ticks = 0;
        int waited = await Program.RoamWaitWhile(() => ticks < busyFor, () => { ticks++; return Task.CompletedTask; }, Program.RoamSeatAdjustWaitMaxTicks);
        Assert.Equal(busyFor, waited);                       // stood only once the adjust was done
        Assert.Equal(0, await Program.RoamWaitWhile(() => false, () => Task.CompletedTask, Program.RoamSeatAdjustWaitMaxTicks));
        int stuck = await Program.RoamWaitWhile(() => true, () => Task.CompletedTask, Program.RoamSeatAdjustWaitMaxTicks);
        Assert.Equal(Program.RoamSeatAdjustWaitMaxTicks, stuck);   // a stuck adjust cannot hold her seated forever
    }

    // ---- picking spots -------------------------------------------------------------------------------------------------
    static ulong H(uint gx, uint gy) => SimGeo.Handle(gx * 256, gy * 256);
    static readonly ulong Ahern = H(997, 1002);
    static Program.RoamRegionView Flat(ulong h, IReadOnlyList<Program.RoamObstacle> obstacles = null) =>
        new(h, 256, 256, 20f, (_, _) => 39.4f, Array.Empty<Program.RoamPrim>(), (_, _) => true, obstacles);

    [Fact]
    public void Edge_margin_is_measured_at_the_candidate_not_where_she_stands()
    {
        // she stands inside the void-edge margin (west and south are void): that must not block planning,
        // and no spot may be inside the margin itself
        float m = Program.RoamEdgeMarginM;
        var start = Program.RoamToGlobal(Ahern, m * 0.5f, m * 0.5f);
        var c = Program.RoamSelectTargets(start, Program.RoamDefaultRadiusM, 40.4f,
            new Dictionary<ulong, Program.RoamRegionView> { [Ahern] = Flat(Ahern) }, null, null, new(), keep: 1000);
        Assert.NotEmpty(c);
        Assert.All(c, k => { var l = Program.RoamFromGlobal(Ahern, k.X, k.Y); Assert.True(l.X >= m && l.Y >= m, $"spot {l} inside the {m} m margin"); });
        // the same position as a candidate is refused
        Assert.True(Program.RoamTooCloseToVoidEdge(m * 0.5f, 128f, 256, 256, m, false, true, true, true));
        Assert.False(Program.RoamTooCloseToVoidEdge(m * 1.5f, 128f, 256, 256, m, false, true, true, true));
    }

    [Fact]
    public void Neighbor_regions_count_as_Linden_land_only_for_the_real_Governor_Linden_uuid()
    {
        var other = new UUID("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Assert.True(Program.RoamIsLindenOwner(new UUID("3d6181b0-6a4b-97ef-18d8-722652995cf1")));
        Assert.True(Program.RoamNeighborAllowed(other, Program.RoamGovernorLinden, connected: true, adjacent: true));
        // the wrong id used before 2026-11-23 is just another resident
        var wrong = new UUID("3d6181b0-6a4b-97ef-18d8-7227138161bb");
        Assert.False(Program.RoamIsLindenOwner(wrong));
        Assert.False(Program.RoamNeighborAllowed(other, wrong, connected: true, adjacent: true));
        Assert.False(Program.RoamIsLindenOwner(UUID.Zero));
    }

    [Fact]
    public void Obstacle_sampling_refuses_a_blocked_straight_walk()
    {
        float bh = Program.RoamBodyHeightM;
        // a wall at x 140..140.2, standing from the ground to well above her head
        var wall = new Program.RoamObstacle(140f, 100f, 140.2f, 160f, 39.4f, 39.4f + bh * 2);
        var regs = new Dictionary<ulong, Program.RoamRegionView> { [Ahern] = Flat(Ahern, new[] { wall }) };
        var a = Program.RoamToGlobal(Ahern, 128f, 128f);
        Assert.True(Program.RoamWalkHitsObstacle(regs, a, Program.RoamToGlobal(Ahern, 150f, 128f), 40.4f));    // through it
        Assert.False(Program.RoamWalkHitsObstacle(regs, a, Program.RoamToGlobal(Ahern, 128f, 150f), 40.4f));   // beside it
        Assert.False(Program.RoamWalkHitsObstacle(regs, a, Program.RoamToGlobal(Ahern, 150f, 200f), 40.4f));   // past its end (crosses x 140 at y 167)
        // and the planner never offers a spot behind it
        var c = Program.RoamSelectTargets(a, Program.RoamDefaultRadiusM, 40.4f, regs, null, null, new(), keep: 1000);
        Assert.DoesNotContain(c, k => { var l = Program.RoamFromGlobal(Ahern, k.X, k.Y); return l.X > 140.2f && l.Y > 100f + 1 && l.Y < 160f - 1 && Math.Abs(l.Y - 128f) < 10f; });
    }

    [Fact]
    public void Arriving_within_the_leg_tolerance_ends_the_leg()
    {
        float tol = Program.RoamArriveTolM;
        Assert.True(Program.WalkCloseEnough(tol * 0.5f, 0f, tol));
        Assert.True(Program.WalkCloseEnough(tol, 0f, tol));
        Assert.True(Program.WalkCloseEnough(tol * 0.9f, 5f, tol));   // height difference never keeps a leg open
        Assert.False(Program.WalkCloseEnough(tol * 1.1f, 0f, tol));
    }
}

// StopRoamIfOn is what teleport, leash, "come here", stop, logout and a new mover call. It changes the static roam task,
// so it runs alone.
[Collection(ProcessStateCollection.Name)]
public class RoamStopTests
{
    [Fact]
    public async Task StopRoamIfOn_cancels_the_running_roam_and_it_ends()
    {
        var oldCts = Program.roamCts; var oldTask = Program.roamTask;
        try
        {
            var cts = new CancellationTokenSource();
            var token = cts.Token;
            Program.roamCts = cts;
            Program.roamTask = Task.Run(async () => { while (!token.IsCancellationRequested) await Task.Delay(Timeout.Infinite, token); }, CancellationToken.None);
            Assert.True(Program.RoamOn);
            foreach (var why in new[] { "teleport", "leashed", "David asked me to come", "stop command", "logout/shutdown" })
                Program.StopRoamIfOn(why);   // idempotent across every caller
            Assert.True(token.IsCancellationRequested);
            await Task.WhenAny(Program.roamTask, Task.Delay(5000));
            Assert.True(Program.roamTask.IsCompleted);
            Assert.False(Program.RoamOn);

            var idle = new CancellationTokenSource();
            Program.roamCts = idle;              // not running: nothing to cancel
            Program.StopRoamIfOn("command");
            Assert.False(idle.IsCancellationRequested);
        }
        finally { Program.roamCts = oldCts; Program.roamTask = oldTask; }
    }
}
