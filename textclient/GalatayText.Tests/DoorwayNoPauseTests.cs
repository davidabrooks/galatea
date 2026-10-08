using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-07 19:45 David: "Don't pause your walk while going through a doorway" (19:44:37 short pause at s=22 in the front doors).</summary>
public class DoorwayNoPauseTests
{
    static bool CrossesY(Vector2 p, Vector2 q, float y) => (p.Y - y) * (q.Y - y) <= 0 && p.Y != q.Y;

    [Fact]
    public void Crossings_found_along_the_whole_path()
    {
        Func<float, Vector2> at = u => new Vector2(228.5f, 60f + u);    // straight north
        var c = Program.AllDoorCrossings(at, 40f, (p, q) => CrossesY(p, q, 82f) || CrossesY(p, q, 95f));
        Assert.Equal(2, c.Count);
        Assert.InRange(c[0], 21.5f, 22.5f);
        Assert.InRange(c[1], 34.5f, 35.5f);
    }

    [Fact]
    public void Zone_runs_from_1_5_m_before_to_2_m_past_each_door()
    {
        var c = new List<float> { 22f };
        Assert.Null(Program.DoorZoneClearAt(20.3f, c));
        Assert.Equal(24f, Program.DoorZoneClearAt(20.6f, c));
        Assert.Equal(24f, Program.DoorZoneClearAt(22f, c));    // the 19:44:37 pause spot
        Assert.Equal(24f, Program.DoorZoneClearAt(23.9f, c));
        Assert.Null(Program.DoorZoneClearAt(24.1f, c));
        // back-to-back doors chain into one zone
        Assert.Equal(27f, Program.DoorZoneClearAt(22.5f, new List<float> { 22f, 25f }));
    }

    [Fact]
    public void Idle_pause_never_in_a_doorway_or_next_to_a_door()
    {
        var c = new List<float> { 22f };
        Assert.False(Program.IdlePauseAllowedHere(22f, c, nearDoor: false));
        Assert.False(Program.IdlePauseAllowedHere(40f, c, nearDoor: true));
        Assert.True(Program.IdlePauseAllowedHere(24.5f, c, nearDoor: false));
        Assert.True(Program.NearDoorCentre(new Vector2(228.5f, 81f), new[] { new Vector2(228.5f, 82f) }));
        Assert.False(Program.NearDoorCentre(new Vector2(228.5f, 79f), new[] { new Vector2(228.5f, 82f) }));
    }

    [Fact]
    public void Blocker_beyond_the_doorway_waits_until_clear_but_never_walks_into_someone()
    {
        Assert.True(Program.DeferBlockerPause(22f, 24f, 24.9f));    // person past the exit: walk on, pause outside
        Assert.False(Program.DeferBlockerPause(22f, 24f, 23f));     // person in the doorway: stop
        Assert.False(Program.DeferBlockerPause(24.2f, 24f, 25f));   // too close
        Assert.False(Program.DeferBlockerPause(10f, null, 12f));    // not in a doorway: the normal pause
    }

    [Fact]
    public void Door_touch_keeps_walking_when_the_leaf_has_time_to_swing()
    {
        Assert.Equal(0, Program.DoorApproachHoldMs(3f, 3f, 300, anyTouched: true));    // 1 s away: no stop
        Assert.Equal(0, Program.DoorApproachHoldMs(0f, 0f, 300, anyTouched: false));   // already open: never stop
        Assert.Equal(Program.DoorSwingWaitMs - 300, Program.DoorApproachHoldMs(0f, 3f, 300, anyTouched: true)); // at the leaf: wait for it
        Assert.InRange(Program.DoorApproachHoldMs(1.5f, 3f, 300, true), 0, 150);
    }
}
