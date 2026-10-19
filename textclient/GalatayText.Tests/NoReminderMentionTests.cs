using System; using System.Collections.Generic; using Xunit; using GalatayText;

/// 2026-10-19 (David): never mention reminders unless one is actually pending.
public class NoReminderMentionTests
{
    static readonly DateTime T0 = new(2026, 10, 19, 14, 12, 0);

    [Fact]
    public void Greeting_WithEmptyReminderList_HasNoReminderText()
    {
        foreach (var atLogin in new[] { false, true })
        {
            var t = Program.DavidLoginText(false, atLogin, "galatea_login", T0, new List<(int, string)>()) + Program.AlreadyGreetedNote(T0, 0);
            Assert.DoesNotContain("reminder", t, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("reminder", Program.DavidLoginText(false, atLogin, "friend online", T0, null), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Greeting_WithPendingReminder_ListsIt()
    {
        Assert.Contains("#1: call mom", Program.DavidLoginText(false, true, "galatea_login", T0, new List<(int, string)> { (1, "call mom") }));
        Assert.Contains("reminder", Program.AlreadyGreetedNote(T0, 1));
    }
}
