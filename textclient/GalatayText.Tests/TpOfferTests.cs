using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-11-11 David: outbound teleport offer (lure) to where Galatay is. Default allow-list is
// David Nightingale only; anyone else needs confirm=true. Name resolution matches the im tool.
public class TpOfferTests
{
    static readonly UUID David = new("44ce5a36-c1c7-4a68-ac9a-635ddfff6233");
    static readonly UUID Sophie = new("d695b17a-6504-4697-a945-0b71c53e4771");
    static readonly UUID Stranger = new("aebe8a43-0170-487f-aad8-3df2dc0137b3");

    [Fact]
    public void Selftest_passes()
    {
        var r = Program.TpOfferSelfTest();
        Assert.DoesNotContain("FAIL ", r);
        Assert.Contains(" 0 FAIL", r);
    }

    [Theory]
    [InlineData("44ce5a36-c1c7-4a68-ac9a-635ddfff6233")]
    [InlineData("David Nightingale")]
    [InlineData("david.nightingale")]
    [InlineData("David")]
    [InlineData("DAVID NIGHTINGALE")]
    public void David_is_allow_listed_by_name_or_uuid(string name) =>
        Assert.True(Program.TpOfferTargetIsDavid(UUID.Zero, name));

    [Fact]
    public void David_is_allow_listed_by_uuid() =>
        Assert.True(Program.TpOfferTargetIsDavid(David, null));

    [Theory]
    [InlineData("me")]
    [InlineData("SophieJeanneLaDouce Resident")]
    [InlineData("Some Stranger")]
    [InlineData("")]
    public void Others_and_me_are_not_allow_listed(string name) =>
        Assert.False(Program.TpOfferTargetIsDavid(UUID.Zero, name));

    [Fact]
    public void David_needs_no_confirm() =>
        Assert.Null(Program.TpOfferRefusal(David, "David Nightingale", false));

    [Fact]
    public void Sophie_is_refused_without_confirm_even_though_incoming_lures_allow_her()
    {
        var r = Program.TpOfferRefusal(Sophie, "SophieJeanneLaDouce Resident", false);
        Assert.NotNull(r);
        Assert.StartsWith("refused:", r);
        Assert.Contains("confirm=true", r);
        Assert.Null(Program.TpOfferRefusal(Sophie, "SophieJeanneLaDouce Resident", true));
    }

    [Fact]
    public void Stranger_needs_confirm()
    {
        Assert.NotNull(Program.TpOfferRefusal(Stranger, "Some Stranger", false));
        Assert.Null(Program.TpOfferRefusal(Stranger, "Some Stranger", true));
    }

    [Fact]
    public void Resolved_uuid_is_authoritative()
    {
        Assert.NotNull(Program.TpOfferRefusal(Stranger, "David Nightingale", false));
        Assert.Null(Program.TpOfferRefusal(David, "Unexpected Name", false));
    }

    [Fact]
    public void Parses_david_name_username_uuid_and_default_message()
    {
        var a = Program.ParseTpOfferArgs("David Nightingale", _ => false);
        Assert.Equal("David Nightingale", a.Target);
        Assert.Equal(Program.TpOfferDefaultMessage, a.Message);
        Assert.False(a.Confirm);

        var b = Program.ParseTpOfferArgs("david.nightingale Come here now", _ => false);
        Assert.Equal("david.nightingale", b.Target);
        Assert.Equal("Come here now", b.Message);

        var c = Program.ParseTpOfferArgs("44ce5a36-c1c7-4a68-ac9a-635ddfff6233", _ => false);
        Assert.Equal("44ce5a36-c1c7-4a68-ac9a-635ddfff6233", c.Target);
        Assert.Equal(Program.TpOfferDefaultMessage, c.Message);
    }

    [Fact]
    public void Parses_confirm_and_quoted_names_like_im()
    {
        var a = Program.ParseTpOfferArgs("--confirm \"Jane Doe\" Meet me", _ => false);
        Assert.Equal("Jane Doe", a.Target);
        Assert.Equal("Meet me", a.Message);
        Assert.True(a.Quoted);
        Assert.True(a.Confirm);

        var b = Program.ParseTpOfferArgs("Some Stranger confirm", _ => false);
        Assert.Equal("Some Stranger", b.Target);
        Assert.True(b.Confirm);
        Assert.Equal(Program.TpOfferDefaultMessage, b.Message);
    }

    [Fact]
    public void Unknown_first_last_keeps_a_one_word_username_fallback()
    {
        var h = Program.ParseTpOfferArgs("Some Stranger hello there", _ => false);
        Assert.Equal("Some Stranger", h.Target);
        Assert.Equal("hello there", h.Message);
        Assert.Equal("Some", h.AltTarget);
        Assert.Equal("Stranger hello there", h.AltMessage);
    }

    [Fact]
    public void Duplicate_guard_skips_the_same_target_within_ten_seconds()
    {
        var t0 = DateTimeOffset.Parse("2026-11-11T15:40:00-08:00");
        Assert.Null(Program.TpOfferDuplicateSkip("David Nightingale", t0, null, Program.TpOfferWindow));
        var skip = Program.TpOfferDuplicateSkip("David Nightingale", t0.AddSeconds(9.9), t0, Program.TpOfferWindow);
        Assert.NotNull(skip);
        Assert.StartsWith("skipped:", skip);
        Assert.Contains("10 s", skip);
        Assert.Null(Program.TpOfferDuplicateSkip("David Nightingale", t0.AddSeconds(10), t0, Program.TpOfferWindow));
    }

    [Fact]
    public void Mcp_exposes_tp_offer_and_never_sends_a_teleport_request()
    {
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.AppContext.BaseDirectory, "../../../../"));
        var mcp = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "galatay-mcp/McpProgram.cs"));
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "galatay-text/TpOffer.cs"));
        Assert.Contains("Name = \"tp_offer\"", mcp);
        Assert.Contains("SendTeleportLure(", src);
        Assert.DoesNotContain("SendTeleportLureRequest(", src);
        Assert.DoesNotContain("SendTeleportLureRequest(", mcp);
    }
}
