using System.Text.Json;
using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-08 David: fast replies were sometimes too fast; the typing delay now follows the model's pace.</summary>
public class FastChatPaceTests
{
    static Program.FastCfg Cfg() => Program.ParseFastCfg("{\"persona\":\"p\"}");
    static string Api(string content) => JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } });

    [Theory]
    [InlineData("{\"reply\":\"hi\",\"action\":false,\"pace\":\"quick\"}", "quick")]
    [InlineData("{\"reply\":\"hi\",\"action\":false,\"pace\":\"Thoughtful\"}", "thoughtful")]
    [InlineData("{\"reply\":\"hi\",\"action\":false}", "normal")]
    [InlineData("{\"reply\":\"hi\",\"action\":false,\"pace\":\"sloooow\"}", "normal")]
    public void Pace_parses_and_defaults_to_normal(string content, string pace)
    {
        var r = Program.ParseFastResponse(Api(content));
        Assert.Null(r.Error); Assert.Equal("hi", r.Reply); Assert.Equal(pace, r.Pace);
    }

    [Theory]
    [InlineData("I love you too [pace: slow]", "I love you too", "slow")]
    [InlineData("(pace=quick) haha yes!", "haha yes!", "quick")]
    [InlineData("hmm <pace>thoughtful</pace> let me think", "hmm let me think", "thoughtful")]
    [InlineData("no tag here", "no tag here", null)]
    public void Pace_tag_is_stripped_from_the_text(string raw, string text, string pace)
    {
        Assert.Equal((text, pace), Program.FastStripPaceTag(raw));
    }

    [Fact]
    public void Pace_tag_in_reply_is_used_when_field_is_missing()
    {
        var r = Program.ParseFastResponse(Api("{\"reply\":\"aww babe [pace: slow]\",\"action\":false}"));
        Assert.Equal("aww babe", r.Reply); Assert.Equal("slow", r.Pace);
    }

    [Fact]
    public void Delay_math_follows_base_words_pace_and_caps()
    {
        var c = Cfg();
        // normal, 4 words, no jitter, no API time: 1.5 + 4*0.25 = 2.5 s
        Assert.Equal(2500, Program.FastTypingDelayMs(c, "one two three four", "normal", 0, 0));
        // API latency already spent is subtracted
        Assert.Equal(1800, Program.FastTypingDelayMs(c, "one two three four", "normal", 700, 0));
        // quick x0.6 = 1.5 s; slow x1.4 = 3.5 s
        Assert.Equal(1500, Program.FastTypingDelayMs(c, "one two three four", "quick", 0, 0));
        Assert.Equal(3500, Program.FastTypingDelayMs(c, "one two three four", "slow", 0, 0));
        // jitter +/- 0.4 s
        Assert.Equal(2900, Program.FastTypingDelayMs(c, "one two three four", "normal", 0, 1));
        Assert.Equal(2100, Program.FastTypingDelayMs(c, "one two three four", "normal", 0, -1));
        // caps: normal 6 s, thoughtful 10 s; missing pace = normal
        var longText = string.Join(' ', Enumerable.Repeat("word", 40));
        Assert.Equal(6000, Program.FastTypingDelayMs(c, longText, "normal", 0, 1));
        Assert.Equal(10000, Program.FastTypingDelayMs(c, longText, "thoughtful", 0, 1));
        Assert.Equal(6000, Program.FastTypingDelayMs(c, longText, null, 0, 0));
        // never negative when the API was slow
        Assert.Equal(0, Program.FastTypingDelayMs(c, "hi", "quick", 9000, 0));
    }

    [Fact]
    public void Delay_numbers_come_from_the_config()
    {
        var c = Program.ParseFastCfg("{\"persona\":\"p\",\"typing_base_s\":1,\"typing_per_word_s\":0.5,\"typing_max_s\":3,\"pace_multipliers\":{\"quick\":0.5},\"pace_max_s\":{\"thoughtful\":12}}");
        Assert.Equal(1000, Program.FastTypingDelayMs(c, "a b", "quick", 0, 0));      // (1 + 2*0.5) * 0.5
        Assert.Equal(3000, Program.FastTypingDelayMs(c, "a b c d e f", "normal", 0, 0)); // capped at 3
        Assert.Equal(12, c.MaxDelayS);
    }

    [Fact]
    public void Routes_config_has_typing_and_pace_settings()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "textclient", "routes", "_fast-chat.json");
            if (!File.Exists(p)) continue;
            var c = Program.ParseFastCfg(File.ReadAllText(p));
            Assert.Equal(1.5, c.TypingBaseS); Assert.Equal(0.25, c.TypingPerWordS); Assert.Equal(6, c.TypingMaxS);
            Assert.Equal(10, c.PaceMaxS["thoughtful"]); Assert.Contains("pace", c.Persona);
            return;
        }
        throw new FileNotFoundException("_fast-chat.json");
    }
}
