using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-11-23 David, first live roam in Ahern: a corner start found no spots (17:09), neighbors were never used,
// and the stops should look alive (look at things, sometimes sit).
public class RoamCornerAndLifeTests
{
    static ulong H(uint gx, uint gy) => SimGeo.Handle(gx * 256, gy * 256);
    static readonly ulong Ahern = H(997, 1002), Dore = H(996, 1002), Morris = H(997, 1001), Bonifacio = H(996, 1001);

    static Program.RoamRegionView Flat(ulong h, float ground = 39.4f, float water = 20f, Func<float, float, bool> parcel = null,
        IReadOnlyList<Program.RoamPrim> prims = null, Func<float, float, float?> terrain = null) =>
        new(h, 256, 256, water, terrain ?? ((_, _) => ground), prims ?? Array.Empty<Program.RoamPrim>(), parcel ?? ((_, _) => true));

    static Dictionary<ulong, Program.RoamRegionView> Regions(params Program.RoamRegionView[] r) => r.ToDictionary(x => x.Handle);

    [Fact]
    public void Live_17_09_corner_start_in_Ahern_alone_now_finds_in_region_spots()
    {
        // the real inputs: <7.0,8.0,40.4> in Ahern (997,1002), radius 40, Ahern only (no neighbor accepted)
        var start = Program.RoamToGlobal(Ahern, 7f, 8f);
        Assert.Equal(255239.0, start.x, 3); Assert.Equal(256520.0, start.y, 3);
        var rej = new Program.RoamRejectCounts();
        var c = Program.RoamSelectTargets(start, 40f, 40.4f, Regions(Flat(Ahern)), null, null, rej, keep: 100);
        Assert.NotEmpty(c);
        foreach (var k in c)
        {
            var l = Program.RoamFromGlobal(Ahern, k.X, k.Y);
            Assert.True(l.X >= 8f && l.Y >= 8f, $"spot {l} must keep 8 m from the void west/south edges");
            Assert.Equal(Ahern, k.Handle);
        }
        Assert.Equal(0, rej.Get(Program.RoamRejPathEdge));   // the old bug: every path refused at its own start
        Assert.True(rej.Get(Program.RoamRejOutside) + rej.Get(Program.RoamRejEdge) > 0);
        Assert.Contains("edge_margin=", rej.ToString());
        Assert.Equal(rej.Considered, rej.Total + c.Count);
    }

    [Fact]
    public void Corner_start_with_mainland_neighbors_uses_them()
    {
        var start = Program.RoamToGlobal(Ahern, 7f, 8f);
        var rej = new Program.RoamRejectCounts();
        var c = Program.RoamSelectTargets(start, 40f, 40.4f, Regions(Flat(Ahern), Flat(Dore), Flat(Morris), Flat(Bonifacio)), null, null, rej, keep: 500);
        Assert.Contains(c, k => k.Handle == Dore);
        Assert.Contains(c, k => k.Handle == Morris);
        Assert.Contains(c, k => k.Handle == Bonifacio);
        Assert.Equal(0, rej.Get(Program.RoamRejOutside));
    }

    [Fact]
    public void Path_may_leave_the_margin_but_not_go_deeper_toward_a_void_edge()
    {
        var none = (0f, 0f, 0f, 0f);
        var start = Program.RoamVoidEdgeDepth(7f, 8f, 256, 256, 8f, false, true, false, true);
        Assert.Equal(1f, start.w, 3); Assert.Equal(0f, start.s, 3);
        Assert.False(Program.RoamPathDeeperIntoEdge(Program.RoamVoidEdgeDepth(9f, 12f, 256, 256, 8f, false, true, false, true), start));
        Assert.False(Program.RoamPathDeeperIntoEdge(start, start));
        Assert.True(Program.RoamPathDeeperIntoEdge(Program.RoamVoidEdgeDepth(4f, 12f, 256, 256, 8f, false, true, false, true), start));
        Assert.True(Program.RoamPathDeeperIntoEdge(Program.RoamVoidEdgeDepth(9f, 6f, 256, 256, 8f, false, true, false, true), start));
        Assert.True(Program.RoamPathDeeperIntoEdge(Program.RoamVoidEdgeDepth(3f, 100f, 256, 256, 8f, false, true, true, true), none));
    }

    [Fact]
    public void Rejections_are_counted_per_reason()
    {
        var start = Program.RoamToGlobal(Ahern, 128f, 128f);
        // water west of x=110, a restricted parcel north of y=150, a 2 m wall at x=140..141
        var wall = RoamFixtures.PrimBox(140.5f, 128f, 1f, 200f, 41.4f);
        var r = Flat(Ahern, terrain: (x, _) => x < 110 ? 10f : 39.4f, parcel: (_, y) => y < 150, prims: new[] { wall });
        var rej = new Program.RoamRejectCounts();
        var c = Program.RoamSelectTargets(start, 40f, 40.4f, Regions(r), null, null, rej, keep: 500);
        Assert.True(rej.Get(Program.RoamRejWater) > 0);
        Assert.True(rej.Get(Program.RoamRejParcel) > 0);
        Assert.True(rej.Get(Program.RoamRejPathWall) + rej.Get(Program.RoamRejPathLedge) > 0);
        Assert.NotEmpty(c);
        Assert.All(c, k => { var l = Program.RoamFromGlobal(Ahern, k.X, k.Y); Assert.True(l.X >= 110 && l.X < 140 && l.Y < 150); });
    }

    [Fact]
    public void A_thin_wall_or_post_on_the_way_blocks_the_straight_walk()
    {
        var start = Program.RoamToGlobal(Ahern, 128f, 128f);
        // a 0.2 m thick, 3 m high wall east of her (not a floor): nothing straight through it
        var wall = new Program.RoamObstacle(140f, 60f, 140.2f, 200f, 39.4f, 42.4f);
        var r = Flat(Ahern) with { Obstacles = new[] { wall } };
        var rej = new Program.RoamRejectCounts();
        var c = Program.RoamSelectTargets(start, 40f, 40.4f, Regions(r), null, null, rej, keep: 500);
        Assert.True(rej.Get(Program.RoamRejPathObstacle) > 0);
        Assert.All(c, k => Assert.True(Program.RoamFromGlobal(Ahern, k.X, k.Y).X < 140f));
        // a low kerb (0.3 m) is a step, a sign board high above the head is not in the way
        Assert.False(Program.RoamObstacleBlocks(new Program.RoamObstacle(0, 0, 1, 1, 39.4f, 39.7f), 39.4f));
        Assert.False(Program.RoamObstacleBlocks(new Program.RoamObstacle(0, 0, 1, 1, 41.5f, 43f), 39.4f));
        Assert.True(Program.RoamObstacleBlocks(new Program.RoamObstacle(0, 0, 1, 1, 39f, 40.5f), 39.4f));
        // segment / box entry
        var o = new Program.RoamObstacle(4, -1, 6, 1, 0, 2);
        Assert.Equal(0.37f, Program.RoamSegmentEntersBox(new Vector2(0, 0), new Vector2(10, 0), o).Value, 2);
        Assert.Null(Program.RoamSegmentEntersBox(new Vector2(0, 3), new Vector2(10, 3), o));
        Assert.Equal(0f, Program.RoamSegmentEntersBox(new Vector2(5, 0), new Vector2(10, 0), o).Value, 3);   // starts inside
        var rot = Program.RoamPrimBounds(new Vector3(0, 0, 1), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), new Vector3(4, 0.2f, 2));
        Assert.Equal(-0.1f, rot.X0, 3); Assert.Equal(-2f, rot.Y0, 3); Assert.Equal(2f, rot.Top, 3);
    }

    [Fact]
    public void A_busy_welcome_area_scan_stays_fast()
    {
        var rnd = new Random(9);
        var floors = Enumerable.Range(0, 4000).Select(_ => RoamFixtures.PrimBox(rnd.Next(0, 256), rnd.Next(0, 256), 2, 2, 39.5f + (float)rnd.NextDouble() * 0.3f)).ToList();
        var obs = Enumerable.Range(0, 4000).Select(_ => { float x = rnd.Next(0, 256), y = rnd.Next(0, 256); return new Program.RoamObstacle(x, y, x + 0.3f, y + 0.3f, 39.4f, 41f); }).ToList();
        var regs = Regions(Flat(Ahern, prims: floors) with { Obstacles = obs }, Flat(Dore, prims: floors) with { Obstacles = obs });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Program.RoamSelectTargets(Program.RoamToGlobal(Ahern, 7f, 8f), 40f, 40.4f, regs, null, null, new(), keep: 80);
        Assert.True(sw.Elapsed.TotalSeconds < 5, $"scan took {sw.Elapsed.TotalSeconds:F1} s");
    }

    [Fact]
    public void Points_of_interest_raise_nearby_stops()
    {
        var start = Program.RoamToGlobal(Ahern, 128f, 128f);
        var poi = Program.RoamToGlobal(Ahern, 150f, 128f);
        var c = Program.RoamSelectTargets(start, 40f, 40.4f, Regions(Flat(Ahern)), null, null, new(), keep: 300, pois: new[] { poi });
        Assert.EndsWith(" poi", c[0].Why);
        var withPoi = c.Where(k => k.Why.EndsWith(" poi")).ToList();
        Assert.NotEmpty(withPoi);
        Assert.True(withPoi.Min(k => k.Score) > c.Where(k => !k.Why.EndsWith(" poi")).Max(k => k.Score));
    }

    [Fact]
    public void Governor_Linden_uuid_and_mainland_neighbors()
    {
        Assert.Equal(new UUID("3d6181b0-6a4b-97ef-18d8-722652995cf1"), Program.RoamGovernorLinden);   // the Ahern parcel owner in the log
        Assert.True(Program.RoamIsMainland("Mainland / Full Region"));
        Assert.False(Program.RoamIsMainland("Estate / Full Region"));
        Assert.False(Program.RoamIsMainland(""));
        // owners unknown (zero) on both: mainland still counts as public land
        Assert.True(Program.RoamNeighborAllowed(UUID.Zero, UUID.Zero, true, true, startMainland: true, neighborMainland: true));
        Assert.False(Program.RoamNeighborAllowed(UUID.Zero, UUID.Zero, true, true));
        Assert.False(Program.RoamNeighborAllowed(UUID.Zero, UUID.Zero, false, true, true, true));
        Assert.True(Program.RoamNeighborAllowed(Program.RoamGovernorLinden, UUID.Zero, true, true));
    }

    [Fact]
    public void Linkset_floor_boxes_rotated_and_tilted()
    {
        var b = Program.RoamPrimWorldBox(new Vector3(10, 10, 39.9f), Quaternion.Identity, new Vector3(20, 10, 0.2f));
        Assert.NotNull(b); Assert.Equal(40f, b.Value.Top, 3); Assert.Equal(0f, b.Value.X0, 3); Assert.Equal(5f, b.Value.Y0, 3);
        // 90 degrees about Z: footprint swaps
        var z90 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var r = Program.RoamPrimWorldBox(new Vector3(10, 10, 39.9f), z90, new Vector3(20, 10, 0.2f));
        Assert.Equal(5f, r.Value.X0, 2); Assert.Equal(0f, r.Value.Y0, 2);
        // a plank stood on edge (90 degrees about X): its up face is 0.2 m wide -> a wall, not a floor
        var x90 = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2);
        Assert.Null(Program.RoamPrimWorldBox(new Vector3(10, 10, 40), x90, new Vector3(4, 3, 0.2f)));
        // a box lying on its side (90 about X) with a wide up face is a floor
        Assert.NotNull(Program.RoamPrimWorldBox(new Vector3(10, 10, 40), x90, new Vector3(4, 0.2f, 4)));
        // a 30 degree ramp is not a flat floor
        Assert.Null(Program.RoamPrimWorldBox(new Vector3(10, 10, 40), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 6), new Vector3(4, 4, 0.2f)));
    }

    static Program.RoamCandidate C(double x, double y, float score = 0) => new(x, y, 40f, Ahern, score, "");

    [Fact]
    public void Next_stop_avoids_recent_spots_bad_spots_and_people()
    {
        var cands = new[] { C(0, 0), C(20, 0), C(0, 20), C(20, 20) };
        var me = (10.0, 10.0);
        var rnd = new Random(1);
        for (int i = 0; i < 50; i++)
        {
            int k = Program.RoamChooseNext(cands, me, new[] { (0.0, 0.0), (20.0, 0.0) }, new[] { (0.0, 21.0) }, new HashSet<string>(), rnd);
            Assert.Equal(3, k);   // the only one not recent and not next to someone
        }
        // everything recent: relaxed, still picks something not bad
        var bad = new HashSet<string> { Program.RoamKey(cands[3]) };
        int j = Program.RoamChooseNext(cands, me, cands.Select(c => (c.X, c.Y)).ToList(), null, bad, rnd);
        Assert.InRange(j, 0, 2);
        // too close to me (< 6 m) is never picked
        Assert.Equal(-1, Program.RoamChooseNext(new[] { C(12, 10) }, me, null, null, null, rnd));
        // spread: across many picks with no history every spot gets used
        var seen = new HashSet<int>();
        for (int i = 0; i < 200; i++) seen.Add(Program.RoamChooseNext(cands, me, null, null, null, rnd));
        Assert.Equal(4, seen.Count);
    }

    [Fact]
    public void Interest_and_look_targets()
    {
        Assert.True(Program.RoamPoiInterest("Welcome Area sign", new Vector3(2, 0.1f, 1)) >= 2f);
        Assert.True(Program.RoamPoiInterest("Fountain", new Vector3(3, 3, 2)) >= 2f);
        Assert.Equal(0f, Program.RoamPoiInterest("Object", new Vector3(1, 1, 1)));
        Assert.True(Program.RoamPoiInterest("Object", new Vector3(10, 10, 6)) > 0);   // a big structure
        Assert.Equal(0f, Program.RoamPoiInterest("sign", new Vector3(0.1f, 0.1f, 0.1f)));

        var me = new Vector3(100, 100, 40);
        var sign = new Program.RoamPoi(UUID.Random(), "sign", new Vector3(105, 100, 40), 2.5f, false);
        var far = new Program.RoamPoi(UUID.Random(), "statue", new Vector3(140, 100, 40), 3f, false);
        var av = new Program.RoamPoi(UUID.Random(), "Someone", new Vector3(104, 104, 40), 1f, true);
        var r = new Random(3);
        int avatarLooks = 0;
        for (int i = 0; i < 400; i++)
        {
            var t = Program.RoamPickLookTarget(new[] { sign, far }, new[] { av }, me, r);
            Assert.NotNull(t);
            Assert.NotEqual(far.Id, t.Value.Id);   // over 15 m
            if (t.Value.IsAvatar) avatarLooks++;
        }
        Assert.InRange(avatarLooks, 20, 110);   // ~15 %: occasional glances, mostly objects
        // skip the last target
        Assert.Null(Program.RoamPickLookTarget(new[] { sign }, null, me, r, skip: sign.Id));
        Assert.Null(Program.RoamPickLookTarget(null, null, me, r));
    }

    [Fact]
    public void Look_plan_stays_inside_the_pause()
    {
        var r = new Random(5);
        for (int pause = 0; pause <= 60; pause++)
        for (int k = 0; k < 20; k++)
        {
            var plan = Program.RoamLookPlan(pause, r);
            if (pause < 6) { Assert.Empty(plan); continue; }
            Assert.InRange(plan.Count, 1, 2);
            int prevEnd = 0;
            foreach (var (at, len) in plan) { Assert.True(at >= prevEnd && len >= 2 && at + len < pause, $"{pause}: {at}+{len}"); prevEnd = at + len; }
        }
    }

    static Program.RoamSeatSeen Seat(string name = "Park bench", bool occ = false, bool adult = false, float others = 20, float leg = 2, bool path = true, bool area = true) =>
        new(UUID.Random(), name, new Vector3(10, 10, 40), occ, adult, others, leg, path, area);

    [Fact]
    public void Seat_choice_rules()
    {
        Assert.Null(Program.RoamSeatSkipReason(Seat()));
        Assert.Equal("not a seat", Program.RoamSeatSkipReason(Seat("Beach chair - parasol")));
        Assert.Equal("adult seat", Program.RoamSeatSkipReason(Seat(adult: true)));
        Assert.Equal("occupied", Program.RoamSeatSkipReason(Seat(occ: true)));
        Assert.Equal("outside the roam area", Program.RoamSeatSkipReason(Seat(area: false)));
        Assert.Contains("off the leg", Program.RoamSeatSkipReason(Seat(leg: 9)));
        Assert.Contains("someone", Program.RoamSeatSkipReason(Seat(others: 2)));
        Assert.Equal("no safe path", Program.RoamSeatSkipReason(Seat(path: false)));
        Assert.Equal("refused me before", Program.RoamSeatSkipReason(Seat(), knownBad: true));

        Assert.True(Program.RoamSitRoll(0.1, 1000));
        Assert.False(Program.RoamSitRoll(0.5, 1000));          // not every time
        Assert.False(Program.RoamSitRoll(0.0, 100));           // cooldown
        var r = new Random(2);
        for (int i = 0; i < 100; i++) Assert.InRange(Program.RoamSitDurationS(r), 60, 300);
        Assert.Equal(2f, Program.RoamDistToSegment(new Vector3(5, 2, 0), new Vector3(0, 0, 0), new Vector3(10, 0, 0)), 3);
        Assert.Equal(5f, Program.RoamDistToSegment(new Vector3(-3, 4, 0), new Vector3(0, 0, 0), new Vector3(10, 0, 0)), 3);
    }

    [Fact]
    public void Eject_and_ban_alerts_are_recognised()
    {
        Assert.True(Program.RoamIsEjectAlert("You have been ejected from this land."));   // 17:20:41 Peacock Park
        Assert.True(Program.RoamIsEjectAlert("You have been banned indefinitely"));
        Assert.True(Program.RoamIsEjectAlert("You are not allowed to enter this parcel"));
        Assert.False(Program.RoamIsEjectAlert("Autopilot canceled"));
        Assert.False(Program.RoamIsEjectAlert(null));
    }
}
