using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-11-11 David: "come here" from David Nightingale works at any distance or sim.
/// Other-sim with no map location: IM him asking for a teleport offer — never a lure request.</summary>
public class DavidComeHereTests
{
    [Fact]
    public void Far_same_region_local_chat_from_David_approaches()
    {
        var p = Program.PlanComeHere(isDavid: true, isComeRequest: true, im: false,
            sameRegion: true, positionKnown: true, dist: 80f, mapLocationKnown: false);
        Assert.Equal(Program.ComeHereAction.Approach, p.action);
    }

    [Fact]
    public void IM_same_region_from_David_approaches_even_when_far()
    {
        var p = Program.PlanComeHere(isDavid: true, isComeRequest: true, im: true,
            sameRegion: true, positionKnown: true, dist: 200f, mapLocationKnown: false);
        Assert.Equal(Program.ComeHereAction.Approach, p.action);
    }

    [Fact]
    public void IM_other_region_with_map_location_teleports()
    {
        var p = Program.PlanComeHere(isDavid: true, isComeRequest: true, im: true,
            sameRegion: false, positionKnown: false, dist: -1f, mapLocationKnown: true);
        Assert.Equal(Program.ComeHereAction.TeleportToLocation, p.action);
    }

    [Fact]
    public void IM_other_region_without_map_asks_for_a_teleport_offer_and_never_sends_a_lure_request()
    {
        var p = Program.PlanComeHere(isDavid: true, isComeRequest: true, im: true,
            sameRegion: false, positionKnown: false, dist: -1f, mapLocationKnown: false);
        Assert.Equal(Program.ComeHereAction.AskForTeleportOffer, p.action);
        Assert.NotEqual("RequestLure", p.action.ToString());
        Assert.Equal("I can't get to you from here, could you send me a TP?", Program.DavidComeHereAskTpIm);
        Assert.DoesNotContain("lure request", Program.DavidComeHereAskTpIm, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Local_chat_from_David_still_approaches_when_region_is_unknown()
    {
        var p = Program.PlanComeHere(isDavid: true, isComeRequest: true, im: false,
            sameRegion: null, positionKnown: false, dist: -1f, mapLocationKnown: false);
        Assert.Equal(Program.ComeHereAction.Approach, p.action);
    }

    [Fact]
    public void Non_David_far_local_chat_does_not_approach()
    {
        var p = Program.PlanComeHere(isDavid: false, isComeRequest: true, im: false,
            sameRegion: true, positionKnown: true, dist: 25f, mapLocationKnown: false);
        Assert.Equal(Program.ComeHereAction.None, p.action);
        // 19 m is past the 5 m wander pause but within the old 20 m come cap
        var near = Program.PlanComeHere(isDavid: false, isComeRequest: true, im: false,
            sameRegion: true, positionKnown: true, dist: 19f, mapLocationKnown: false);
        Assert.Equal(Program.ComeHereAction.Approach, near.action);
    }

    [Fact]
    public void Non_David_nearby_local_chat_still_approaches()
    {
        var p = Program.PlanComeHere(isDavid: false, isComeRequest: true, im: false,
            sameRegion: true, positionKnown: true, dist: 12f, mapLocationKnown: false);
        Assert.Equal(Program.ComeHereAction.Approach, p.action);
        Assert.Equal(20f, Program.OtherComeLocalMaxM);
        Assert.Equal(40f, Program.OtherComeImMaxM);
    }

    [Fact]
    public void Come_here_is_David_only_for_other_regions()
    {
        var p = Program.PlanComeHere(isDavid: false, isComeRequest: true, im: true,
            sameRegion: false, positionKnown: false, dist: -1f, mapLocationKnown: true);
        Assert.Equal(Program.ComeHereAction.None, p.action);
    }

    [Fact]
    public void Hearing_checks_are_not_come_requests()
    {
        Assert.False(Program.IsComeRequest("can you hear me babe?"));
        var p = Program.PlanComeHere(isDavid: true, isComeRequest: false, im: false,
            sameRegion: true, positionKnown: true, dist: 80f, mapLocationKnown: false);
        Assert.Equal(Program.ComeHereAction.None, p.action);
    }
}
