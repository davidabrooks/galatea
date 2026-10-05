using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

public class RouteDoorAndFrontTests
{
    [Fact]
    public void Door_pair_group_includes_double_doors_same_axis()
    {
        var east = new Program.NavDoor { Name = "front-double-east", Id = UUID.Parse("1b5752e3-7a49-aa75-621f-033b5b61d125"), AlongX = true, Center = new Vector3(229.44f, 79.58f, 29.39f) };
        var west = new Program.NavDoor { Name = "front-double-west", Id = UUID.Parse("22927a8c-b2f0-1299-d837-952bd27b31da"), AlongX = true, Center = new Vector3(226.66f, 79.58f, 29.39f) };
        var side = new Program.NavDoor { Name = "side", Id = UUID.Parse("c9a6a523-81dd-fadc-331f-4e0147ee2841"), AlongX = false, Center = new Vector3(231.85f, 76.03f, 29.38f) };
        var pair = Program.DoorPairGroup(new[] { east, west, side }, east);
        Assert.Equal(2, pair.Count);
        Assert.Contains(pair, d => d.Name.Contains("west"));
        Assert.Equal(300, Program.DoorThroughDelayMs);
    }

    [Fact]
    public void Front_arc_is_tight_and_ends_in_front()
    {
        var me = new Vector2(-2f, 0f);
        var L = new Vector2(0, 0);
        var pts = Program.FrontArcWaypoints(me, L, facingYawRad: 0f, frontDist: 1.5f, orbitR: 1.0f);
        Assert.True(pts.Count >= 1);
        Assert.InRange(pts[^1].X, 1.45f, 1.55f);
        Assert.InRange(pts[^1].Y, -0.05f, 0.05f);
        Assert.True(Program.FrontArcMinClearance(me, L, pts) >= 0.85f);
    }

    [Fact]
    public void Front_arc_from_side_does_not_cut_through()
    {
        var me = new Vector2(0f, 2f);
        var L = Vector2.Zero;
        var pts = Program.FrontArcWaypoints(me, L, 0f, 1.5f, 1.0f);
        Assert.True(Program.FrontArcMinClearance(me, L, pts) >= 0.85f);
    }
}
