using System.Collections.Generic;
using GalatayText;
using Xunit;
public class FeetPageTests
{
    // live HUD layout 2026-10-21: page tab bars at z 1.988/0.989/-0.011/-1.011/-2.012, feet buttons at -1.21
    [Fact] public void FeetButtonsUseTheirOwnPageTabBar()
    {
        var prims = new List<(string, float)> { ("root", -2.1f), ("f3", -1.210f), ("tabs", -0.011f), ("tabs", -2.012f), ("tabs", -1.011f), ("tabs", 0.989f), ("tabs", 1.988f) };
        Assert.Equal(4, Program.FeetPageTabIndex(prims, -1.210f));
        Assert.Equal(-1, Program.FeetPageTabIndex(new List<(string, float)> { ("tabs", 5f) }, -1.21f));
    }
}
