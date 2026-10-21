using System;
using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-10-16 11:06:52: David's kiss reached the log but was never detected. Root cause: the live client runs with
// InvariantGlobalization (no ICU), where string.Normalize(FormKC) returns the text unchanged, so the Real Kisser's
// math-bold "𝐤𝐢𝐬𝐬𝐞𝐬" never matched /kisses/. The test project cannot run invariant (LibreMetaverse's static init breaks), so the
// tests pass normalize:false = "Normalize does nothing", exactly the live runtime. (Before the fix these parses returned null.)
public class KissDetectInvariantTests
{
    const string Line = "★ 𝐏𝐫𝐞𝐭𝐭𝐲 𝐋𝐢𝐚𝐫𝐬 ★ David Nightingale 𝐤𝐢𝐬𝐬𝐞𝐬 Galatea Nightingale's 𝐥𝐢𝐩𝐬. ";

    [Fact]
    public void Invariant_runtime_does_not_fold_so_the_manual_fold_is_needed()
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture; Assert.NotNull(invariant);
        Assert.Equal("David Nightingale kisses Galatea", Program.FoldFancyText("David Nightingale 𝐤𝐢𝐬𝐬𝐞𝐬 Galatea", normalize: false));
    }

    [Fact]
    public void Real_kisser_line_parses_without_ICU()
    {
        var k = Program.ParseKissLine(Line, normalize: false);
        Assert.NotNull(k);
        Assert.Equal(("David Nightingale", "Galatea Nightingale", "lips"), k.Value);
        Assert.True(Program.IsKissOnMe(k, "Galatay Resident", "David Nightingale", DateTime.Now, DateTime.MinValue));
    }

    [Fact]
    public void Fold_covers_math_styles_digits_fullwidth()
    {
        Assert.Equal("Pretty Liars kisses", Program.FoldFancyText("𝐏𝐫𝐞𝐭𝐭𝐲 𝐋𝐢𝐚𝐫𝐬 𝓴𝓲𝓼𝓼𝓮𝓼", false));
        Assert.Equal("kisses 123 ABC", Program.FoldFancyText("𝗸𝗶𝘀𝘀𝗲𝘀 𝟏𝟐𝟑 ＡＢＣ", false));
        Assert.Equal("", Program.FoldFancyText(null, false));
    }

    [Fact]
    public void Skip_reason_names_each_silent_return()
    {
        var now = DateTime.Now; var me = "Galatay Resident"; var dv = "David Nightingale";
        Assert.Null(Program.KissSkipReason(ChatSourceType.Object, ChatType.Normal, Line, me, dv, now, DateTime.MinValue, false));
        Assert.Contains("not Object", Program.KissSkipReason(ChatSourceType.Agent, ChatType.Normal, Line, me, dv, now, DateTime.MinValue, false));
        Assert.Contains("OwnerSay", Program.KissSkipReason(ChatSourceType.Object, ChatType.OwnerSay, Line, me, dv, now, DateTime.MinValue, false));
        Assert.Contains("did not parse", Program.KissSkipReason(ChatSourceType.Object, ChatType.Normal, "someone kisses the sky", me, dv, now, DateTime.MinValue, false));
        Assert.Contains("not me", Program.KissSkipReason(ChatSourceType.Object, ChatType.Normal, "Bob kisses Alice's lips.", me, dv, now, DateTime.MinValue, false));
        Assert.Contains("not David", Program.KissSkipReason(ChatSourceType.Object, ChatType.Normal, "Bob kisses Galatea Nightingale's lips.", me, dv, now, DateTime.MinValue, false));
        Assert.Contains("within 20 s", Program.KissSkipReason(ChatSourceType.Object, ChatType.Normal, Line, me, dv, now, now.AddSeconds(-5), false));
        Assert.Contains("unknown", Program.KissSkipReason(ChatSourceType.Object, ChatType.Normal, Line, me, null, now, DateTime.MinValue, false));
    }
}
