using GalatayText;
using LibreMetaverse;
using Xunit;
using static GalatayText.Program;

namespace GalatayText.Tests;

/// <summary>2026-10-08 ~18:58 PT (David: "Yes"): skip the ~20 s colour re-check when re-dressing the same outfit, and
/// batch + bake right away on every outfit wear (the bikini change in/out included).</summary>
public class RedressSkipHudTests
{
    static UUID U(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    [Fact]
    public void Same_items_with_a_remembered_colour_skip_the_HUD()
    {
        var shirt = new[] { U(1) };
        Assert.NotNull(RedressHudSkipReason(true, "cotton 2 blue", new[] { U(1), U(2) }, shirt, new HashSet<UUID> { U(1), U(9) }));
    }

    [Fact]
    public void Real_switch_unknown_colour_fresh_copy_or_not_worn_run_the_HUD()
    {
        var rec = new[] { U(1) };
        var worn = new HashSet<UUID> { U(1), U(5) };
        Assert.Null(RedressHudSkipReason(false, "D6", rec, new[] { U(1) }, worn));          // new random colour
        Assert.Null(RedressHudSkipReason(true, null, rec, new[] { U(1) }, worn));           // nothing remembered
        Assert.Null(RedressHudSkipReason(true, "D6", new UUID[0], new[] { U(1) }, worn));    // no record yet: learn once
        Assert.Null(RedressHudSkipReason(true, "D6", rec, new[] { U(5) }, worn));            // different item id (fresh copy)
        Assert.Null(RedressHudSkipReason(true, "D6", rec, new[] { U(1) }, new HashSet<UUID> { U(5) })); // not worn
    }

    [Fact]
    public void A_new_pick_replaces_the_record_and_a_kept_colour_adds_to_it()
    {
        Assert.Equal(new[] { U(3) }, HudColouredItemsAfter(new[] { U(1), U(2) }, new[] { U(3) }, newPick: true));
        Assert.Equal(new[] { U(1), U(2) }, HudColouredItemsAfter(new[] { U(1) }, new[] { U(2), U(1) }, newPick: false));
        Assert.Equal(new[] { U(1), U(2) }, ParseItemList(FormatItemList(new[] { U(1), U(2) }) + ",junk,"));
    }

    [Fact]
    public void Outfit_wear_batches_and_bakes_before_the_new_attachments()
    {
        var o = OutfitSwapOrder(detach: 2, layersOff: 3, bodyParts: 4, layersOn: 0, attach: 2);
        Assert.Equal(new[] { SwapStep.DetachAll, SwapStep.LayersOff, SwapStep.BodyOn, SwapStep.CofLinksOff, SwapStep.RebakeNow, SwapStep.AttachAll }, o);
        Assert.Equal(o.Count, o.Distinct().Count()); // one batch each, no per-layer step
        var back = OutfitSwapOrder(2, 0, 4, 3, 2);   // bikini off: leg alphas back on, linked in one batch, baked at once
        Assert.True(back.IndexOf(SwapStep.LayersOn) < back.IndexOf(SwapStep.CofLinksOn));
        Assert.True(back.IndexOf(SwapStep.CofLinksOn) < back.IndexOf(SwapStep.RebakeNow));
        Assert.DoesNotContain(SwapStep.RebakeNow, OutfitSwapOrder(1, 0, 0, 0, 1)); // attachments only: no layer bake
    }
}
