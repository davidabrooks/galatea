using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreMetaverse;
using GalatayText;
using Xunit;
namespace GalatayText.Tests;

// 2026-10-13 fixes: bedroom-only beach change, join-David rules on sit_near, real seats + next seat, lap poses
public class SitFixesTests
{
    static string Src(string f) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../galatay-text", f));

    [Fact]
    public void Beach_zone_never_swaps_in_place_outside_the_bedroom()
    {
        Assert.Equal("bedroom-first", Program.ZoneBikiniRoute("Peronaut", false, false, false, false));
        Assert.Equal("bikini-on", Program.ZoneBikiniRoute("Peronaut", true, false, false, false));
        Assert.Equal("wait-walk", Program.ZoneBikiniRoute("Peronaut", false, true, false, false));   // sit_near does its own prep
        Assert.Equal("wait-wander", Program.ZoneBikiniRoute("Peronaut", false, false, true, false));
        Assert.Equal("wait-retry", Program.ZoneBikiniRoute("Peronaut", false, false, false, true));
        Assert.Equal("bikini-on", Program.ZoneBikiniRoute("Naberrie", false, false, false, false));  // no home bedroom elsewhere
    }

    [Fact]
    public void Wander_from_the_beach_detours_to_the_bedroom()
    {
        Assert.Equal("detour-indoors", Program.IndoorBikiniPlan(true, false, false, true));
        Assert.Equal("none", Program.IndoorBikiniPlan(true, true, false, true));
    }

    [Fact]
    public void Sit_near_and_goto_David_run_the_join_David_prep()
    {
        var p = Src("Program.cs");
        Assert.Contains("cmd is \"goto_avatar\" or \"sit_near\" && NamesDavid", p);
        Assert.Contains("case \"sit_lap\"", p);
        Assert.Contains("manual: true", Src("JoinDavid.cs"));   // works while the wander is on hold
        Assert.True(Program.JoinDavidNeedsBikini(false, false, "Peronaut", new Vector3(223.5f, 55.7f, 21.4f)));
        Assert.True(Program.NamesDavid("David Nightingale"));
    }

    [Fact]
    public void Parasol_and_table_parts_are_not_seats()
    {
        Assert.False(Program.IsRealSeatName("Nerenzo beach chair - parasol"));
        Assert.False(Program.IsRealSeatName("Nerenzo beach chair - table"));
        Assert.True(Program.IsRealSeatName("Nerenzo beach chair - adult"));
        Assert.True(Program.IsRealSeatName("Garden bench"));
        Assert.False(Program.IsRealSeatName("Rock"));
        Assert.False(Program.IsRealSeatName(null));
    }

    [Fact]
    public void Next_nearest_real_seat_after_a_refusal()
    {
        var a = new UUID("00000000-0000-0000-0000-00000000000a"); var b = new UUID("00000000-0000-0000-0000-00000000000b"); var c = new UUID("00000000-0000-0000-0000-00000000000c");
        var cands = new List<(string, float, UUID)> { ("Nerenzo beach chair - parasol", 1.6f, a), ("Nerenzo beach chair - adult", 2.2f, b), ("Nerenzo beach chair - pg", 2.5f, c) };
        Assert.Equal(1, Program.PickSeatIndex(cands, new HashSet<UUID>()));
        Assert.Equal(2, Program.PickSeatIndex(cands, new HashSet<UUID> { b }));
        Assert.Equal(-1, Program.PickSeatIndex(cands, new HashSet<UUID> { b, c }));
        Assert.True(Program.IsNoRoomAlert("No room to sit here, try another spot."));
        Assert.False(Program.IsNoRoomAlert("Autopilot canceled"));
        Assert.Contains("SitNearWithFallback", Src("NavWalk.cs"));
    }

    [Fact]
    public void Lap_pose_table_and_menu_fallback()
    {
        Assert.Equal(new[] { "CUDDLES*", "on you" }, Program.LapPoseFor("Nerenzo beach chair - adult"));
        Assert.Null(Program.LapPoseFor("Some sofa"));
        var root = new List<string> { "[ADJUST]", "SINGLES*", "ADULT*", "CUDDLES*", "[BACK]" };
        Assert.Equal(3, Program.PickLapMenu(root));
        Assert.Equal(1, Program.PickLapMenu(new List<string> { "Solo*", "Couples PG*", "Adult M+F*" }));
        var cud = new List<string> { "paradise", "like us", "the way", "on you", "my moon", "[BACK]" };
        Assert.Equal(3, Program.PickLapLeaf(cud));
        Assert.Equal(0, Program.PickLapLeaf(new List<string> { "Lap sit", "Hug" }));
        Assert.Equal(-1, Program.PickLapLeaf(new List<string> { "paradise", "my moon" }));
    }

    [Fact]
    public void Root_seat_of_a_sitting_avatar()
    {
        var parents = new Dictionary<uint, uint> { [10] = 5, [5] = 0 };
        Assert.Equal(5u, Program.RootSeatOf(10, id => parents.TryGetValue(id, out var p) ? p : 0));
        Assert.Equal(0u, Program.RootSeatOf(0, _ => 0));
    }
}
