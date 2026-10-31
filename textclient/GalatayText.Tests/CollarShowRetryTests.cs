using Xunit;
namespace GalatayText.Tests;

// 2026-10-30 17:34:04 login: "collar menu did not open (no single worn attachment matches; worn: ?, ...)": collar left invisible.
public class CollarShowRetryTests
{
    [Fact] public void NameMiss_IsRetried_WithinTries()
    {
        Assert.True(Program.CollarTouchRetry("no single worn attachment matches; worn: ?, ?", 1));
        Assert.False(Program.CollarTouchRetry("no single worn attachment matches; worn: ?", Program.CollarTouchTries));
        Assert.False(Program.CollarTouchRetry("touched ':: No 9 :: Premium Collar' link 1", 1));
    }
}
