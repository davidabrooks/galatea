using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-14 13:17 David: on the Beach House boat she chose 'tan 1', which he was already in, and sat on him.</summary>
public class SeatOccupancyTests
{
    [Fact]
    public void Overlap_threshold_half_metre()
    {
        var me = new Vector3(10, 10, 22);
        Assert.True(Program.PoseOverlaps(me, new[] { new Vector3(10.2f, 10.1f, 22.1f) }, 0.5f));
        Assert.False(Program.PoseOverlaps(me, new[] { new Vector3(11, 10, 22) }, 0.5f));
        Assert.False(Program.PoseOverlaps(me, Array.Empty<Vector3>(), 0.5f));
        Assert.Equal(1f, Program.NearestAvatarDist(me, new[] { new Vector3(11, 10, 22), new Vector3(13, 10, 22) }), 3);
    }

    [Fact]
    public void Leaf_whose_spot_David_sits_in_is_taken()
    {
        var spots = new Dictionary<string, Vector3> { ["tan 1"] = new(1, 2, 0.5f), ["tan 2"] = new(-1, 2, 0.5f), ["chill 1"] = new(0, -2, 0.6f) };
        var david = new[] { new Vector3(1.1f, 2.05f, 0.45f) };
        var t = Program.PoseLeavesTaken(spots, null, david, 0.5f);
        Assert.Equal(new[] { "tan 1" }, t.ToArray());
        Assert.Empty(Program.PoseLeavesTaken(spots, null, Array.Empty<Vector3>(), 0.5f));
        Assert.Contains("chill 3", Program.PoseLeavesTaken(spots, new[] { "chill 3" }, david, 0.5f));
    }

    [Fact]
    public void Taken_leaves_are_dropped_submenus_and_controls_kept()
    {
        var labels = new List<string> { "[SWAP]", "[BACK]", "tan 1", "Tan 2", "chill 1", "SINGLE*" };
        var r = Program.PoseDropTaken(labels, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tan 1", "tan 2" });
        Assert.Equal(new[] { "[SWAP]", "[BACK]", "chill 1", "SINGLE*" }, r);
        Assert.Equal(labels, Program.PoseDropTaken(labels, new HashSet<string>()));
    }

    [Fact]
    public void Boat_pick_never_lands_on_the_taken_pose()
    {
        var labels = new List<string> { "[SWAP]", "tan 1", "tan 2", "[BACK]" };
        var free = Program.PoseDropTaken(labels, new HashSet<string> { "tan 1" });
        for (int k = 0; k < 30; k++) Assert.Equal("tan 2", Program.BoatPickPose(free, "chill 1", new Random(k)));
    }

    [Fact]
    public void Rmem_max_warning()
    {
        Assert.Contains("capped", Program.UdpRcvbufWarning(212992, 8 << 20));
        Assert.Null(Program.UdpRcvbufWarning(16 << 20, 8 << 20));
        Assert.Null(Program.UdpRcvbufWarning(0, 8 << 20));
    }
}
