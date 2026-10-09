using System.Text.Json;
using GalatayMcp;
using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-08 David: chat answers took ~60 s (routine start-up). FastChat.cs answers conversational nearby chat
/// and IMs through the xAI API in seconds; the API is mocked here (pure, no network).</summary>
public class FastChatTests
{
    static Program.FastCfg Cfg()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            foreach (var p in new[] { Path.Combine(d.FullName, "textclient", "routes", "_fast-chat.json"), Path.Combine(d.FullName, "routes", "_fast-chat.json") })
                if (File.Exists(p)) { var c = Program.ParseFastCfg(File.ReadAllText(p)); Assert.NotNull(c); return c; }
        throw new FileNotFoundException("_fast-chat.json");
    }
    static string Api(string content, int pt = 900, int cached = 800, int outT = 20) => JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { role = "assistant", content } } },
        usage = new { prompt_tokens = pt, completion_tokens = outT, prompt_tokens_details = new { cached_tokens = cached } },
    });
    const string Dav = "44ce5a36-c1c7-4a68-ac9a-635ddfff6233", Ryan = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void Routes_config_is_valid_and_uses_the_fast_model()
    {
        var c = Cfg();
        Assert.True(c.Enabled);
        Assert.Equal("grok-4.20-0309-non-reasoning", c.Model);
        Assert.InRange(c.TimeoutS, 1, 10);
        Assert.Contains("Ryan", c.Persona); Assert.Contains("Agrasifim", c.Persona); Assert.Contains("rent", c.Persona);
        Assert.Null(Program.ParseFastCfg("{\"enabled\":true}"));   // no persona -> never call the model
    }

    [Fact]
    public void Log_lines_parse_into_separate_nearby_and_IM_scopes()
    {
        Assert.Equal(new Program.FastLine("nearby", "David Nightingale", "hi babe", false), Program.FastParseLog("chat", "David Nightingale: hi babe"));
        Assert.Null(Program.FastParseLog("chat", "Door (object): click"));
        Assert.Equal("hey you", Program.FastParseLog("me-chat", "(say --re 12, fast) hey you").Text);
        Assert.Equal("Welcome!", Program.FastParseLog("me-chat", "Welcome! (wander greeting)").Text);
        Assert.Null(Program.FastParseLog("me-chat", "(channel 7) /cmd"));
        var inIm = Program.FastParseLog("im", $"David Nightingale ({Dav}): miss you");
        Assert.Equal(("im:" + Dav, "miss you", false), (inIm.Scope, inIm.Text, inIm.Mine));
        var outIm = Program.FastParseLog("me-im", $"to David Nightingale ({Dav}): miss you too (fast --re 5)");
        Assert.Equal(("im:" + Dav, "miss you too", true), (outIm.Scope, outIm.Text, outIm.Mine));
        Assert.Equal("on it", Program.FastParseLog("me-im", $"to David Nightingale ({Dav}): on it (fast ack)").Text);
    }

    [Fact]
    public void Request_puts_persona_first_and_keeps_IM_and_nearby_history_apart()
    {
        var c = Cfg();
        var hist = new List<Program.FastLine>
        {
            new("nearby", "Sophie", "lovely beach", false),
            new("im:" + Dav, "David Nightingale", "secret IM line", false),
            new("im:" + Ryan, "Ryan", "ryan IM line", false),
            new("nearby", "David Nightingale", "how was your day?", false),
        };
        var near = JsonDocument.Parse(Program.BuildFastRequest(c, "at home", hist, "nearby", "David Nightingale", "how was your day?", false)).RootElement;
        var msgs = near.GetProperty("messages");
        Assert.Equal(c.Persona, msgs[0].GetProperty("content").GetString());   // stable prefix (prompt cache)
        Assert.Equal("Current state: at home", msgs[1].GetProperty("content").GetString());
        var u = msgs[2].GetProperty("content").GetString();
        Assert.Contains("lovely beach", u); Assert.DoesNotContain("IM line", u);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(u, "how was your day"));   // the new line once, at the end
        Assert.EndsWith("from David Nightingale: how was your day?", u);
        Assert.Equal(c.Model, near.GetProperty("model").GetString());

        var im = Program.BuildFastRequest(c, "x", hist, "im:" + Dav, "David Nightingale", "hello", true);
        Assert.Contains("secret IM line", im); Assert.DoesNotContain("lovely beach", im); Assert.DoesNotContain("ryan IM line", im);
        Assert.Contains("Private IM", im);

        hist.Add(new("voice", "David Nightingale", "spoken earlier", false));
        var voice = Program.BuildFastRequest(c, "x", hist, "voice", "David Nightingale", "can you hear me", false);
        Assert.Contains("spoken earlier", voice); Assert.Contains("speech-to-text", voice);
        Assert.DoesNotContain("lovely beach", voice); Assert.DoesNotContain("IM line", voice);
        Assert.DoesNotContain("spoken earlier", Program.BuildFastRequest(c, "x", hist, "nearby", "David Nightingale", "hi", false));
    }

    [Fact]
    public void Responses_parse_and_decide_send_ack_or_routine()
    {
        var r = Program.ParseFastResponse(Api("{\"reply\":\"Hey babe!\\nMissed you\",\"action\":false}"));
        Assert.Equal(("Hey babe! Missed you", false, 900, 800, 20), (r.Reply, r.Action, r.PromptTok, r.CachedTok, r.OutTok));
        Assert.Equal(Program.FastOutcome.Send, Program.FastDecide(r, true));
        var act = Program.ParseFastResponse(Api("{\"reply\":\"Sure, one sec!\",\"action\":true}"));
        Assert.Equal(Program.FastOutcome.Ack, Program.FastDecide(act, true));        // David: ack, routine acts
        Assert.Equal(Program.FastOutcome.Routine, Program.FastDecide(act, false));   // visitor: routine only
        Assert.Equal(Program.FastOutcome.Routine, Program.FastDecide(Program.ParseFastResponse(Api("{\"reply\":\"\",\"action\":false}")), true));
        Assert.Equal(Program.FastOutcome.Routine, Program.FastDecide(Program.ParseFastResponse(Api("not json")), true));
        Assert.Equal(Program.FastOutcome.Routine, Program.FastDecide(Program.ParseFastResponse("{\"error\":\"x\"}"), true));
        Assert.Equal("ok", Program.ParseFastResponse(Api("```json\n{\"reply\":\"ok\",\"action\":false}\n```")).Reply);
        Assert.Equal(0.00175, Program.FastCost(Cfg(), 1000, 0, 200), 6);
        Assert.Equal(0.00041, Program.FastCost(Cfg(), 1000, 1000, 84), 6);   // cached prompt: 0.2 + 0.21
    }

    [Fact]
    public async Task Mocked_API_call_returns_and_a_slow_API_times_out_to_the_routine()
    {
        var c = Cfg(); c.TimeoutS = 1;
        var p0 = Program.FastPostOverride;
        try
        {
            string seen = null;
            Program.FastPostOverride = (body, ct) => { seen = body; return Task.FromResult(Api("{\"reply\":\"Hi love\",\"action\":false}")); };
            var (r, _) = await Program.FastCall(c, "{\"x\":1}", null);
            Assert.Equal("Hi love", r.Reply); Assert.Equal("{\"x\":1}", seen);
            Program.FastPostOverride = async (body, ct) => { await Task.Delay(5000, ct); return Api("{\"reply\":\"late\"}"); };
            var (slow, ms) = await Program.FastCall(c, "{}", null);
            Assert.StartsWith("timeout", slow.Error); Assert.InRange(ms, 500, 3000);
            Assert.Equal(Program.FastOutcome.Routine, Program.FastDecide(slow, true));
        }
        finally { Program.FastPostOverride = p0; }
    }

    [Fact]
    public void Webhook_holds_in_flight_lines_and_marks_acked_lines()
    {
        var a = new Webhook.Ev("local_chat", "David", Dav, "go to the beach", "t", 1) { msg_id = 7 };
        var b = new Webhook.Ev("local_chat", "David", Dav, "lol", "t", 1) { msg_id = 8, answered = true, my_reply = "haha" };
        Assert.True(Webhook.Held(new[] { a, b }, id => id == 7));
        Assert.False(Webhook.Held(new[] { b }, id => id == 7));
        var r = Webhook.WithFastAcks(new List<Webhook.Ev> { a, b }, id => id is 7 or 8 ? "Sure, one sec!" : null);
        Assert.Equal("Sure, one sec!", r[0].fast_ack);
        Assert.Null(r[1].fast_ack);   // an answered line is context only
    }
}
