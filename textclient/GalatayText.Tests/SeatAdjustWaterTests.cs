using System;
using System.IO;
using GalatayText;
using Xunit;

// 2026-11-22 (fixes list): the Nov 20 Dutchie tub ('read book' -0.319, 'shave' -0.489, 'soak' -0.189 nudged worse) and the Mirage bed
// ('texting' -0.263) were render-only false positives. In water the tub's water surface lies over her body and reads as a sunk gap; the
// Mirage pose was right, sunk by the Nov 11 save that a 91 % dark re-render "confirmed".
public class SeatAdjustWaterTests
{
    static SeatAdjust.SupportReading R(string kind, double gap, params string[] bear) => new(kind, bear, gap, new());
    const string GoodQ = "GT: support quality: frame z 20.000..20.600; seat found; hits shins 12, feet 9, hips 10, thighs 8, chest 9, hands 11; surface shins 20.150, feet 20.140, hips 20.300, thighs 20.310, chest 20.300, hands 20.320\nGT: render quality: overexposed 0.090; dark 0.100; contrast 0.300";
    static string Src(string f)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "textclient/galatay-text", f));
    }

    [Theory]
    [InlineData("Dutchie clawfoot bath tub adult", true)]
    [InlineData("Hot Tub Deluxe", true)]
    [InlineData("hottub v2", true)]
    [InlineData("Beach Pool Float", true)]
    [InlineData("Jacuzzi", true)]
    [InlineData("BENTO PHYSICS MIRAGE BED 1.0", false)]
    [InlineData("BackBone Playtime Bathroom Sink - Adult", false)]
    [InlineData("Poolrock bench", false)]
    [InlineData("Spanish chair", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void WaterSeat_FromTheName(string name, bool water) => Assert.Equal(water, SeatAdjust.IsWaterSeat(name));

    [Fact] public void InWater_ASunkReadingIsUnmeasurable_AFloatingOneIsNot()
    {
        var q = SeatAdjust.ParseQuality(GoodQ);
        Assert.Contains("water", SeatAdjust.GateFail(R("sitting", -0.189, "hips", "thighs"), q, waterSeat: true));   // the Nov 20 'soak' reading
        Assert.Null(SeatAdjust.GateFail(R("sitting", -0.189, "hips", "thighs"), q));                                // out of water: still measurable
        Assert.Null(SeatAdjust.GateFail(R("sitting", 0.12, "hips", "thighs"), q, waterSeat: true));                 // floating over the water: measurable
        Assert.Null(SeatAdjust.GateFail(R("sitting", -0.01, "hips", "thighs"), q, waterSeat: true));                // in the contact band: fine
    }

    [Fact] public void InWater_ANudgeThatEndsSunkIsUndone()
    {
        var q = SeatAdjust.ParseQuality(GoodQ);
        var (call, why) = SeatAdjust.SaveDecision(R("sitting", 0.20, "hips"), q, R("sitting", -0.15, "hips"), q, 35, true, true, waterSeat: true);
        Assert.Equal(SeatAdjust.SaveCall.Revert, call); Assert.Contains("water", why);
    }

    [Fact] public void TheThreeNov20Readings_AreNeverBugged_OnlySuspect()
    {
        var tub = SeatAdjust.LinksetZ(new[] { (28.6, 0.4) });   // her centre 28.9 in a 28.2..29.0 tub: neither floating nor buried
        foreach (var (r, water) in new[] { (R("lying", -0.319, "chest"), true), (R("sitting", -0.489, "thighs"), true), (R("lying", -0.263, "chest"), false) })
        {
            Assert.Null(SeatAdjust.BuggedReason(28.9, 1.566, tub, r, true));
            Assert.NotNull(SeatAdjust.SuspectReason(r, water));
            Assert.Contains("beyond any fit", SeatAdjust.GateFail(r, SeatAdjust.ParseQuality(GoodQ), water) ?? "");
        }
        Assert.Contains("water surface", SeatAdjust.SuspectReason(R("lying", -0.319, "chest"), true));
        Assert.Null(SeatAdjust.SuspectReason(R("sitting", -0.10, "hips"), true));
    }

    [Fact] public void TheRun_LogsASuspectWithoutDenylisting_AndPassesTheWaterFlagEverywhere()
    {
        var run = Src("SeatAdjustRun.cs");
        Assert.Contains("bool water = SeatAdjust.IsWaterSeat(seatName);", run);
        Assert.Contains("SeatAdjust.SuspectReason(r0, water) is string suspect", run);
        var iSuspect = run.IndexOf("SeatAdjust.SuspectReason(r0, water)", StringComparison.Ordinal);
        var iPress = run.IndexOf("SeatAdjustOpenPersonal(seat, ct)", StringComparison.Ordinal);
        Assert.True(iSuspect > 0 && iSuspect < iPress);                         // decided before any menu is opened
        Assert.DoesNotContain("SeatAdjustGatedRender();", run);                 // every render is gated with the water flag
    }
}
