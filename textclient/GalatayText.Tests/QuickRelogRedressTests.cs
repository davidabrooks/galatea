using GalatayText;
using Xunit;

// 2026-11-03 16:25 / 18:03 PT (David): a quick naked relog dressed her again on the logout stand. run/quick-relog holds back that one re-dress.
public class QuickRelogRedressTests
{
    [Fact] public void QuickRelog_Stand_DoesNotRedress()
    {
        var (due, why) = Program.NakedRedressDue("Black dress", false, false, false, 99, false, quickRelog: true);
        Assert.False(due); Assert.Contains("quick relog", why);
    }

    [Fact] public void NormalStand_RedressesAsBefore()
    {
        Assert.Equal((true, "stood up"), Program.NakedRedressDue("Black dress", false, false, false, 99, false));
        Assert.Equal((true, "stood up"), Program.NakedRedressDue("Black dress", false, false, false, 99, false, quickRelog: false));
    }

    [Fact] public void Flag_SetByLogoutQuick_ClearedAfterLogin()
    {
        var p = Path.Combine(Path.GetTempPath(), "quick-relog-" + Guid.NewGuid());
        Assert.False(Program.QuickRelogSet(p));
        Program.QuickRelogMark(p, "test");
        Assert.True(Program.QuickRelogSet(p));
        Assert.True(Program.QuickRelogClear(p));   // the login step
        Assert.False(Program.QuickRelogSet(p));
        Assert.False(Program.QuickRelogClear(p));  // nothing to clear: a normal login
        var (due, _) = Program.NakedRedressDue("Black dress", false, false, false, 99, false, Program.QuickRelogSet(p));
        Assert.True(due);                          // after the login a stand re-dresses again
    }

    [Fact] public void Wiring_LogoutQuickMarks_LoginClears_BothRedressPathsRespectIt()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent;
        var dir = Path.Combine(d!.FullName, "textclient/galatay-text");
        var prog = File.ReadAllText(Path.Combine(dir, "Program.cs")); var ar = File.ReadAllText(Path.Combine(dir, "AdultRedress.cs"));
        Assert.Contains("QuickRelogMark(QuickRelogPath, \"logout quick\")", prog);
        Assert.True(prog.IndexOf("QuickRelogClear(QuickRelogPath)") is > 0 and var c && c < prog.IndexOf("_ = Task.Run(AfterQuickLogin)"));
        Assert.Contains("beachOutfitBusy, QuickRelogSet(QuickRelogPath))", ar);                 // the stand tick
        // 2026-11-04: wander's before-stand re-dress is gone (she stands as she is), so the tick is now the only place that dresses her
        // after a stand, and it is the one that must still respect the quick-relog flag
        Assert.DoesNotContain("NakedRedressBeforeStand", File.ReadAllText(Path.Combine(dir, "Wander.cs")));
        Assert.Contains("QuickRelogSet(QuickRelogPath)", ar);
    }
}
