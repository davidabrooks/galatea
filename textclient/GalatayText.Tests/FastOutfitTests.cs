using System.Text.Json;
using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-09 David: outfit changes he asks for start in ~1 s (FastOutfit.cs), and how/why questions about
/// herself go to the routine instead of a guessed answer. Pure, no network.</summary>
public class FastOutfitTests
{
    static readonly string[] Names =
    {
        "ARTi'S Strapless Top", "ARTi'S Strapless Top shorts", "Bikini", "Bikini top jeans", "Bikini top shorts",
        "PCP Beth Tube Top", "PCP Beth Tube Top shorts", "TETRA Chill T-Shirt", "TETRA Chill T-Shirt shorts",
    };
    static string Api(string content) => JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } });

    [Fact]
    public void Model_outfit_and_helper_fields_parse()
    {
        var r = Program.ParseFastResponse(Api("{\"reply\":\"Sure babe!\",\"action\":true,\"outfit\":\"random\",\"pace\":\"quick\"}"));
        Assert.Equal(("random", true, false), (r.Outfit, r.Action, r.Helper));
        Assert.Null(Program.ParseFastResponse(Api("{\"reply\":\"hi\",\"action\":false,\"outfit\":\"Bikini\"}")).Outfit);   // only with action
        Assert.Null(Program.ParseFastResponse(Api("{\"reply\":\"ok\",\"action\":true,\"outfit\":\"null\"}")).Outfit);
        var h = Program.ParseFastResponse(Api("{\"reply\":\"Let me check, babe\",\"action\":false,\"helper\":true}"));
        Assert.True(h.Helper && h.Action && h.Outfit == null);
        Assert.Equal(Program.FastOutcome.Ack, Program.FastDecide(h, true));        // ack, line left to the routine (not claimed)
        Assert.Equal(Program.FastOutcome.Routine, Program.FastDecide(h, false));
        var bug = Program.ParseFastResponse(Api("{\"reply\":\"Oh no, let me look at that, babe\",\"action\":true,\"helper\":true,\"outfit\":\"random\"}"));
        Assert.Equal((true, true, (string)null), (bug.Helper, bug.Action, bug.Outfit));   // a helper line never changes outfits
        Assert.Equal(Program.FastOutcome.Ack, Program.FastDecide(bug, true));
    }

    [Fact]
    public void Named_requests_resolve_against_the_real_outfit_list()
    {
        var rnd = new Random(1);
        Assert.Equal(("named", "Bikini"), Pick("Bikini", "TETRA Chill T-Shirt", rnd));
        Assert.Equal(("named", "Bikini"), Pick("your bikini", null, rnd));
        Assert.Equal(("named", "TETRA Chill T-Shirt shorts"), Pick("tetra chill t shirt shorts", null, rnd));
        Assert.Equal(("named", "ARTi'S Strapless Top"), Pick("arti's strapless top", null, rnd));
        Assert.Equal(("named", "Bikini top jeans"), Pick("jeans", null, rnd));
        var tee = Pick("t-shirt", "TETRA Chill T-Shirt", rnd);            // two t-shirt outfits, the worn one excluded
        Assert.Equal(("named", "TETRA Chill T-Shirt shorts"), tee);
        Assert.Contains(Pick("shorts", null, rnd).name, Names.Where(n => n.EndsWith("shorts")));
        Assert.Null(Pick("ball gown", null, rnd).kind);
        Assert.Null(Pick("top", null, rnd).kind);                           // too vague (6+ outfits)
        Assert.Null(Program.ResolveFastOutfit("Bikini", Array.Empty<string>(), null, null, rnd).kind);
    }

    [Fact]
    public void Random_never_repeats_the_worn_outfit_or_picks_the_bikini_and_top_color_needs_a_current_outfit()
    {
        for (int i = 0; i < 40; i++)
        {
            var (kind, name, _) = Program.ResolveFastOutfit("random", Names, "PCP Beth Tube Top", null, new Random(i));
            Assert.Equal("random", kind); Assert.NotEqual("PCP Beth Tube Top", name); Assert.NotEqual("Bikini", name);
        }
        var allow = new[] { "PCP Beth Tube Top", "TETRA Chill T-Shirt" };
        Assert.Equal("TETRA Chill T-Shirt", Program.ResolveFastOutfit("random", Names, "PCP Beth Tube Top", allow, new Random(3)).name);
        Assert.Equal(("top_color", "Bikini top shorts"), Two(Program.ResolveFastOutfit("top_color", Names, "Bikini top shorts", null, new Random(1))));
        Assert.Null(Program.ResolveFastOutfit("top_color", Names, null, null, new Random(1)).kind);
    }

    [Fact]
    public void Guards_leave_seated_busy_and_beach_cases_to_the_routine()
    {
        Assert.Null(Program.FastOutfitBlock(true, false, false, false, false, "random"));
        Assert.NotNull(Program.FastOutfitBlock(true, true, false, false, false, "random"));       // seated: furniture/toilet rules
        Assert.NotNull(Program.FastOutfitBlock(true, false, true, false, false, "Bikini"));        // another change running
        Assert.NotNull(Program.FastOutfitBlock(true, false, false, true, false, "Bikini"));        // tub/toilet re-dress pending
        Assert.NotNull(Program.FastOutfitBlock(true, false, false, false, true, "random"));        // beach rules
        Assert.Null(Program.FastOutfitBlock(true, false, false, false, true, "Bikini"));
        Assert.Null(Program.FastOutfitBlock(true, false, false, false, true, "top_color"));
        Assert.NotNull(Program.FastOutfitBlock(false, false, false, false, false, "random"));
    }

    [Fact]
    public void Persona_asks_for_outfit_and_helper_fields()
    {
        string p = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && p == null; d = d.Parent)
            foreach (var f in new[] { Path.Combine(d.FullName, "textclient", "routes", "_fast-chat.json"), Path.Combine(d.FullName, "routes", "_fast-chat.json") })
                if (File.Exists(f)) { p = Program.ParseFastCfg(File.ReadAllText(f)).Persona; break; }
        Assert.NotNull(p);
        Assert.Contains("\"outfit\"", p); Assert.Contains("top_color", p); Assert.Contains("helper", p); Assert.Contains("guesswork", p);
        // problem reports / feedback / new rules: short ack, routine investigates; never reassure or invent a cause
        Assert.Contains("Problem reports, feedback and new rules", p); Assert.Contains("never make up a cause", p);
    }

    [Fact]
    public void Look_into_always_hands_off_to_the_routine()
    {
        Assert.True(Program.FastLookIntoTrigger("Can you look into why you got stuck?"));
        Assert.True(Program.FastLookIntoTrigger("LOOK IN TO the sink thing"));
        Assert.True(Program.FastLookIntoTrigger("please look  into it"));
        Assert.False(Program.FastLookIntoTrigger("look in the bedroom"));
        Assert.False(Program.FastLookIntoTrigger("lookinto"));
        Assert.False(Program.FastLookIntoTrigger("love you"));
        Assert.All(Program.FastLookIntoAcks, a => Assert.InRange(a.Length, 5, 60));
    }

    static (string kind, string name) Pick(string req, string cur, Random r) => Two(Program.ResolveFastOutfit(req, Names, cur, null, r));
    static (string, string) Two((string kind, string name, string why) x) => (x.kind, x.name);
}
