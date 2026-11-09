using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-11-09 (David t3194u). 1) 14:46 PT the tub's change-spot leg "arrived" 1.0 m short three times (the sim autopilot's stop
/// distance), the 0.8 m change-spot check failed, and the tub was skipped for 30 min. A precise leg now finishes the last metre.
/// 2) A bath ended after 2-4 min: a wander bath is a 15-25 min soak with the pose rotating, and a requested bath stays until told otherwise.</summary>
public class ChangeSpotWalkBathSoakTests
{
    static readonly Vector3 Spot = new(239.9f, 73.6f, 29.0f);

    static string Src(string f)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "textclient/galatay-text", f));
    }
    static Program.HomeSeatInfo Seat(string special = null, string place = null, (int, int)? stay = null) =>
        new(UUID.Random(), "seat", Spot, "upper", null, special, new(), new(), UUID.Zero, true, null, stay, Place: place);

    [Fact]
    public void A_precise_leg_stalled_at_the_stop_distance_is_not_arrived()
    {
        // the live case: tol 0.5, she stalled at 238.9,73.7 = 1.0 m from the change spot
        float d = MathF.Sqrt(MathF.Pow(238.9f - Spot.X, 2) + MathF.Pow(73.7f - Spot.Y, 2));
        Assert.InRange(d, 0.95f, 1.05f);
        Assert.True(d <= Program.AutopilotStopSlack(0.5f));             // the old code returned "arrived" here
        Assert.True(Program.ShortLegNeedsFinish(true, d, 0.5f));        // a precise leg now finishes the last metre
        Assert.False(Program.ShortLegNeedsFinish(false, d, 0.5f));      // ordinary waypoints keep the stop-distance slack
        Assert.False(Program.ShortLegNeedsFinish(true, 0.4f, 0.5f));
    }

    [Fact]
    public void The_final_approach_aims_through_the_spot_and_stops_on_it()
    {
        var from = new Vector3(238.9f, 73.6f, 29f);
        var aim = Program.StandOffAim(from, Spot, _ => true);
        Assert.True(aim.X > Spot.X + 1.0f);                              // past the spot so the ~1 m stop lands on it
        Assert.Equal(Spot, Program.StandOffAim(from, Spot, _ => false)); // never through a wall or furniture
        Assert.True(Program.FinalApproachReached(from, Spot, new Vector3(239.6f, 73.6f, 29f), 0.5f));
        Assert.True(Program.FinalApproachReached(from, Spot, new Vector3(240.45f, 73.6f, 29f), 0.5f));   // just past it
        Assert.False(Program.FinalApproachReached(from, Spot, new Vector3(239.2f, 73.6f, 29f), 0.5f));   // still short
        Assert.True(Program.AtChangeSpot(new Vector3(240.45f, 73.6f, 29f), Spot));
    }

    [Fact]
    public void The_change_spot_and_stand_out_and_centre_line_legs_are_precise()
    {
        Assert.Contains("\"change spot (\" + why + \")\", ct, precise: true)", Src("HomeSeats.cs"));
        Assert.Contains("navMode: true, precise: true)", Src("SeatStandOut.cs"));
        Assert.Contains("\"narrow centre line (\" + why + \")\", ct, precise: true)", Src("NarrowPath.cs"));
        Assert.Contains("return await FinishShortLeg(target, tol, label, ct);", Src("NavWalk.cs"));
    }

    [Fact]
    public void A_wander_bath_is_a_15_to_25_minute_soak()
    {
        var tub = Seat(special: "undress", place: "tub");
        Assert.True(Program.IsBathSoak(tub));
        Assert.All(Enumerable.Range(0, 300).Select(k => Program.HomeSitStaySeconds(tub, new Random(k))), x => Assert.InRange(x, 900, 1500));
        Assert.All(Enumerable.Range(0, 50).Select(k => Program.HomeSitStaySeconds(Seat(), new Random(k))), x => Assert.InRange(x, 120, 240));
        Assert.All(Enumerable.Range(0, 50).Select(k => Program.HomeSitStaySeconds(Seat("toilet", "toilet", (30, 60)), new Random(k), true)), x => Assert.InRange(x, 30, 60));
    }

    [Fact]
    public void A_requested_bath_stays_until_told_otherwise()
    {
        Assert.Equal(Program.BathOnRequestS, Program.HomeSitStaySeconds(Seat(special: "undress", place: "tub"), new Random(1), onRequest: true));
        Assert.True(Program.BathOnRequestS >= 3 * 3600);
        Assert.All(Enumerable.Range(0, 50).Select(k => Program.HomeSitStaySeconds(Seat(), new Random(k), onRequest: true)), x => Assert.InRange(x, 120, 240));
        Assert.Contains("HomeRandomSit(g, ct, force: cand, onRequest: true)", Src("WanderSit.cs"));
    }

    [Fact]
    public void Poses_keep_rotating_through_a_long_soak_short_sits_unchanged()
    {
        var r = new Random(3);
        for (int k = 0; k < 100; k++)
        {
            var first = Program.FirstPoseChangeAt(1200, true, r);
            Assert.InRange(first, 180, 300);
            var next = Program.NextPoseChangeAt(first, 1200, r);
            Assert.InRange(next - first, 240, 420);
            Assert.InRange(Program.FirstPoseChangeAt(200, true, r), 80, 120);   // short sit: one change at 40-60 %
            Assert.Equal(double.MaxValue, Program.NextPoseChangeAt(100, 200, r));
        }
        Assert.Equal(double.MaxValue, Program.FirstPoseChangeAt(150, true, r));
        Assert.Equal(double.MaxValue, Program.FirstPoseChangeAt(1200, false, r));
        Assert.Contains("changeAt = NextPoseChangeAt(", Src("Wander.cs"));
    }
}
