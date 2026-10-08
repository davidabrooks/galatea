using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-07 19:38 "You greeted me twice": wander greets skip anyone she spoke near (or greeted) in the last 15 min.</summary>
public class WanderSpokeToTests
{
    static readonly Vector3 Me = new(120, 120, 22);
    static readonly DateTime Now = new(2026, 10, 7, 19, 38, 17);

    [Fact]
    public void Chat_ranges_and_in_range_filter()
    {
        Assert.Equal(20f, Program.ChatRangeOf(ChatType.Normal));
        Assert.Equal(100f, Program.ChatRangeOf(ChatType.Shout));
        Assert.Equal(10f, Program.ChatRangeOf(ChatType.Whisper));
        UUID self = UUID.Random(), near = UUID.Random(), far = UUID.Random(), unknown = UUID.Random();
        var avs = new[] { (self, Me, true), (near, new Vector3(135, 120, 22), true), (far, new Vector3(150, 120, 22), true), (unknown, Me, false) };
        Assert.Equal(new[] { near }, Program.InChatRange(avs, Me, 20f, self));
        Assert.Empty(Program.InChatRange(avs, Me, 10f, self));
        Assert.Equal(2, Program.InChatRange(avs, Me, 100f, self).Count);
    }

    [Fact]
    public void Routine_say_blocks_wander_greet_for_15_minutes()
    {
        var david = UUID.Random(); var self = UUID.Random();
        var spoke = new Dictionary<UUID, DateTime>();
        Program.MarkSpoke(spoke, new[] { david }, Now.AddSeconds(-30));   // routine "Hey babe, I'm back!"
        var avs = new List<Program.GreetCand> { new(david, "David Nightingale", new Vector3(123.4f, 120, 22), false) };
        var (w, _) = Program.PickGreet(avs, Me, Now, new(), DateTime.MinValue, _ => false, self, null, 24, spoke);
        Assert.Null(w);
        var (w2, why2) = Program.PickGreet(avs, Me, Now.AddMinutes(15), new(), DateTime.MinValue, _ => false, self, null, 24, spoke);
        Assert.Equal(david, w2?.id); Assert.Equal("ok", why2);
        var (w3, _) = Program.PickGreet(avs, Me, Now, new(), DateTime.MinValue, _ => false, self, null, 24, null);
        Assert.Equal(david, w3?.id);   // nobody spoken to: normal greeting still works
    }

    [Fact]
    public void Greeted_within_15_minutes_is_skipped_even_with_short_repeat()
    {
        var a = UUID.Random(); var self = UUID.Random();
        var greeted = new Dictionary<UUID, DateTime> { [a] = Now.AddMinutes(-10) };
        var avs = new List<Program.GreetCand> { new(a, "Anna Walker", new Vector3(122, 120, 22), false) };
        Assert.Null(Program.PickGreet(avs, Me, Now, greeted, DateTime.MinValue, _ => false, self, null, 0.05).who);
        Assert.NotNull(Program.PickGreet(avs, Me, Now.AddMinutes(6), greeted, DateTime.MinValue, _ => false, self, null, 0.05).who);
    }

    [Fact]
    public void MarkSpoke_keeps_latest_time()
    {
        var a = UUID.Random(); var spoke = new Dictionary<UUID, DateTime>();
        Program.MarkSpoke(spoke, new[] { a, UUID.Zero }, Now);
        Program.MarkSpoke(spoke, new[] { a }, Now.AddMinutes(-5));
        Assert.Equal(Now, spoke[a]); Assert.False(spoke.ContainsKey(UUID.Zero));
        Assert.True(Program.SpokeRecently(spoke, a, Now.AddMinutes(14), Program.SpokeWindow));
        Assert.False(Program.SpokeRecently(spoke, a, Now.AddMinutes(15), Program.SpokeWindow));
    }
}
