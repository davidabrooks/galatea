using GalatayText;
using Xunit;

public class HdAvatarWaitTests
{
    [Fact]
    public void Hd_stall_window_is_at_least_10s_and_mostly_dressed_rule()   // 2026-10-13 20:05: David 15/16 after 3 s
    {
        Assert.Equal(3000, Program.LookStallMs(1, 1, 10000));
        Assert.Equal(10000, Program.LookStallMs(1, 1, 10000, true));
        Assert.Equal(12000, Program.LookStallMs(1, 1, 12000, true));
        Assert.True(Program.Program_MostlyDressed(16, 15));
        Assert.False(Program.Program_MostlyDressed(16, 13));
        Assert.False(Program.Program_MostlyDressed(3, 2));
        Assert.False(Program.Program_MostlyDressed(0, 0));
    }
}
