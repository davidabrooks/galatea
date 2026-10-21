using LibreMetaverse.StructuredData;
using Xunit;
using GalatayText;

public class ForceRebakeTests
{
    [Fact] public void SuccessNoRetry() => Assert.Null(Program.RebakeRetryVersion(new OSDMap { ["success"] = true }, 5));
    [Fact] public void ExpectedRetries() => Assert.Equal(7, Program.RebakeRetryVersion(new OSDMap { ["success"] = false, ["expected"] = 7 }, 5));
    [Fact] public void SameExpectedStops() => Assert.Null(Program.RebakeRetryVersion(new OSDMap { ["success"] = false, ["expected"] = 5 }, 5));
    [Fact] public void ErrorWithoutExpectedStops() => Assert.Null(Program.RebakeRetryVersion(new OSDMap { ["success"] = false, ["error"] = "x" }, 5));
}
