using GalatayText;
using LibreMetaverse;
using Xunit;
public class LoginJoinTests
{
    [Fact] public void SpawnDistance_OnlyWhenBothArrived()
    {
        Assert.Null(Program.LoginSpawnDistance(null, new Vector3(1, 1, 1)));
        Assert.Null(Program.LoginSpawnDistance(new Vector3(1, 1, 1), null));
        Assert.Null(Program.LoginSpawnDistance(Vector3.Zero, new Vector3(1, 1, 1)));   // stale/unset position
        Assert.Equal(15.6f, Program.LoginSpawnDistance(new Vector3(230.2f, 60.7f, 29.1f), new Vector3(228.2f, 76.2f, 29.1f)).Value, 1);
    }
    [Fact] public void AcrossTheRoom_WakeNeverSaysBySide()
    {
        var t = Program.LoginPlacementText(new Program.LoginPlacement(15.6f, "walked", 1.4f));
        Assert.Contains("spawned 15.6 m", t); Assert.Contains("walked over", t); Assert.Contains("did NOT appear by his side", t);
        Assert.Contains("UNKNOWN", Program.LoginPlacementText(null));
        Assert.Contains("UNKNOWN", Program.LoginPlacementText(new Program.LoginPlacement(null, "unknown", null)));
        Assert.DoesNotContain("NOT appear", Program.LoginPlacementText(new Program.LoginPlacement(1.2f, "spawned-near", 1.2f)));
    }
    [Fact] public void RelogAwayFromHim_WalksOver()
    {
        Assert.True(Program.LoginShouldJoin(15.6f, true, false));
        Assert.False(Program.LoginShouldJoin(1.5f, true, false));
        Assert.False(Program.LoginShouldJoin(15.6f, false, false));    // he's not in my region
        Assert.False(Program.LoginShouldJoin(15.6f, true, true));      // seated
        Assert.False(Program.LoginShouldJoin(null, true, false));      // unknown: don't guess
    }
    // ---- 2026-10-27 08:43: David on the Mirage bed 4.7 m away; the walk started 1 s after login and went onto the mattress ----
    static readonly UUID BedId = UUID.Parse("cdedb159-2e61-e54d-6045-283a58d25015");
    static readonly Program.FurnitureBox Bed = new("BENTO PHYSICS MIRAGE BED 1.0", BedId.ToString(), 214.46f, 72.71f, 217.92f, 76.16f);
    static Program.HomeSeatInfo BedSeat(Vector3? standSpot) =>
        new(BedId, "BENTO PHYSICS MIRAGE BED 1.0", new Vector3(216.15f, 75.28f, 27.87f), "upper", null, null, new(), new(), UUID.Zero, true, StandSpot: standSpot);
    static readonly Vector3 Spawn = new(220.6f, 74.3f, 29.1f), OnBed = new(215.9f, 74.8f, 29.1f);

    [Fact] public void NotLoadedYet_WaitsForAppearanceOutfitAndRoom()
    {
        var W = Program.LoginLoadVerdict.Wait; var L = Program.LoginLoadVerdict.Loaded;
        Assert.Equal(W, Program.LoginLoadStep(1, 1, false, 0, false));      // 1 s after login (the 08:43:26 walk start)
        Assert.Equal(W, Program.LoginLoadStep(10, 10, true, 12, true));     // too early: the outfit re-send has not even run
        Assert.Equal(W, Program.LoginLoadStep(40, 3, true, 14, true));      // attachments / objects still arriving
        Assert.Equal(W, Program.LoginLoadStep(40, 9, false, 19, true));     // no appearance yet
        Assert.Equal(W, Program.LoginLoadStep(40, 9, true, 19, false));     // his seat prim not in yet
        Assert.Equal(W, Program.LoginLoadStep(40, 9, true, 0, true));       // wearing nothing yet
        Assert.Equal(L, Program.LoginLoadStep(40, 9, true, 19, true));
        Assert.Equal(Program.LoginLoadVerdict.GaveUp, Program.LoginLoadStep(76, 0, false, 0, false));   // never wait forever
    }
    [Fact] public void DavidOnTheBed_WalksToTheSeatsFloorSpot_NeverOntoTheBed()
    {
        var spot = new Vector3(218.32f, 75.2f, 29.0f);
        var g = Program.LoginJoinGoal(Spawn, OnBed, true, BedId, new[] { BedSeat(spot) }, new[] { Bed });
        Assert.Equal("spot", g.Kind);
        Assert.True(System.Math.Abs(g.To.Value.X - spot.X) + System.Math.Abs(g.To.Value.Y - spot.Y) < 0.01f);
        Assert.Null(Program.InFurnitureMargin(g.To.Value, new[] { Bed }, 0.3f));
    }
    [Fact] public void DavidOnTheBed_UnknownSeat_ComputesAFloorSpotOutsideTheFootprint_WithAClearLine()
    {
        var g = Program.LoginJoinGoal(Spawn, OnBed, true, UUID.Zero, new Program.HomeSeatInfo[0], new[] { Bed });
        Assert.Equal("spot", g.Kind);
        Assert.Null(Program.InFurnitureMargin(g.To.Value, new[] { Bed }, 0.35f));          // not on the bed or its margin
        Assert.True(g.To.Value.X > Bed.X1);                                                 // the room side, the straight line never crosses the mattress
        for (float t = 0; t <= 1; t += 0.02f)
            Assert.Null(Program.InFurnitureMargin(Spawn + (g.To.Value - Spawn) * t, new[] { Bed }, 0.1f));
    }
    [Fact] public void RouteWouldCrossTheBed_NoStraightWalk_NoSpotMeansStayPut()
    {
        // me on the far (west) side of the bed, him standing east of it: the straight line crosses the mattress
        var me = new Vector3(213.5f, 74.5f, 29.1f); var him = new Vector3(219.5f, 74.5f, 29.1f);
        var g = Program.LoginJoinGoal(me, him, false, UUID.Zero, new Program.HomeSeatInfo[0], new[] { Bed });
        Assert.NotEqual("follow", g.Kind);                                                  // never the plain walk across the bed
        if (g.To is Vector3 to) for (float t = 0; t <= 1; t += 0.02f) Assert.Null(Program.InFurnitureMargin(me + (to - me) * t, new[] { Bed }, 0.1f));
        // walled in: every spot blocked -> no walk
        var none = Program.LoginJoinGoal(Spawn, OnBed, true, BedId, new[] { BedSeat(null) }, new[] { Bed }, (x, y) => true);
        Assert.Equal("none", none.Kind); Assert.Null(none.To);
    }
    [Fact] public void OpenFloor_KeepsTheFollowWalk()
    {
        var g = Program.LoginJoinGoal(new Vector3(230.2f, 60.7f, 29.1f), new Vector3(228.2f, 76.2f, 29.1f), false, UUID.Zero, new Program.HomeSeatInfo[0], new[] { Bed });
        Assert.Equal("follow", g.Kind);
    }
    [Fact] public void PlacementTextForTheFurnitureWalk()
    {
        var t = Program.LoginPlacementText(new Program.LoginPlacement(4.7f, "spot-walked", 2.4f));
        Assert.Contains("fully loaded", t); Assert.Contains("beside the furniture", t); Assert.Contains("never onto it", t); Assert.Contains("did NOT appear", t);
        Assert.Contains("stayed where I was", Program.LoginPlacementText(new Program.LoginPlacement(4.7f, "no-route", 4.7f)));
        Assert.Contains("did not walk", Program.LoginPlacementText(new Program.LoginPlacement(4.7f, "met", 1.9f)));
    }
}
