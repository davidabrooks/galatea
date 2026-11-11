using System.Text.Json;
using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-11-11 David: "say thanks galatea" must thank the person just thanked (Beragon), never herself.</summary>
public class FastChatSpeakTests
{
    static List<Program.FastLine> IncidentHist() => new()
    {
        new("nearby", "Kikas Babenco", "Thanks for the music Berangon!", false),
        new("nearby", "Beragon Betts", "my pleasure", false),
        new("nearby", "David Nightingale", "thank you beragon", false),
    };

    [Fact]
    public void Say_thanks_galatea_is_a_speak_command_and_dance_galatea_is_not()
    {
        Assert.True(Program.FastIsSpeakCommand("say thanks galatea"));
        Assert.True(Program.FastIsSpeakCommand("say thanks"));
        Assert.True(Program.FastIsSpeakCommand("Say thanks, Galatea!"));
        Assert.True(Program.FastIsSpeakCommand("Galatea, say thanks"));
        Assert.True(Program.FastIsSpeakCommand("please say thanks babe"));
        Assert.True(Program.FastIsSpeakCommand("tell them thanks"));
        Assert.True(Program.FastIsSpeakCommand("thank beragon"));
        Assert.False(Program.FastIsSpeakCommand("thank you beragon"));
        Assert.False(Program.FastIsSpeakCommand("dance galatea"));
        Assert.False(Program.FastIsSpeakCommand("can you hear me galatea?"));
        Assert.Equal("say thanks", Program.FastStripVocative("say thanks galatea"));
        Assert.True(Program.FastIsThanksCommand("say thanks galatea"));
        Assert.False(Program.FastIsThanksCommand("dance galatea"));
        Assert.False(Program.FastIsThanksCommand("say hi everyone"));
    }

    [Fact]
    public void Recent_thankee_is_Beragon_from_the_incident_history()
    {
        Assert.Equal("Beragon", Program.FastRecentThankee(IncidentHist(), "nearby"));
        Assert.Equal("Beragon", Program.FastRecentThankee(IncidentHist(), "nearby")); // David is newest; spelling comes from the speaker name
        Assert.Null(Program.FastRecentThankee(new List<Program.FastLine>(), "nearby"));
    }

    [Fact]
    public void Say_thanks_galatea_thanks_Beragon_never_Galatea()
    {
        var (reply, why) = Program.FastVetReply("Thanks Galatea! \u2764\uFE0F", IncidentHist(), "nearby", "say thanks galatea", true);
        Assert.Null(why);
        Assert.Contains("Beragon", reply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Galatea", reply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Galatay", reply, StringComparison.OrdinalIgnoreCase);
        Assert.True(Program.FastAddressesSelf("Thanks Galatea! \u2764\uFE0F"));
        Assert.False(Program.FastAddressesSelf(reply));
    }

    [Fact]
    public void Bare_say_thanks_names_Beragon()
    {
        var (reply, why) = Program.FastVetReply("Thanks!", IncidentHist(), "nearby", "say thanks", true);
        Assert.Null(why);
        Assert.Contains("Beragon", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Thanks_Galatea_is_rejected_when_no_thankee_is_known()
    {
        var (reply, why) = Program.FastVetReply("Thanks Galatea!", new List<Program.FastLine>(), "nearby", "hi babe", true);
        Assert.NotNull(why);
        Assert.Contains("herself", why);
        Assert.Equal("Thanks Galatea!", reply);
    }

    [Fact]
    public void Dance_galatea_still_gets_an_action_ack()
    {
        Assert.False(Program.FastIsSpeakCommand("dance galatea"));
        var json = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content = "{\"reply\":\"Sure babe, one sec!\",\"action\":true,\"pace\":\"quick\"}" } } },
            usage = new { prompt_tokens = 100, completion_tokens = 20, prompt_tokens_details = new { cached_tokens = 0 } },
        });
        var r = Program.ParseFastResponse(json);
        var dec = Program.FastDecide(r, true);
        var why = Program.FastPostprocess(ref r, ref dec, true, "dance galatea", IncidentHist(), "nearby");
        Assert.Null(why);
        Assert.Equal(Program.FastOutcome.Ack, dec);
        Assert.Equal("Sure babe, one sec!", r.Reply);
        Assert.True(r.Action);
    }

    [Fact]
    public void Speak_command_is_sent_not_treated_as_an_action()
    {
        var json = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content = "{\"reply\":\"Thanks Galatea!\",\"action\":true,\"pace\":\"quick\"}" } } },
            usage = new { prompt_tokens = 100, completion_tokens = 20, prompt_tokens_details = new { cached_tokens = 0 } },
        });
        var r = Program.ParseFastResponse(json);
        var dec = Program.FastDecide(r, true);
        Assert.Equal(Program.FastOutcome.Ack, dec); // before postprocess, the model wrongly set action
        var why = Program.FastPostprocess(ref r, ref dec, true, "say thanks galatea", IncidentHist(), "nearby");
        Assert.Null(why);
        Assert.Equal(Program.FastOutcome.Send, dec);
        Assert.False(r.Action);
        Assert.Contains("Beragon", r.Reply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Galatea", r.Reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reasoning_tokens_are_parsed_and_costed()
    {
        var json = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content = "{\"reply\":\"Thanks, Beragon!\",\"action\":false}" } } },
            usage = new
            {
                prompt_tokens = 334,
                completion_tokens = 35,
                prompt_tokens_details = new { cached_tokens = 128 },
                completion_tokens_details = new { reasoning_tokens = 1462 },
            },
        });
        var r = Program.ParseFastResponse(json);
        Assert.Equal(1462, r.ReasonTok);
        Assert.Equal(334, r.PromptTok);
        Assert.Equal(35, r.OutTok);
        Assert.Equal("Thanks, Beragon!", r.Reply);
    }
}

public class ChatGuardWindowTests
{
    static readonly TimeSpan W = TimeSpan.FromSeconds(5);

    [Fact]
    public void Distinct_routine_followup_within_5s_goes_through_but_lol_after_lol_is_blocked()
    {
        var t = DateTimeOffset.Now;
        Assert.True(Program.ChatWindowBlocks(t, t.AddSeconds(-0.5), W, false, "lol", "lol"));
        Assert.True(Program.ChatWindowBlocks(t, t.AddSeconds(-3.5), W, false, "Yay, all good now?", "Yay, all good now?"));
        Assert.False(Program.ChatWindowBlocks(t, t.AddSeconds(-3.5), W, false,
            "Thanks Beragon! Give my android bits a sec to finish rezzing, hehe", "Yay, all good now?"));
        Assert.False(Program.ChatWindowBlocks(t, t.AddSeconds(-0.6), W, false,
            "Aww, thank you, Jinja! Honestly my AO is doing most of the work", "Hi Be!"));
        Assert.False(Program.ChatWindowBlocks(t, t.AddSeconds(-1), W, true, "lol", "lol"));
        Assert.False(Program.ChatWindowBlocks(t, t.AddSeconds(-6), W, false, "lol", "lol"));
    }
}
