using GalatayText;
using Xunit;

public class CollarLogoutStealthTests
{
    // 16:24:53 logout: About answered ~1 s after the press; the 1 s wait gave up just before it
    [Fact] public void CollarWait_ComesFromObservedAnswers_AndCoversTheOneSecondAboutPage()
    {
        Assert.Equal(Program.CollarWaitDefaultMs, Program.CollarWaitMs(null));
        Assert.Equal(Program.CollarWaitDefaultMs, Program.CollarWaitMs(new[] { 900, 1000 }));
        Assert.True(Program.CollarWaitDefaultMs > 1000 + 500);
        Assert.Equal(1800, Program.CollarWaitMs(new[] { 300, 1200, 900 }));
        Assert.Equal(Program.CollarWaitMinMs, Program.CollarWaitMs(new[] { 100, 200, 250 }));
        Assert.Equal(Program.CollarWaitMaxMs, Program.CollarWaitMs(new[] { 100, 7000, 250 }));
    }
    [Fact] public void LogoutBudget_FitsEveryTryButStaysCapped()
    {
        Assert.True(LoginVeil.CollarLogoutAboutTries >= 3);
        Assert.Equal((LoginVeil.CollarLogoutTouches + LoginVeil.CollarLogoutAboutTries + 1) * 1500, LoginVeil.CollarLogoutBudgetMs(1500));
        Assert.Equal(LoginVeil.CollarStealthMaxMs, LoginVeil.CollarLogoutBudgetMs(Program.CollarWaitMaxMs));
    }
    // 15:51:32 the collar_stealth command turned Stealth on; the 16:24 logout said "was unknown"
    [Fact] public void LastSeenState_FillsInWhenTheMenuDoesNotOpen()
    {
        var seen = new Program.CollarSeen(true, DateTime.Now, "command (pressed)");
        Assert.True(Program.CollarWasOn(null, seen));
        Assert.False(Program.CollarWasOn(false, seen));     // a page read now wins
        Assert.Null(Program.CollarWasOn(null, null));
        // hidden before, menu failed: nothing to show at login
        Assert.Equal("keep", LoginVeil.CollarMarker(Program.CollarWasOn(null, seen), "collar About menu did not open"));
    }
    [Fact] public void LogoutReport_IsLoudWhenStealthIsNotConfirmed()
    {
        Assert.True(LoginVeil.CollarLogoutReport("collar now invisible (Stealth on)", true).confirmed);
        Assert.True(LoginVeil.CollarLogoutReport("collar already invisible (Stealth on)", true).confirmed);
        var (ok, line) = LoginVeil.CollarLogoutReport("collar About menu did not open (3 tries of 2500 ms)", false);
        Assert.False(ok); Assert.StartsWith("WARNING", line); Assert.Contains("visible", line);
        Assert.Contains("never seen", LoginVeil.CollarLogoutReport(null, null).line);
    }
}
