using Xunit;
using GalatayText;

public class CollarPauseTests
{
    [Fact] public void PausedDropsCollar() => Assert.DoesNotContain(Program.N9CollarItem, Program.AlwaysWearIdsFrom(null, collarPaused: true));
    [Fact] public void PausedDropsCollarEvenIfListed() => Assert.DoesNotContain(Program.N9CollarItem, Program.AlwaysWearIdsFrom(Program.N9CollarItem + " collar", true));
    [Fact] public void NotPausedKeepsCollar() => Assert.Contains(Program.N9CollarItem, Program.AlwaysWearIdsFrom(null));
}
