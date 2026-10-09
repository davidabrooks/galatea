using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>libremetaverse-seed-retry.patch: seed capability retry is a capped backoff, never an unbounded loop.</summary>
public class SeedRetryTests
{
    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 2000)]
    [InlineData(3, 4000)]
    [InlineData(5, 16000)]
    [InlineData(6, 30000)]
    [InlineData(50, 30000)]
    [InlineData(0, 1000)]
    public void DelayBacksOffAndCaps(int attempt, int ms) => Assert.Equal(ms, Caps.SeedRetryDelayMs(attempt));

    [Fact]
    public void RetryStopsAtCapOnCancelOrNotFound()
    {
        Assert.True(Caps.ShouldRetrySeed(1, false, false));
        Assert.True(Caps.ShouldRetrySeed(Caps.SeedMaxAttempts - 1, false, false));
        Assert.False(Caps.ShouldRetrySeed(Caps.SeedMaxAttempts, false, false));
        Assert.False(Caps.ShouldRetrySeed(1, true, false));
        Assert.False(Caps.ShouldRetrySeed(1, false, true));
    }

    [Fact]
    public void WorstCaseTotalWaitIsBounded()
    {
        int total = 0;
        for (int a = 1; Caps.ShouldRetrySeed(a, false, false); a++) total += Caps.SeedRetryDelayMs(a);
        Assert.InRange(total, 1, 120000);
    }
}
