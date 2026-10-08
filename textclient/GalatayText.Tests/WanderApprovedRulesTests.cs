using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-08 14:51 David approved: listed seats only; toilet and sink sits 30-60 s; rest on the sofa after
/// 3 failed walks and carry on (capped); the bikini comes off indoors while the wander is paused (8-min guard kept).</summary>
public class WanderApprovedRulesTests
{
    static string RouteFile(string f)
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", f));
        return File.Exists(repo) ? repo : "/workspace/galatea-sl-repo/textclient/routes/" + f;
    }
    static List<Program.HomeSeatInfo> Seats() => Program.ParseHomeSeats(File.ReadAllText(RouteFile("_seats-peronaut-home.json")));
    static readonly UUID Sofa = new("f6844138-499f-0f3e-ba6a-ce601a16eafe");
    static readonly DateTime T0 = new(2026, 10, 8, 15, 0, 0);

    [Fact]
    public void Wander_uses_listed_seats_only()
    {
        var w = Program.HomeWanderSeatInfos(Seats());
        Assert.True(Program.HomeWanderSeatAllowed(Sofa, w));
        Assert.True(Program.HomeWanderSeatAllowed(Program.ToiletSeatId, w));
        Assert.True(Program.HomeWanderSeatAllowed(Program.BathroomSinkId, w));
        // unlisted, seat-named (the Newbrooke bench on the far mooring deck) or brand new furniture: never
        Assert.False(Program.HomeWanderSeatAllowed(new UUID("e2d7b6d2-e555-fb15-cf94-7d9200088812"), w));
        Assert.False(Program.HomeWanderSeatAllowed(UUID.Random(), w));
        // listed but wander false (removed deck chairs, the parasol): never
        Assert.False(Program.HomeWanderSeatAllowed(new UUID("9970956b-d6dc-8328-224a-6f527268eb3f"), w));
        Assert.False(Program.HomeWanderSeatAllowed(new UUID("e352f3a5-1c88-1747-b5fb-b9bc703eb2db"), w));
        Assert.All(w.Values, i => Assert.True(i.Wander));
    }

    [Fact]
    public void Toilet_and_sink_sits_are_short_others_unchanged()
    {
        var s = Seats().ToDictionary(i => i.Id);
        Assert.Equal((30, 60), s[Program.ToiletSeatId].StayS);
        Assert.Equal((30, 60), s[Program.BathroomSinkId].StayS);
        foreach (var id in new[] { Program.ToiletSeatId, Program.BathroomSinkId })
            Assert.All(Enumerable.Range(0, 300).Select(k => Program.HomeSitStaySeconds(s[id], new Random(k))), x => Assert.InRange(x, 30, 60));
        Assert.Null(s[Sofa].StayS);
        var sofa = Enumerable.Range(0, 300).Select(k => Program.HomeSitStaySeconds(s[Sofa], new Random(k))).ToList();
        Assert.All(sofa, x => Assert.InRange(x, 120, 240));
        Assert.All(Enumerable.Range(0, 50).Select(k => Program.HomeSitStaySeconds(null, new Random(k))), x => Assert.InRange(x, 120, 240));
        // the hand wash after the toilet keeps its own 20-40 s
        Assert.All(Enumerable.Range(0, 200).Select(k => Program.WashStaySeconds(new Random(k))), x => Assert.InRange(x, 20, 40));
        // a bad stay_s is ignored (default sit)
        var bad = Program.ParseHomeSeats("{\"seats\":[{\"uuid\":\"" + Sofa + "\",\"stay_s\":[60,30]},{\"uuid\":\"" + Program.ToiletSeatId + "\",\"stay_s\":[\"a\",2]}]}");
        Assert.All(bad, i => Assert.Null(i.StayS));
    }

    [Fact]
    public void Three_failed_walks_mean_a_rest_then_wander_again_capped_per_hour()
    {
        Assert.True(Program.RestAllowed(new List<DateTime>(), T0));
        Assert.True(Program.RestAllowed(new[] { T0.AddMinutes(-10) }, T0));
        Assert.False(Program.RestAllowed(new[] { T0.AddMinutes(-50), T0.AddMinutes(-10) }, T0));
        Assert.True(Program.RestAllowed(new[] { T0.AddMinutes(-70), T0.AddMinutes(-61) }, T0));   // older than an hour
        Assert.All(Enumerable.Range(0, 300).Select(k => Program.RestSeconds(new Random(k))), x => Assert.InRange(x, 300, 600));
        // rest state: resting while seated with time left; over when the time is up or she got up (e.g. to talk)
        Assert.Equal("none", Program.RestState(null, T0, true, true));
        Assert.Equal("rest", Program.RestState(T0.AddMinutes(4), T0, true, true));
        Assert.Equal("end", Program.RestState(T0.AddMinutes(4), T0, true, false));    // got up from the sofa (to talk)
        Assert.Equal("end", Program.RestState(T0, T0, true, true));                   // time is up
        Assert.Equal("rest", Program.RestState(T0.AddMinutes(4), T0, false, false));  // sofa sit failed: rests standing
        Assert.Equal("end", Program.RestState(T0.AddMinutes(-1), T0, false, false));
    }

    [Fact]
    public void A_spot_that_always_fails_cannot_loop_forever()
    {
        // every 3 failed walks take ~2 min, then a 5-10 min rest: at most 2 rests, then the wander turns off
        var rests = new List<DateTime>(); var t = T0; int restsTaken = 0; bool off = false;
        for (int round = 0; round < 50 && !off; round++)
        {
            t = t.AddMinutes(2);
            if (Program.RestAllowed(rests, t)) { rests.Add(t); restsTaken++; t = t.AddSeconds(Program.RestSeconds(new Random(round))); }
            else off = true;
        }
        Assert.True(off);
        Assert.Equal(Program.MaxRestsPerHour, restsTaken);
        Assert.Equal(2, Program.MaxRestsPerHour);
    }

    [Fact]
    public void Paused_indoors_changes_back_out_of_the_bikini_but_the_guard_and_the_beach_net_stay()
    {
        // who owns the change back: only a moving home wander (the 15 s greeting stop counts as moving)
        Assert.True(Program.HomeWanderOwnsRestore(true, true, null));
        Assert.True(Program.HomeWanderOwnsRestore(true, true, "greet"));
        foreach (var p in new[] { "chat", "hold", "user", "ao", "rest" }) Assert.False(Program.HomeWanderOwnsRestore(true, true, p));
        Assert.False(Program.HomeWanderOwnsRestore(false, true, null));
        Assert.False(Program.HomeWanderOwnsRestore(true, false, null));
        var now = T0.ToUniversalTime(); var noGuard = DateTime.MinValue; var guard = now.AddMinutes(5);
        bool Own(string p) => Program.HomeWanderOwnsRestore(true, true, p);
        // paused indoors in the bikini (beach mode): change back now, unless the bedroom change was < 8 min ago
        Assert.Equal("restore", Program.ZoneTickAction("house", true, true, true, Own("chat"), now, noGuard));
        Assert.Equal("restore", Program.ZoneTickAction("house", true, true, true, Own("rest"), now, noGuard));
        Assert.Equal("wait-guard", Program.ZoneTickAction("house", true, true, true, Own("chat"), now, guard));
        // moving: the wander changes back in the bedroom, as before
        Assert.Equal("wait-wander", Program.ZoneTickAction("house", true, true, true, Own(null), now, noGuard));
        // paused outside (patio, porch): wait until inside
        Assert.Equal("wait-indoors", Program.ZoneTickAction("house", true, true, false, Own("chat"), now, noGuard));
        // beach safety net: on the beach level without the bikini she changes on the spot, whatever the wander does
        foreach (var p in new string[] { null, "greet", "chat", "hold", "rest" })
            Assert.Equal("bikini-on", Program.ZoneTickAction("beach", false, false, false, Own(p), now, noGuard));
        Assert.Equal("bikini-on", Program.ZoneTickAction("beach", false, false, false, false, now, noGuard));
        // ... and it never fights the bedroom change: already changed (beach mode) = nothing; worn = just mark it
        Assert.Equal("none", Program.ZoneTickAction("beach", true, true, false, Own(null), now, guard));
        Assert.Equal("mark-beach", Program.ZoneTickAction("beach", false, true, false, Own(null), now, noGuard));
        // the bedroom plan leaves a beach-level start to the safety net, and changes indoors otherwise
        Assert.Equal("none", Program.IndoorBikiniPlan(true, false, false, true));
        Assert.Equal("change-here", Program.IndoorBikiniPlan(true, false, true, false));
        Assert.Equal("detour-indoors", Program.IndoorBikiniPlan(true, false, false, false));
    }
}
