using Xunit;
using GalatayText;

namespace GalatayText.Tests;

/// <summary>2026-10-31 18:26: the sim's appearance hover (-0.08) was inside her agent z; the hover check env now hands it to the renderer to take out.</summary>
public class SimHoverEnvTests
{
    [Fact]
    public void Sim_hover_is_passed_to_the_renderer()
        => Assert.Equal("-0.080", Hover.HoverCheckEnv(1.572, 0.012, null, -0.08)["GT_SIM_HOVER"]);

    [Fact]
    public void Default_is_zero()
        => Assert.Equal("0.000", Hover.HoverCheckEnv(1.572, 0.012)["GT_SIM_HOVER"]);
}
