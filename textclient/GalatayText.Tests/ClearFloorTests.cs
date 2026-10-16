using GalatayText;
using LibreMetaverse;
using Xunit;
using static GalatayText.Program;

namespace GalatayText.Tests;

/// <summary>2026-10-16 16:30 PT (David: "you were standing on that makeup desk before you changed"): before any outfit change after a
/// stand-up (Naked swap, re-dress, toilet / tub undress) she steps to clear floor beside the furniture first.</summary>
public class ClearFloorTests
{
    static string RouteFile(string f)
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", f));
        return File.Exists(repo) ? repo : "/workspace/galatea-sl-repo/textclient/routes/" + f;
    }
    static List<FurnitureBox> Boxes() => ParseFurnitureBoxes(File.ReadAllText(RouteFile("_graph-Peronaut.json")));
    static HomeSeatInfo Table(bool withSpot = true)
    {
        var s = ParseHomeSeats(File.ReadAllText(RouteFile("_seats-peronaut-home.json"))).First(x => x.Name.StartsWith("Xplicit Makeup Table"));
        return withSpot ? s : s with { StandSpot = null, Approach = null, ChangeSpot = null };
    }
    static float Hd(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    // where David saw her after standing from the table (log: standing pos 222.6,72.7,29.7)
    static readonly Vector3 OnTable = new(222.6f, 72.7f, 29.7f);

    [Fact]
    public void Standing_on_the_makeup_table_steps_to_its_listed_clear_spot()
    {
        var p = ClearStepPlan(OnTable, Table(), Boxes(), null);
        Assert.True(p.Needed); Assert.True(p.FromData);
        Assert.Equal(221.64f, p.To.Value.X, 2); Assert.Equal(72.9f, p.To.Value.Y, 2);
        Assert.Null(InFurniture(p.To.Value, Boxes()));
    }

    [Fact]
    public void Without_a_listed_spot_a_clear_floor_spot_is_computed_within_2_m_toward_the_room()
    {
        var boxes = Boxes();
        var p = ClearStepPlan(OnTable, Table(withSpot: false), boxes, null);
        Assert.True(p.Needed); Assert.False(p.FromData); Assert.NotNull(p.To);
        var to = p.To.Value;
        Assert.Null(InFurnitureMargin(to, boxes, ClearBodyMargin));   // outside every footprint with the body margin
        Assert.True(Hd(OnTable, to) <= ClearMaxRadiusM + 0.01f);
        Assert.True(to.X < OnTable.X);                               // west = the room, away from the table and the east wall
    }

    [Fact]
    public void A_wall_cell_on_the_nav_grid_is_never_chosen_and_the_walk_is_not_through_it()
    {
        var boxes = Boxes();
        bool Wall(float x, float y) => x < 221.0f;                   // everything west of 221 is a wall
        var p = ClearStepPlan(OnTable, Table(withSpot: false), boxes, Wall);
        Assert.NotNull(p.To);
        Assert.False(Wall(p.To.Value.X, p.To.Value.Y));
        Assert.Null(InFurnitureMargin(p.To.Value, boxes, ClearBodyMargin));
    }

    [Fact]
    public void No_clear_spot_is_reported_and_proceeds_as_before()
    {
        var p = ClearStepPlan(OnTable, Table(withSpot: false), Boxes(), (x, y) => Hd(new Vector3(x, y, 0), new Vector3(OnTable.X, OnTable.Y, 0)) > 0.6f);
        Assert.True(p.Needed); Assert.Null(p.To);
        Assert.Contains("no clear floor spot", p.Why);
    }

    [Fact]
    public void Clear_floor_needs_no_step()
    {
        var p = ClearStepPlan(new Vector3(221.0f, 72.9f, 29.0f), Table(), Boxes(), null);
        Assert.False(p.Needed); Assert.Null(p.To);
        Assert.False(ClearStepPlan(new Vector3(100, 100, 30), Table(), Boxes(), null).Needed);
    }

    [Fact]
    public void Standing_high_above_a_seats_floor_spot_counts_as_on_the_furniture_even_without_a_footprint()
    {
        var seat = Table();
        var me = new Vector3(222.9f, 72.7f, 29.8f);
        var p = ClearStepPlan(me, seat, new List<FurnitureBox>(), null);
        Assert.True(p.Needed); Assert.True(p.FromData);
        Assert.False(ClearStepPlan(new Vector3(222.9f, 72.7f, 29.1f), seat, new List<FurnitureBox>(), null).Needed);   // on the floor
    }

    [Fact]
    public void A_seat_she_is_not_next_to_does_not_send_her_to_its_spot()
    {
        // stale seat info (the table) while she stands inside another footprint far away: computed spot, not the table's
        var boxes = new List<FurnitureBox> { new("Bench", "x", 100f, 100f, 102f, 101f) };
        var p = ClearStepPlan(new Vector3(101f, 100.5f, 30f), Table(), boxes, null);
        Assert.True(p.Needed); Assert.False(p.FromData); Assert.True(Hd(p.To.Value, new Vector3(101f, 100.5f, 30f)) <= ClearMaxRadiusM + 0.01f);
    }

    [Fact]
    public void Other_furniture_in_the_way_is_not_walked_through()
    {
        var table = new FurnitureBox("Table", "t", 10f, 10f, 12f, 12f);
        var wall = new FurnitureBox("Cabinet", "c", 8.0f, 9f, 9.4f, 13f);   // just west of the table: the straight west step crosses it
        var p = ClearStepPlan(new Vector3(10.4f, 11f, 30f), null, new List<FurnitureBox> { table, wall }, null);
        Assert.NotNull(p.To);
        Assert.Null(InFurnitureMargin(p.To.Value, new[] { table, wall }, ClearBodyMargin));
        Assert.True(p.To.Value.X > 9.4f + ClearBodyMargin || p.To.Value.Y < 9f - ClearBodyMargin || p.To.Value.Y > 13f + ClearBodyMargin || p.To.Value.X > 12f);
    }

    [Fact]
    public void Nearest_listed_seat_is_found_for_the_redress()
    {
        var seats = ParseHomeSeats(File.ReadAllText(RouteFile("_seats-peronaut-home.json")));
        Assert.StartsWith("Xplicit Makeup Table", NearestHomeSeat(OnTable, seats).Name);
        Assert.Null(NearestHomeSeat(new Vector3(5, 5, 30), seats));
    }

    static string Src(string f)
    {
        var p = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "galatay-text", f));
        return File.ReadAllText(File.Exists(p) ? p : "/workspace/galatea-sl-repo/textclient/galatay-text/" + f);
    }

    [Fact]
    public void Every_outfit_change_flow_steps_clear_first()
    {
        var naked = Src("AdultRedress.cs");
        int clear = naked.IndexOf("ClearFloorForOutfitChange(\"naked-first undress\"", StringComparison.Ordinal), wear = naked.IndexOf("OutfitWearNamed(\"Naked\"", StringComparison.Ordinal);
        Assert.True(clear > 0 && clear < wear);
        Assert.True(naked.IndexOf("ClearFloorForOutfitChange(\"re-dress\"", StringComparison.Ordinal) < naked.IndexOf("WearOutfitWithHuds(name, keepColor: true)", StringComparison.Ordinal));
        var w = Src("Wander.cs");
        Assert.True(w.IndexOf("ClearFloorForOutfitChange(\"toilet undress\"", StringComparison.Ordinal) < w.IndexOf("await ToiletUndress(ct)", StringComparison.Ordinal));
        Assert.True(w.IndexOf("ClearFloorForOutfitChange(\"tub undress\"", StringComparison.Ordinal) < w.IndexOf("await UndressForSeat(ct)", StringComparison.Ordinal));
        Assert.Contains("ClearFloorForOutfitChange(\"re-dress after seat\"", Src("HomeSeats.cs"));
    }
}
