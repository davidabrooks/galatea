using System.Text.Json;
using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-06 Peronaut doorway routing + solo pose / double-greet pure checks.</summary>
public class PeronautDoorwayAndPoseGreetTests
{
    static string GraphPath()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", "_graph-Peronaut.json"));
        if (File.Exists(repo)) return repo;
        var live = "/home/box/viewers/textclient/routes/_graph-Peronaut.json";
        if (File.Exists(live)) return live;
        var ws = "/workspace/galatea-sl-repo/textclient/routes/_graph-Peronaut.json";
        return ws;
    }

    [Fact]
    public void Peronaut_doorway_graph_routes_bed_and_east_deck_through_doors()
    {
        var (ok, detail) = Program.PeronautDoorwaySelfTest(GraphPath());
        Assert.True(ok, detail);
    }

    [Fact]
    public void OwnChatLooksLikeGreeting_login_hi_vs_ordinary()
    {
        Assert.True(Program.OwnChatLooksLikeGreeting("Hi David! I just popped in and found you already here. Good to see you, love."));
        Assert.True(Program.OwnChatLooksLikeGreeting("Hello there"));
        Assert.True(Program.OwnChatLooksLikeGreeting("hey love"));
        Assert.False(Program.OwnChatLooksLikeGreeting("What are we up to tonight?"));
        Assert.False(Program.OwnChatLooksLikeGreeting("sitting on the bed"));
    }

    [Fact]
    public void Graph_json_has_no_direct_inside_to_wing_edges()
    {
        var json = File.ReadAllText(GraphPath());
        using var doc = JsonDocument.Parse(json);
        var places = doc.RootElement.GetProperty("places");
        int iw = places.GetProperty("inside-west").GetProperty("node").GetInt32();
        int ie = places.GetProperty("inside-east").GetProperty("node").GetInt32();
        int bed = places.GetProperty("bed").GetProperty("node").GetInt32();
        int ed = places.GetProperty("east-deck").GetProperty("node").GetInt32();
        foreach (var e in doc.RootElement.GetProperty("edges").EnumerateArray())
        {
            int a = e[0].GetInt32(), b = e[1].GetInt32();
            Assert.False((a == iw && b == bed) || (a == bed && b == iw), "direct inside-west <-> bed edge");
            Assert.False((a == ie && b == ed) || (a == ed && b == ie), "direct inside-east <-> east-deck edge");
        }
        bool westDoor = false, eastDoor = false;
        foreach (var e in doc.RootElement.GetProperty("edges").EnumerateArray())
        {
            var kind = e[2].GetString() ?? "";
            if (kind.Contains("west-doorway", StringComparison.OrdinalIgnoreCase)) westDoor = true;
            if (kind.Contains("east-doorway", StringComparison.OrdinalIgnoreCase)) eastDoor = true;
        }
        Assert.True(westDoor && eastDoor);
    }
}
