using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-18: visitor fast-reply guards (FastGuard.cs), after fast chat answered ~40 crowd greetings in a club and got her banned.</summary>
public class FastChatGuardTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
    static Program.FastCfg C() => new();
    static string Pre(Program.FastGuardState st, string text, string who = "v1", bool im = false, int near = 20, bool routine = false, DateTimeOffset? own = null, double dt = 0) =>
        Program.FastGuardPre(C(), st, who, text, im, near, routine, own, T0.AddSeconds(dt), out _);
    static string Fin(Program.FastGuardState st, string reply, string who = "v1", bool im = false, int near = 20, string last = null, double dt = 0) =>
        Program.FastGuardFinal(C(), st, who, reply, im, near, last, T0.AddSeconds(dt));

    [Theory]
    [InlineData("Gala, how are you?")] [InlineData("hi galatay")] [InlineData("What's your name Galatea?")] [InlineData("I love your hair gala!")]
    public void Lines_naming_her_are_addressed(string t) => Assert.Null(Pre(new(), t));

    [Theory]
    [InlineData("hi everyone")] [InlineData("HAPPY SUNDAY!")] [InlineData("Hello all, welcome back")] [InlineData("good evening guys")]
    [InlineData("Nele, you look great")] [InlineData("hi Nele")] [InlineData("@Sheluxe hello!")] [InlineData("lol that was funny")] [InlineData("who's the DJ?")]
    [InlineData("hi Nele, Sheluxe, Gala")] [InlineData("Hello Nele Sheluxe Gala")]
    public void Crowd_lines_and_lines_for_others_are_never_answered(string t) => Assert.NotNull(Pre(new(), t));

    [Theory]
    [InlineData("😊😊")] [InlineData("<3")] [InlineData("/me waves")] [InlineData("*hugs*")] [InlineData("   ")] [InlineData("...")]
    public void Emotes_and_emoji_only_are_skipped_even_in_an_IM(string t) { Assert.NotNull(Pre(new(), t)); Assert.NotNull(Pre(new(), t, im: true)); }

    [Fact]
    public void IMs_are_addressed_and_a_direct_reply_within_60_s_counts()
    {
        Assert.Null(Pre(new(), "what are you up to", im: true));
        var st = new Program.FastGuardState();
        Assert.NotNull(Pre(st, "nice weather today"));                         // she never spoke to them
        Assert.Null(Fin(st, "Hi there!", dt: 0));                               // she replied to v1 at t=0
        Assert.Null(Pre(st, "thanks, I'm from Spain", dt: 30));                 // direct answer in 60 s
        Assert.NotNull(Pre(st, "thanks, I'm from Spain", dt: 90));              // too late
        Assert.NotNull(Pre(st, "hi everyone", dt: 30));                         // a crowd greeting is still not a reply
        Assert.NotNull(Pre(st, "Nele, did you see that?", dt: 30));             // addressed to someone else
        Assert.NotNull(Pre(st, "thanks!", who: "v2", dt: 30));                  // someone else's line
        Assert.Null(Pre(new(), "thanks!", who: "v3", routine: true));           // the routine answered them with 'say --re'
    }

    [Fact]
    public void A_question_with_you_counts_only_when_few_avatars_are_around()
    {
        Assert.Null(Pre(new(), "are you new here?", near: 2));
        Assert.NotNull(Pre(new(), "are you new here?", near: 40));
        Assert.NotNull(Pre(new(), "you are nice", near: 2));
    }

    [Fact]
    public void Near_identical_lines_are_detected()
    {
        Assert.True(Program.FastNearIdentical("Sorry about that. 🧡", "sorry about that"));
        Assert.True(Program.FastNearIdentical("Hi Nele! 🥰", "hi nele"));
        Assert.True(Program.FastNearIdentical("Good music and good company", "Good music and good company, Nele!"));
        Assert.False(Program.FastNearIdentical("Hi Nele!", "Hi Sheluxe!"));
        Assert.False(Program.FastNearIdentical("Thanks!", ""));
    }

    [Fact]
    public void No_repeat_in_a_row_and_a_per_person_10_min_window()
    {
        var st = new Program.FastGuardState();
        Assert.Null(Fin(st, "Thanks, that is kind!", who: "a", dt: 0));
        Assert.NotNull(Fin(st, "thanks that is kind", who: "a", dt: 100));                 // same person, within 10 min
        Assert.Null(Fin(st, "Thanks, that is kind!", who: "a", dt: 700, last: "Something else")); // window passed
        Assert.NotNull(Fin(new(), "Hello there friend", who: "b", last: "hello there friend!")); // same as my last nearby line
        Assert.Null(Fin(new(), "Hello there friend", who: "b", im: true, last: "hello there friend!")); // IM: last nearby line is irrelevant
    }

    [Fact]
    public void Nearby_cap_is_6_a_minute_and_3_in_a_busy_area()
    {
        var st = new Program.FastGuardState();
        for (int i = 0; i < 3; i++) Assert.Null(Fin(st, "reply number " + new string('x', i + 1), who: "p" + i, near: 30, dt: i));
        Assert.Contains("cap 3/min", Fin(st, "another different one", who: "p9", near: 30, dt: 4));
        Assert.Null(Fin(st, "after a minute passes", who: "p9", near: 30, dt: 70));
        var calm = new Program.FastGuardState();
        for (int i = 0; i < 6; i++) Assert.Null(Fin(calm, "calm reply " + (char)('a' + i) + "q" + new string('z', i), who: "c" + i, near: 5, dt: i));
        Assert.Contains("cap 6/min", Fin(calm, "one more distinct line here", who: "c9", near: 5, dt: 7));
        Assert.Null(Fin(new(), "IMs are not capped", im: true, who: "z"));
        var imSt = new Program.FastGuardState();
        for (int i = 0; i < 10; i++) Assert.Null(Fin(imSt, "im line " + new string('k', i + 1), im: true, who: "i" + i, dt: i));
    }

    [Fact]
    public void Only_one_apology_per_window_in_nearby_chat()
    {
        var st = new Program.FastGuardState();
        Assert.Null(Fin(st, "Sorry about that. 🧡", who: "a", dt: 0));
        Assert.Equal("already apologised recently", Fin(st, "Oops, my bad, sorry!", who: "b", dt: 60));
        Assert.Equal("already apologised recently", Fin(st, "I apologize for the noise", who: "c", dt: 120));
        Assert.Null(Fin(st, "Oops, my bad, sorry!", who: "b", im: true, dt: 130));       // IMs unaffected
        Assert.Null(Fin(st, "Sorry again, the spam was too much", who: "d", dt: 31 * 60));
    }

    [Theory]
    [InlineData("you need to tell David to stop spamming local chat")] [InlineData("stop chatting please")] [InlineData("please stop")] [InlineData("STOP SPAM")]
    [InlineData("shut up already")] [InlineData("stfu bot")]
    public void Stop_and_spam_requests_are_complaints(string t) => Assert.True(Program.FastIsComplaint(t));

    [Theory]
    [InlineData("don't stop the music")] [InlineData("love this song")] [InlineData("hi Gala")]
    public void Normal_lines_are_not_complaints(string t) => Assert.False(Program.FastIsComplaint(t));

    [Fact]
    public void A_complaint_gets_one_reply_then_15_minutes_of_nearby_silence()
    {
        var st = new Program.FastGuardState();
        Assert.Null(Program.FastGuardPre(C(), st, "h", "Gala stop spamming local chat", false, 30, false, null, T0, out var complaint));
        Assert.True(complaint);                                                       // this one line may get a short reply
        Assert.Contains("silent", Pre(st, "Gala, what do you mean?", who: "x", dt: 5));   // everything nearby is refused now
        Assert.Contains("silent", Pre(st, "Gala stop it", who: "h", dt: 20));          // a second complaint gets nothing
        Assert.Contains("silent", Pre(st, "Gala hi", who: "y", dt: 14 * 60));
        Assert.Null(Pre(st, "Gala, are you there?", who: "y", dt: 16 * 60));           // after 15 min nearby is allowed again
        Assert.Null(Pre(st, "hello, a question for you", who: "x", im: true, dt: 5));   // IMs still ok
    }

    [Fact]
    public void An_unaddressed_complaint_counts_only_right_after_she_spoke_and_IM_complaints_get_one_reply_each()
    {
        var spoke = T0.AddSeconds(-30);
        Assert.Null(Program.FastGuardPre(C(), new(), "h", "someone make it stop spamming", false, 30, false, spoke, T0, out var c1)); Assert.True(c1);
        Assert.NotNull(Pre(new(), "someone make it stop spamming", own: T0.AddMinutes(-10)));   // she has been quiet: not about her
        var st = new Program.FastGuardState();
        Assert.Null(Program.FastGuardPre(C(), st, "i", "please stop spamming the chat", true, 0, false, null, T0, out var c2)); Assert.True(c2);
        Assert.Equal("stop request already answered once", Program.FastGuardPre(C(), st, "i", "stop it!", true, 0, false, null, T0.AddSeconds(10), out _));
        Assert.Contains("silent", Pre(st, "Gala hello", dt: 20));                                // an IM complaint silences nearby as well
    }

    [Fact]
    public void Config_has_visitors_off_guards_and_the_persona_matches()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "routes", "_fast-chat.json");
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "textclient", "routes", "_fast-chat.json"))) { path = Path.Combine(d.FullName, "textclient", "routes", "_fast-chat.json"); break; }
        var c = Program.ParseFastCfg(File.ReadAllText(path));
        Assert.NotNull(c);
        Assert.False(c.Visitors);
        Assert.Equal((60.0, 600.0, 15, 3, 15, 30), (c.GuardReplyWindowS, c.GuardDedupeWindowS, c.GuardBusyAvatars, c.GuardBusyMaxPerMin, c.GuardStopSilenceMin, c.GuardApologyWindowMin));
        Assert.Contains("crowd greetings", c.Persona); Assert.Contains("say sorry at most once", c.Persona); Assert.Contains("15 minutes", c.Persona);
        Assert.Equal(60, Program.ParseFastCfg("{\"persona\":\"x\",\"guards\":{\"reply_window_s\":9999}}").GuardReplyWindowS == 600 ? 60 : 0);   // out-of-range values are clamped
    }
}
