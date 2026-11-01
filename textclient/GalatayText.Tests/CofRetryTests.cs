using GalatayText;
using Xunit;

// 2026-10-31 16:40:18 relog: "usual outfit: Current Outfit folder not found" at login left the old jeans and tube top linked and worn.
public class CofRetryTests
{
    static Func<int, CancellationToken, Task> NoWait => (_, _) => Task.CompletedTask;

    [Fact] public async Task FolderMissingEarlyInLogin_IsRetriedUntilTheDropWorks()
    {
        int calls = 0;
        var r = await CofRetry.Run(_ => Task.FromResult(++calls < 4 ? "Current Outfit folder not found" : "2 COF link(s) removed in one batch"),
            CancellationToken.None, delay: NoWait);
        Assert.Equal(4, calls); Assert.StartsWith("2 COF link(s) removed", r); Assert.Contains("after 4 tries", r);
    }
    [Fact] public async Task OtherResults_AreNotRetried()
    {
        int calls = 0;
        var r = await CofRetry.Run(_ => { calls++; return Task.FromResult("COF batch removal FAILED: boom"); }, CancellationToken.None, delay: NoWait);
        Assert.Equal(1, calls); Assert.Equal("COF batch removal FAILED: boom", r);
    }
    [Fact] public async Task StillMissing_GivesUpAfterTheTries()
    {
        int calls = 0;
        var r = await CofRetry.Run(_ => { calls++; return Task.FromResult("Current Outfit folder not found"); }, CancellationToken.None, tries: 5, delay: NoWait);
        Assert.Equal(5, calls); Assert.Contains("gave up after 5 tries", r);
    }
}
