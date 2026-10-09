using GalatayMcp;
using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-08 19:08 David: "There was a long delay before you answered me in nearby chat." Instant replies for a
/// few clear requests (routes/_instant-replies.json), claimed like 'say --re'; David's lines skip the long webhook batching.</summary>
public class InstantReplyTests
{
    static Program.InstantCfg Cfg()
    {
        var dir = Environment.GetEnvironmentVariable("GT_TEST_ROUTE_DIR");
        if (string.IsNullOrEmpty(dir))
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "textclient", "routes", "_instant-replies.json"))) { dir = Path.Combine(d.FullName, "textclient", "routes"); break; }
                else if (File.Exists(Path.Combine(d.FullName, "routes", "_instant-replies.json"))) { dir = Path.Combine(d.FullName, "routes"); break; }
        var c = Program.ParseInstantCfg(File.ReadAllText(Path.Combine(dir!, "_instant-replies.json")));
        Assert.NotNull(c);
        return c;
    }

    [Theory]
    [InlineData("start wandering!", "wander_start")]
    [InlineData("Start wandering", "wander_start")]
    [InlineData("ok babe, keep wandering", "wander_start")]
    [InlineData("You can continue wandering now", "wander_start")]
    [InlineData("go back to wandering please 😊", "wander_start")]
    [InlineData("Stop wandering", "wander_stop")]
    [InlineData("babe stop wandering for now", "wander_stop")]
    [InlineData("Come here", "follow")]
    [InlineData("follow me babe", "follow")]
    [InlineData("Galatea, come with me", "follow")]
    [InlineData("stop following me", "follow_stop")]
    [InlineData("ok stay here", "follow_stop")]
    public void Clear_requests_match(string text, string action) =>
        Assert.Equal(action, Program.MatchInstant(Cfg(), text)?.Action);

    [Theory]
    [InlineData("Did you stop wandering?")]
    [InlineData("can you start wandering?")]
    [InlineData("Don't stop wandering")]
    [InlineData("I love watching you wander around the house")]
    [InlineData("come here and sit with me on the sofa")]
    [InlineData("good morning babe")]
    [InlineData("ok")]
    [InlineData("babe")]
    [InlineData("/me smiles and says come here")]
    [InlineData("stop wandering and go put your bikini on, then meet me at the beach by the pier")]
    [InlineData("wandering is fun")]
    [InlineData("")]
    public void Chatty_or_ambiguous_lines_go_to_the_routine(string text) =>
        Assert.Null(Program.MatchInstant(Cfg(), text));

    [Fact]
    public void Disabled_config_matches_nothing()
    {
        var c = Cfg(); c.Enabled = false;
        Assert.Null(Program.MatchInstant(c, "start wandering"));
        Assert.Null(Program.MatchInstant(Program.ParseInstantCfg("{ not json"), "start wandering"));
    }

    [Fact]
    public void Every_command_has_varied_replies()
    {
        foreach (var c in Cfg().Commands) Assert.True(c.Replies.Count >= 3, c.Action);
    }

    [Fact]
    public void Login_greeting_is_folded_in_only_until_I_have_spoken()
    {
        var login = new DateTime(2026, 10, 8, 2, 0, 0, DateTimeKind.Utc);
        var now = login.AddSeconds(40);
        var w = TimeSpan.FromMinutes(15);
        Assert.True(Program.NeedsLoginGreet(now, login, DateTime.MinValue, null, null, w));
        Assert.False(Program.NeedsLoginGreet(now, login, DateTime.MinValue, new DateTimeOffset(login.AddSeconds(20)), null, w)); // already said hi
        Assert.False(Program.NeedsLoginGreet(now, login, DateTime.MinValue, null, new DateTimeOffset(login.AddSeconds(5)), w));  // IMed him
        Assert.True(Program.NeedsLoginGreet(now, login, DateTime.MinValue, new DateTimeOffset(login.AddMinutes(-30)), null, w)); // old session
        Assert.False(Program.NeedsLoginGreet(login.AddMinutes(20), login, DateTime.MinValue, null, null, w));                    // long ago
        var c = new Program.InstantCmd { Action = "wander_start", Replies = { "On it!" } };
        Assert.Equal("Hi babe! On it!", Program.InstantReplyText(c, new[] { "Hi babe! " }, true, new Random(1)));
        Assert.Equal("On it!", Program.InstantReplyText(c, new[] { "Hi babe! " }, false, new Random(1)));
    }

    static Webhook.Ev E(long id, string text) => new("local_chat", "David Nightingale", "44ce5a36-c1c7-4a68-ac9a-635ddfff6233", text, "t", 3) { msg_id = id };

    [Fact]
    public void Instant_claimed_line_is_not_posted_alone_but_rides_along_as_answered_context()
    {
        var answered = new HashSet<long> { 1 }; var instant = new Dictionary<long, string> { [1] = "On it, babe!" };
        Func<Webhook.Ev, bool> isAns = e => answered.Contains(e.msg_id!.Value);
        Func<Webhook.Ev, string> ir = e => instant.TryGetValue(e.msg_id!.Value, out var r) ? r : null;
        Assert.Empty(Webhook.FilterAnswered(new() { E(1, "start wandering") }, isAns, ir, out var d1));
        Assert.Equal(1, d1);
        var b = Webhook.FilterAnswered(new() { E(1, "start wandering"), E(2, "and take a picture of the sunset for me") }, isAns, ir, out var d2);
        Assert.Equal(0, d2);
        Assert.True(b[0].answered == true && b[0].my_reply == "On it, babe!");
        Assert.Null(b[1].answered);
        answered.Add(3); // answered by 'say --re' (not instant): dropped as before
        var c = Webhook.FilterAnswered(new() { E(3, "x"), E(4, "y") }, isAns, ir, out var d3);
        Assert.Single(c); Assert.Equal(4, c[0].msg_id); Assert.Equal(1, d3);
    }

    [Fact]
    public void Davids_lines_post_after_about_a_second_and_a_burst_stays_one_event()
    {
        var t0 = new DateTime(2026, 10, 8, 2, 7, 17, DateTimeKind.Utc);
        Assert.False(Webhook.DueFor(true, false, t0, t0, t0.AddSeconds(1.0)));
        Assert.True(Webhook.DueFor(true, false, t0, t0, t0.AddSeconds(1.3)));    // single line from David: ~1.2 s
        Assert.False(Webhook.DueFor(false, false, t0, t0, t0.AddSeconds(1.3)));  // others keep the 4 s window
        Assert.False(Webhook.DueFor(true, true, t0, t0.AddSeconds(1), t0.AddSeconds(3)));  // burst: waits 3 s quiet
        Assert.True(Webhook.DueFor(true, true, t0, t0.AddSeconds(1), t0.AddSeconds(4.1)));
        Assert.True(Webhook.DueFor(true, true, t0, t0.AddSeconds(9.5), t0.AddSeconds(10.1))); // capped at 10 s
    }
}
