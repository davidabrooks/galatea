using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-08 08:13 "You greeted me twice again": a plain say answers recent unanswered nearby lines, so the
/// chat routine's later 'say --re' on them is refused.</summary>
public class PlainSayClaimTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 8, 8, 13, 24, TimeSpan.FromHours(-7));

    [Fact]
    public void Plain_say_claims_recent_unanswered_lines_then_re_is_refused()
    {
        var all = new List<Program.InMsg>
        {
            new() { Id = 1, T = Now.AddMinutes(-10), Text = "old line" },
            new() { Id = 2, T = Now.AddSeconds(-30), Text = "earlier", AnsweredAt = Now.AddSeconds(-20), AnsweredBy = "hi", Explicit = true },
            new() { Id = 3, T = Now.AddSeconds(-18), Text = "good morning, babe. let's test your wandering" },
        };
        var claim = Program.PlainSayClaimIds(all, Now, TimeSpan.FromMinutes(2));
        Assert.Equal(new long[] { 3 }, claim);                            // not the 10 min old line, not the answered one
        Assert.Null(Program.ChatReClaimCheck(all, new long[] { 3 }));      // before the plain say: --re would go out
        foreach (var m in all.Where(m => claim.Contains(m.Id))) { m.AnsweredAt = Now; m.AnsweredBy = "Good morning, love!"; }
        var why = Program.ChatReClaimCheck(all, new long[] { 3 });
        Assert.NotNull(why);
        Assert.Contains("already answered", why);
        Assert.Empty(Program.PlainSayClaimIds(all, Now.AddSeconds(10), TimeSpan.FromMinutes(2)));
    }
}
