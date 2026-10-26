using System;
using LibreMetaverse;
using Xunit;
using P = GalatayText.Program;

// 2026-10-26 10:16:49 / 10:13:59 live: android form, Magic Leash -> Invisible, David unleashed me; the collar sent its RLV lifts
// at once but the client only noticed 30-50 s later (chain-gone + 30 s no-pull rule, kept fresh by my own leash-walk) and kept
// walking after him. The collar's RLV lift is now the prompt, authoritative unleash signal.
public class LeashRlvReleaseTests
{
    static readonly UUID Collar = UUID.Parse("617d81ea-ca85-4172-8252-9caa307422db");
    static readonly UUID David = UUID.Parse("44ce5a36-c1c7-4a68-ac9a-635ddfff6233");
    static readonly DateTime T0 = new(2026, 10, 26, 10, 16, 34);

    // one ownersay line per message, exactly as logged
    static string Feed(P.RlvStore st, P.LeashRelease rel, DateTime t, string line, bool leashed = true, UUID? holder = null)
    {
        string last = null;
        foreach (var c in P.RlvParse(line)) last = rel.Apply(st, t, Collar, "RLV", c, holder ?? David, leashed);
        return last;
    }
    static readonly string[] LeashOn = { "@fly=n", "@tplm=n", "@tplure=n", "@tploc=n", "@sittp:6=n", "@tplure:44ce5a36-c1c7-4a68-ac9a-635ddfff6233=n" };
    static readonly string[] LeashOff = { "@fly=y", "@tplm=y", "@tplure=y", "@tploc=y", "@sittp:6=y", "@tplure:44ce5a36-c1c7-4a68-ac9a-635ddfff6233=y" };   // 10:16:49 order
    static readonly string[] LeashOff2 = { "@tplm=y", "@tplure=y", "@fly=y", "@tploc=y", "@sittp:6=y", "@tplure:44ce5a36-c1c7-4a68-ac9a-635ddfff6233=y" };  // 10:13:59 order

    static (P.RlvStore, P.LeashRelease) Leashed()
    {
        var st = new P.RlvStore(); var rel = new P.LeashRelease();
        foreach (var m in LeashOn) Assert.Equal("restricted", Feed(st, rel, T0, m, leashed: false));
        return (st, rel);
    }

    [Theory] [InlineData(0)] [InlineData(1)]
    public void CollarReleaseEndsTheLeashAfterTheConfirmWindow(int order)
    {
        var (st, rel) = Leashed();
        var t = T0.AddSeconds(15);
        foreach (var m in order == 0 ? LeashOff : LeashOff2) Assert.Equal("lifted", Feed(st, rel, t, m));
        Assert.True(rel.Pending);
        Assert.False(rel.Confirm(st, t.AddSeconds(0.5), David));      // still inside the window
        Assert.True(rel.Confirm(st, t.AddSeconds(1.6), David));       // gone: unleash
        Assert.False(rel.Confirm(st, t.AddSeconds(3), David));        // once only
        Assert.False(st.Has("tplm")); Assert.False(st.Has("tplure", David.ToString()));
    }
    [Fact] public void HolderKeyLiftAloneIsEnough()
    {   // the collar keeps other locks of its own (tplm), but the holder's @tplure exception is leash-specific
        var (st, rel) = Leashed(); var t = T0.AddSeconds(15);
        Feed(st, rel, t, LeashOff[5]);
        Assert.True(rel.Pending);
        Assert.True(rel.Confirm(st, t.AddSeconds(1.6), David));
        Assert.True(st.Has("tplm"));
    }
    [Fact] public void HolderKeyLiftThenReapplyStaysLeashed()
    {
        var (st, rel) = Leashed(); var t = T0.AddSeconds(15);
        Feed(st, rel, t, LeashOff[5]);
        Feed(st, rel, t.AddSeconds(0.7), LeashOn[5]);
        Assert.False(rel.Confirm(st, t.AddSeconds(2), David));
    }
    [Fact] public void LiftThenReapplyInsideTheWindowStaysLeashed()
    {
        var (st, rel) = Leashed(); var t = T0.AddSeconds(15);
        foreach (var m in LeashOff) Feed(st, rel, t, m);
        Assert.True(rel.Pending);
        foreach (var m in LeashOn) Feed(st, rel, t.AddSeconds(0.8), m);   // collar menu re-send within a second
        Assert.False(rel.Pending);
        Assert.False(rel.Confirm(st, t.AddSeconds(2), David));
        Assert.True(st.Has("tplm"));
    }
    [Fact] public void PartialReapplyStillStaysLeashed()
    {
        var (st, rel) = Leashed(); var t = T0.AddSeconds(15);
        foreach (var m in LeashOff) Feed(st, rel, t, m);
        Feed(st, rel, t.AddSeconds(1.0), LeashOn[5]);   // holder's tplure comes back; confirm sees it held
        Assert.False(rel.Confirm(st, t.AddSeconds(1.6), David));
    }
    [Fact] public void LiftsWhileNotLeashedOrForAnotherHolderChangeNothing()
    {
        var (st, rel) = Leashed(); var t = T0.AddSeconds(15);
        foreach (var m in LeashOff) Feed(st, rel, t, m, leashed: false);
        Assert.False(rel.Pending);
        var (st2, rel2) = Leashed();
        foreach (var m in LeashOff) Feed(st2, rel2, t, m);
        Assert.False(rel2.Confirm(st2, t.AddSeconds(1.6), UUID.Random()));   // a different holder now: stale, dropped
    }
    [Fact] public void LiftOfSomethingTheCollarNeverHeldIsNoRelease()
    {
        var st = new P.RlvStore(); var rel = new P.LeashRelease();
        Feed(st, rel, T0, "@tplm=n", leashed: false);
        Feed(st, rel, T0, "@fly=y");                       // never held
        Assert.False(rel.Pending);
        Feed(st, rel, T0, "@sendchat=y");                  // not a leash restriction
        Assert.False(rel.Pending);
    }
    [Fact] public void OtherRestrictionsFromTheCollarDoNotBlockTheRelease()
    {
        var (st, rel) = Leashed(); var t = T0.AddSeconds(15);
        Feed(st, rel, t.AddSeconds(-5), "@detach=n", leashed: false);   // not a leash key: the collar keeps its lock
        foreach (var m in LeashOff) Feed(st, rel, t, m);
        Assert.True(rel.Confirm(st, t.AddSeconds(1.6), David));
        Assert.True(st.Has("detach"));
    }
    [Fact] public void FullLogReplayWalkThenRelease()
    {   // 10:16:34 leash on, 10:16:35..47 leashed (nothing happens), 10:16:49 lifts, no chain at all
        var (st, rel) = Leashed();
        for (int s = 1; s <= 14; s++) Assert.False(rel.Confirm(st, T0.AddSeconds(s), David));
        var t = T0.AddSeconds(15);
        foreach (var m in LeashOff) Feed(st, rel, t, m);
        Assert.True(rel.Confirm(st, t.AddSeconds(1.7), David));   // ~2 s after the lift, not 54 s
    }
    [Fact] public void ChainLossFastFallback()
    {
        Assert.True(P.LeashChainLossEndsFast(3, hadStrict: true, strictNow: false));
        Assert.False(P.LeashChainLossEndsFast(2, true, false));    // not yet 3 scans
        Assert.False(P.LeashChainLossEndsFast(5, true, true));     // restrictions still held: real leash with a hidden chain
        Assert.False(P.LeashChainLossEndsFast(5, false, false));   // never strict: only the old 30 s rule applies
    }
    [Fact] public void NoPullRestartRightAfterARelease()
    {
        var now = DateTime.Now;
        Assert.False(P.LeashPullStartAllowed(now, now.AddSeconds(20)));
        Assert.True(P.LeashPullStartAllowed(now.AddSeconds(21), now.AddSeconds(20)));
    }
}
