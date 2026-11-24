using Xunit;

/// <summary>2026-11-23: the opt-in slow tier (real speech model, minutes of runtime). Skipped unless GALATEA_SLOW_TESTS=1,
/// so CI and the default galatea-test.sh run stay fast; `GALATEA_SLOW_TESTS=1 galatea-test.sh --csharp` runs them.</summary>
public sealed class SlowFactAttribute : FactAttribute
{
    public SlowFactAttribute()
    {
        if (System.Environment.GetEnvironmentVariable("GALATEA_SLOW_TESTS") != "1")
            Skip = "slow tier: set GALATEA_SLOW_TESTS=1 to run";
    }
}
