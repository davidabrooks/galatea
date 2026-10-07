using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-07 home wander: uniform target/seat picks, no repeat of the last 2.</summary>
public class WanderUniformPickTests
{
    [Fact]
    public void PickFresh_is_uniform_and_never_repeats_last_two()
    {
        var cands = new[] { "front", "chairs", "patio-sw", "living", "home", "patio-east", "east-deck", "porch" };
        var r = new Random(1234); var recent = new List<string>(); var seq = new List<string>();
        var counts = cands.ToDictionary(c => c, _ => 0);
        const int n = 16000;
        for (int i = 0; i < n; i++) { var p = Program.PickFresh(cands, recent, r); seq.Add(p); counts[p]++; }
        for (int i = 2; i < n; i++) Assert.True(seq[i] != seq[i - 1] && seq[i] != seq[i - 2], $"repeat at {i}");
        double exp = n / (double)cands.Length;
        foreach (var kv in counts) Assert.InRange(kv.Value, exp * 0.9, exp * 1.1);
    }

    [Fact]
    public void PickFresh_small_pools_fall_back()
    {
        var r = new Random(7); var recent = new List<string>();
        Assert.Null(Program.PickFresh(Array.Empty<string>(), recent, r));
        Assert.Equal("a", Program.PickFresh(new[] { "a" }, recent, r));
        Assert.Equal("a", Program.PickFresh(new[] { "a" }, recent, r));
        var two = new[] { "a", "b" }; var last = "a";
        for (int i = 0; i < 50; i++) { var p = Program.PickFresh(two, recent, r); Assert.NotEqual(last, p); last = p; }
    }
}

public class ReileyChairGroupTests
{
    [Fact]
    public void Reiley_net_chairs_share_one_seat_group()
    {
        var p = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", "_seats-peronaut-home.json"));
        if (!File.Exists(p)) p = "/workspace/galatea-sl-repo/textclient/routes/_seats-peronaut-home.json";
        var t = File.ReadAllText(p);
        foreach (var id in new[] { "7beb04ac-b4cc-8e7d-98b2-d5a4683afcbc", "cab3241e-97e4-4ad6-f795-008180ea2d74" })
        {
            var line = t.Split('\n').Single(l => l.Contains(id));
            Assert.Contains("\"group\": \"reiley-chairs\"", line);
        }
    }
}
