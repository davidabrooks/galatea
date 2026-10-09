using GalatayText;
using LibreMetaverse;
using Xunit;
using static GalatayText.Program;

namespace GalatayText.Tests;

/// <summary>2026-10-08 18:36-18:39 PT (David): slow leg-alpha removal, slow redress after the toilet, a HUD step for a
/// single-color top, and standing up inside the sink cabinet.</summary>
public class UndressStandOutHudTests
{
    static string RouteFile(string f)
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", f));
        return File.Exists(repo) ? repo : "/workspace/galatea-sl-repo/textclient/routes/" + f;
    }

    [Fact]
    public void Leg_alphas_come_off_with_the_clothing_and_the_bake_goes_before_the_extras()
    {
        var o = UndressOrder(attachments: 1, layers: 3, extras: 2, orphanCheck: true);
        Assert.Equal(new[] { LegStep.LayersOff, LegStep.DetachClothing, LegStep.CofLinksOff, LegStep.RebakeNow, LegStep.ExtrasOn, LegStep.OrphanCheck }, o);
        // one batch each: no step repeats (no per-alpha remove + wait)
        Assert.Equal(o.Count, o.Distinct().Count());
        Assert.True(o.IndexOf(LegStep.RebakeNow) < o.IndexOf(LegStep.ExtrasOn));
        Assert.True(o.IndexOf(LegStep.CofLinksOff) < o.IndexOf(LegStep.RebakeNow));
    }

    [Fact]
    public void No_layers_means_no_bake_and_nothing_means_nothing()
    {
        Assert.DoesNotContain(LegStep.RebakeNow, UndressOrder(1, 0, 0, false));
        Assert.Empty(UndressOrder(0, 0, 0, true));
        Assert.Equal(new[] { LegStep.LayersOff, LegStep.CofLinksOff, LegStep.RebakeNow }, UndressOrder(0, 2, 0, true));
    }

    [Fact]
    public void Layer_link_descriptions_are_numbered_from_memory_without_fetches()
    {
        UUID A(int n) => new($"00000000-0000-0000-0000-{n:D12}");
        var worn = new[] { (A(1), WearableType.Alpha), (A(2), WearableType.Skin), (A(3), WearableType.Alpha) };
        var d = LayerLinkDescs(new[] { (A(10), WearableType.Alpha), (A(11), WearableType.Alpha), (A(12), WearableType.Pants) }, worn);
        Assert.Equal("@1302", d[A(10)]);
        Assert.Equal("@1303", d[A(11)]);
        Assert.Equal("@500", d[A(12)]);
        // an item already counted in the worn list (AddToOutfit ran first) is not counted twice
        Assert.Equal("@1301", LayerLinkDescs(new[] { (A(3), WearableType.Alpha) }, worn)[A(3)]);
    }

    [Fact]
    public void Beth_tube_top_is_single_color_so_its_HUD_is_skipped_but_still_steers_the_shorts()
    {
        var specs = ParseClothingHudSpecs(File.ReadAllText(RouteFile("_clothing-huds.json")));
        var beth = specs.Single(s => s.Hud == new UUID("6899891e-79d1-3a31-93ab-fac14a3bd75e"));
        Assert.True(beth.SingleColor);
        var outfit = new HashSet<UUID> { new("5c8487b7-0db2-34fd-a81b-fe2709c98021") };
        var (press, skip) = PartitionHudSpecs(specs, outfit);
        Assert.Empty(press);
        Assert.Single(skip);
        // multi-color tops still get their HUD step
        Assert.Contains(specs, s => !s.SingleColor && s.HudName.Contains("Strapless"));
    }

    static HomeSeatInfo Seat(UUID id) =>
        ParseHomeSeats(File.ReadAllText(RouteFile("_seats-peronaut-home.json"))).Single(s => s.Id == id);
    static List<FurnitureBox> Boxes() => ParseFurnitureBoxes(File.ReadAllText(RouteFile("_graph-Peronaut.json")));

    [Fact]
    public void Standing_inside_the_sink_cabinet_hops_to_the_centre_line()
    {
        var sink = Seat(BathroomSinkId);
        Assert.Equal(73.8f, sink.StandSpot!.Value.Y, 2);
        var stood = new Vector3(240.6f, 75.7f, 28.9f);  // 18:38:02 live
        Assert.NotNull(InFurniture(stood, Boxes()));
        var (how, to) = StandOutPlan(stood, sink, Boxes());
        Assert.Equal(StandOutMove.Hop, how);
        Assert.Null(InFurniture(to, Boxes()));       // the spot itself is clear floor
        Assert.Equal(StandOutMove.None, StandOutPlan(new Vector3(240.5f, 73.9f, 29f), sink, Boxes()).how);
    }

    [Fact]
    public void Toilet_and_tub_step_to_their_change_spots_and_no_progress_means_hop()
    {
        var toilet = Seat(ToiletSeatId);
        var tub = Seat(new UUID("53f8929b-0abc-6e12-fac9-f9d6f9bfe6bc"));
        var (h1, to1) = StandOutPlan(new Vector3(232.8f, 72.4f, 29f), toilet, Boxes());
        Assert.Equal(StandOutMove.Walk, h1); Assert.Equal(toilet.ChangeSpot!.Value, to1);
        Assert.Equal(StandOutMove.Hop, StandOutPlan(new Vector3(240.6f, 72.0f, 29f), tub, Boxes()).how); // inside the tub box
        foreach (var b in new[] { to1, tub.ChangeSpot!.Value }) Assert.Null(InFurniture(b, Boxes()));
        Assert.True(StandOutNoProgress(new Vector3(240.6f, 75.7f, 29f), new Vector3(240.65f, 75.6f, 29f), new Vector3(240.4f, 73.8f, 29f)));
        Assert.False(StandOutNoProgress(new Vector3(240.6f, 75.7f, 29f), new Vector3(240.4f, 74.1f, 29f), new Vector3(240.4f, 73.8f, 29f)));
    }
}
