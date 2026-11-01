using GalatayText;
using Xunit;

// 2026-10-31 17:31 login (#384 live): f3 pressed 17:32:02, fit verified only 17:32:49; the stand check gave up and left the barefoot hover.
// 17:30:51: a stand check during the logout saved -0.001 for f3.
public class FootwearPressHoverTests
{
    static readonly Dictionary<string, double> Saved = new() { ["f3"] = 0.012, ["f1"] = -0.08 };

    [Fact] public void Standing_press_applies_saved_value_for_that_footwear()
        => Assert.Equal(0.012, Program.FootwearPressHover("stand", false, "f3", Saved));
    [Fact] public void Sitting_or_kneeling_press_applies_nothing()
    {
        Assert.Null(Program.FootwearPressHover("sit", false, "f3", Saved));
        Assert.Null(Program.FootwearPressHover("stand", true, "f3", Saved));
        Assert.Null(Program.FootwearPressHover("kneel", false, "f3", Saved));
    }
    [Fact] public void Never_saved_footwear_applies_nothing() => Assert.Null(Program.FootwearPressHover("stand", false, "f5", Saved));

    [Fact] public void Timeout_without_fit_confirm_reapplies_saved_not_barefoot()
        => Assert.True(Program.FootwearTimeoutReapply("f3", "f3", null, -0.080, 0.012));
    [Fact] public void Timeout_after_fit_confirmed_leaves_the_check_in_charge()
        => Assert.False(Program.FootwearTimeoutReapply("f3", "f3", "f3", -0.080, 0.012));
    [Fact] public void Timeout_after_footwear_changed_does_nothing()
        => Assert.False(Program.FootwearTimeoutReapply("f3", "f1", null, -0.080, 0.012));
    [Fact] public void Timeout_with_saved_already_in_force_does_nothing()
        => Assert.False(Program.FootwearTimeoutReapply("f3", "f3", null, 0.012, 0.012));

    [Fact] public void No_footwear_hover_saved_during_logout()
    {
        Assert.NotNull(Program.FootwearHoverSaveBlock(true));
        Assert.Null(Program.FootwearHoverSaveBlock(false));
    }
}
