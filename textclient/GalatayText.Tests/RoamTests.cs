using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-11-11 David: roam target selection, support-surface z, path checks (stairs ok; walls/ledges not),
// multi-region coordinates at a 4-corner, and the void-edge margin rule.
public class RoamTests
{
    static ulong H(uint gx, uint gy) => SimGeo.Handle(gx * 256, gy * 256);

    [Fact]
    public void Global_local_round_trip_across_a_four_corner()
    {
        // Ahern is the NE of the four (spawn at 7.7,10.1 is the SW corner of Ahern).
        // Neighbors to the west / south have negative local coords in Ahern's frame.
        ulong ahern = H(1001, 1001), west = H(1000, 1001), south = H(1001, 1000), sw = H(1000, 1000);
        var g = Program.RoamToGlobal(ahern, 7.7f, 10.1f);
        var back = Program.RoamFromGlobal(ahern, g.x, g.y);
        Assert.InRange(back.X, 7.7f - 1e-3f, 7.7f + 1e-3f);
        Assert.InRange(back.Y, 10.1f - 1e-3f, 10.1f + 1e-3f);

        // a point just west of Ahern (in the west neighbor) reads negative X in Ahern's frame
        var gWest = Program.RoamToGlobal(west, 253f, 10f);
        var inAhern = Program.RoamFromGlobal(ahern, gWest.x, gWest.y);
        Assert.True(inAhern.X < 0, "neighbor west of Ahern is negative local X");
        Assert.InRange(inAhern.X, -3.1f, -2.9f);
        Assert.InRange(inAhern.Y, 9.9f, 10.1f);

        var gSw = Program.RoamToGlobal(sw, 250f, 250f);
        var fromAhern = Program.RoamFromGlobal(ahern, gSw.x, gSw.y);
        Assert.True(fromAhern.X < 0 && fromAhern.Y < 0);

        var gSouth = Program.RoamToGlobal(south, 8f, 250f);
        var fromAhernS = Program.RoamFromGlobal(ahern, gSouth.x, gSouth.y);
        Assert.True(fromAhernS.Y < 0);
        Assert.InRange(fromAhernS.X, 7.9f, 8.1f);
    }

    [Fact]
    public void Edge_margin_applies_only_on_void_or_unconnected_edges()
    {
        // near the west edge, no west neighbor: 8 m margin applies
        Assert.True(Program.RoamTooCloseToVoidEdge(4f, 128f, 256, 256, 8f, hasWest: false, hasEast: true, hasSouth: true, hasNorth: true));
        // same point, west neighbor is a connected allowed region: no margin
        Assert.False(Program.RoamTooCloseToVoidEdge(4f, 128f, 256, 256, 8f, hasWest: true, hasEast: true, hasSouth: true, hasNorth: true));
        // interior point never hits the margin, even with all void edges
        Assert.False(Program.RoamTooCloseToVoidEdge(128f, 128f, 256, 256, 8f, false, false, false, false));
        // south-east corner, only east is void
        Assert.True(Program.RoamTooCloseToVoidEdge(252f, 4f, 256, 256, 8f, hasWest: true, hasEast: false, hasSouth: true, hasNorth: true));
        Assert.False(Program.RoamTooCloseToVoidEdge(252f, 20f, 256, 256, 8f, hasWest: true, hasEast: true, hasSouth: true, hasNorth: true));
    }

    [Fact]
    public void Support_z_is_the_higher_of_terrain_and_a_platform_under_the_point()
    {
        // Ahern landing is a built plaza ~z 40 while terrain may be much lower
        var plaza = Program.RoamPrimBox(10, 10, 20, 20, 40f);
        float z = Program.RoamSupportZ(7.7f, 10.1f, terrain: 20f, avatarZ: 40.45f, new[] { plaza });
        Assert.Equal(40f, z);
        // off the plaza: terrain
        Assert.Equal(20f, Program.RoamSupportZ(100f, 100f, 20f, 22f, new[] { plaza }));
        // a ceiling 5 m above the avatar is ignored (only surfaces <= avatar+2 m)
        var ceiling = Program.RoamPrimBox(10, 10, 20, 20, 48f);
        Assert.Equal(40f, Program.RoamSupportZ(8f, 10f, 20f, 40.45f, new[] { plaza, ceiling }));
        // no prims: terrain
        Assert.Equal(22.5f, Program.RoamSupportZ(1, 1, 22.5f, 23f, null));
    }

    [Fact]
    public void Staircase_is_accepted_and_a_wall_or_ledge_is_rejected()
    {
        // 10 steps of 0.45 m rise over 0.5 m run (plus a flat landing). Overall rise ~4.5 m must be allowed.
        float Stair(float d)
        {
            int step = Math.Min(9, (int)(d / 0.5f));
            return 20f + step * 0.45f;
        }
        var stair = Program.RoamPathCheckAlong(6f, Stair, _ => false, _ => false);
        Assert.Equal(Program.RoamPathReject.Ok, stair);
        // a 2 m vertical jump is a wall
        float Wall(float d) => d < 2f ? 20f : 22.5f;
        Assert.Equal(Program.RoamPathReject.Wall, Program.RoamPathCheckAlong(4f, Wall, _ => false, _ => false));
        // a 1.5 m drop is a platform edge
        float Ledge(float d) => d < 2f ? 40f : 38.2f;
        Assert.Equal(Program.RoamPathReject.Ledge, Program.RoamPathCheckAlong(4f, Ledge, _ => false, _ => false));
        // water
        Assert.Equal(Program.RoamPathReject.Water, Program.RoamPathCheckAlong(4f, _ => 19f, d => d > 1f, _ => false));
        // void edge
        Assert.Equal(Program.RoamPathReject.Edge, Program.RoamPathCheckAlong(4f, _ => 20f, _ => false, d => d > 3f));
        // a single 0.5 m step is fine
        float OneStep(float d) => d < 2f ? 20f : 20.5f;
        Assert.Equal(Program.RoamPathReject.Ok, Program.RoamPathCheckAlong(4f, OneStep, _ => false, _ => false));
    }

    [Fact]
    public void Fine_samples_are_inserted_near_height_changes()
    {
        float Stair(float d) => 20f + Math.Min(9, (int)(d / 0.5f)) * 0.45f;
        var ds = Program.RoamSampleDistances(5f, Stair);
        Assert.Contains(ds, d => d > 0 && d < 2 && MathF.Abs(d % 0.5f) < 1e-3f || d == 0);
        // spacing near the stairs should include ~0.5 m samples, not only 2 m
        var deltas = ds.Zip(ds.Skip(1), (a, b) => b - a).ToList();
        Assert.Contains(deltas, d => d <= 0.51f);
    }

    [Fact]
    public void Chat_pauses_only_for_nearby_local_chat_not_ims_or_far_chat()
    {
        Assert.True(Program.RoamChatShouldPause(im: false, 0f));
        Assert.True(Program.RoamChatShouldPause(im: false, 5f));
        Assert.False(Program.RoamChatShouldPause(im: false, 5.1f));
        Assert.False(Program.RoamChatShouldPause(im: false, -1f));
        Assert.False(Program.RoamChatShouldPause(im: true, 0f));
        Assert.False(Program.RoamChatShouldPause(im: true, 100f));
    }

    [Fact]
    public void Neighbor_allowed_for_same_owner_or_linden_not_a_private_estate()
    {
        var linden = Program.RoamGovernorLinden;
        var other = new UUID("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Assert.True(Program.RoamNeighborAllowed(linden, linden, true, true));
        Assert.True(Program.RoamNeighborAllowed(other, other, true, true));
        Assert.False(Program.RoamNeighborAllowed(other, linden, true, true) is false && false); // linden neighbor of a private start: allowed (either is Linden)
        Assert.True(Program.RoamNeighborAllowed(other, linden, true, true));
        Assert.False(Program.RoamNeighborAllowed(other, new UUID("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), true, true));
        Assert.False(Program.RoamNeighborAllowed(linden, linden, connected: false, adjacent: true));
        Assert.False(Program.RoamNeighborAllowed(linden, linden, connected: true, adjacent: false));
    }

    [Fact]
    public void Walk_arrival_is_horizontal_and_does_not_require_matching_z()
    {
        Assert.True(Program.WalkCloseEnough(1.0f, 0.75f));
        Assert.True(Program.WalkCloseEnough(1.5f, 3.0f)); // z never blocks
        Assert.False(Program.WalkCloseEnough(1.51f, 0f));
        Assert.True(Program.WalkArrivedStill(1.2f, 2f, 0.1f));
        Assert.False(Program.WalkArrivedStill(1.2f, 1.9f, 0.1f));
        Assert.False(Program.WalkArrivedStill(1.6f, 2f, 0.1f));
        Assert.False(Program.WalkArrivedStill(1.0f, 2f, 0.31f));
        Assert.True(Program.WalkHorizStuck(0.2f, 3f));
        Assert.False(Program.WalkHorizStuck(0.31f, 3f));
        Assert.False(Program.WalkHorizStuck(0.1f, 2.9f));
        // precise-leg rule is unchanged
        Assert.True(Program.ShortLegNeedsFinish(true, 1.0f, 0.5f));
        Assert.False(Program.ShortLegNeedsFinish(false, 1.0f, 0.5f));
    }
}
