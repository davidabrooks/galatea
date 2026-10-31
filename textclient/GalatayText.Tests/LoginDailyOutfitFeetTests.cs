using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-10-31 12:52 login: the veil put on the usual outfit (Moca boots), the daily picker swapped to 'PCP Beth Tube Top' (barefoot) ~30 s later,
// and f1 was pressed only at 12:54:00 (~90 s after login): the feet waited for the 8 s whole-outfit window and an unrelated pose transition.
public class LoginDailyOutfitFeetTests
{
    const string Shirt = "TETRA - Chill T-Shirt - LaraX Petite", Boots = "TETRA - Moca Ankle Boots (Suede) - Maitreya Mid",
        Top = "Beth Top :: PetiteX - White :: Pink Cream Pie", Jeans = "Maitreya LaraX Vintage Jeans Low-Waist #3 (Flat)", Hair = "DOUX - Oasis Hairstyle [M/Long]";
    static readonly UUID ShirtId = UUID.Random(), BootsId = UUID.Random(), TopId = UUID.Random(), JeansId = UUID.Random(), HairId = UUID.Random();
    static List<(UUID, string)> Usual() => new() { (ShirtId, Shirt), (BootsId, Boots) };
    static List<(UUID, string)> Daily() => new() { (TopId, Top), (JeansId, Jeans) };
    static List<LoginVeil.Stashed> Worn() => new() { new(HairId, 2, Hair), new(ShirtId, 10, Shirt), new(BootsId, 7, Boots) };

    [Fact] public void LongAbsence_NewDay_LoginWearsTheDailyPick_NotTheUsual()
    {
        var since = TimeSpan.FromMinutes(1149);
        var t = UsualOutfit.LoginTarget(since, dailyPending: true, "TETRA Chill T-Shirt shorts", Usual(), "PCP Beth Tube Top", Daily());
        Assert.True(t.Daily); Assert.Equal("PCP Beth Tube Top", t.Name);
        var p = UsualOutfit.AtLogin(Worn(), t.Links, t.Name, since);
        Assert.True(p.Changed);
        Assert.Contains(p.Stash, x => x.Item == TopId); Assert.Contains(p.Stash, x => x.Item == JeansId);
        Assert.DoesNotContain(p.Stash, x => x.Item == BootsId);                 // barefoot from the start: no boots, then a swap
        Assert.Contains(BootsId, p.Drop); Assert.Contains(p.Stash, x => x.Item == HairId);   // head parts unchanged
        var (now, _) = LoginVeil.SplitStash(p.Stash);
        Assert.Contains(now, x => x.Item == TopId);                              // clothes go with the veil flow as before
    }

    [Fact] public void QuickRelog_KeepsExactlyWhatWasWorn_EvenOnANewDay()
    {
        var since = TimeSpan.FromMinutes(4);
        var t = UsualOutfit.LoginTarget(since, true, "TETRA Chill T-Shirt shorts", Usual(), "PCP Beth Tube Top", Daily());
        Assert.False(t.Daily);
        var p = UsualOutfit.AtLogin(Worn(), t.Links, t.Name, since);
        Assert.False(p.Changed); Assert.Contains(p.Stash, x => x.Item == BootsId);
    }

    [Fact] public void DailyAlreadyDone_OrNoPrePick_WearsTheUsual()
    {
        var since = TimeSpan.FromMinutes(60);
        Assert.False(UsualOutfit.LoginTarget(since, false, "U", Usual(), "D", Daily()).Daily);
        Assert.False(UsualOutfit.LoginTarget(since, true, "U", Usual(), null, new()).Daily);
        Assert.False(UsualOutfit.LoginTarget(since, true, "U", Usual(), "D", new() { (HairId, Hair) }).Daily);   // no clothes in it: usual
    }

    [Fact] public void DailyPickerAfterLogin_KeepsTheVeilPick_NoSecondSwap()
    {
        var cands = new List<string> { "PCP Beth Tube Top", "TETRA Chill T-Shirt", "TETRA Chill T-Shirt shorts" };
        var pool = Program.DailyOutfitPool(cands, "TETRA Chill T-Shirt shorts");
        Assert.Equal("PCP Beth Tube Top", Program.DailyPickAtLogin(cands, pool, "pcp beth tube top", _ => "TETRA Chill T-Shirt"));
        Assert.Equal("TETRA Chill T-Shirt", Program.DailyPickAtLogin(cands, pool, null, _ => "TETRA Chill T-Shirt"));
        Assert.Equal("TETRA Chill T-Shirt", Program.DailyPickAtLogin(cands, pool, "Removed Outfit", _ => "TETRA Chill T-Shirt"));
    }

    static FeetAuto.FeetRules Rules() => new(UUID.Random(), "f1", new() { [BootsId] = ("Moca", "f3") });

    [Fact] public void FootwearSignature_TracksOnlyListedShoes()
    {
        var r = Rules();
        var withBoots = FeetAuto.FeetFootwearSignature(new[] { ShirtId, BootsId }, r);
        Assert.Equal(withBoots, FeetAuto.FeetFootwearSignature(new[] { TopId, JeansId, BootsId }, r));   // the rest of the outfit changing is not a footwear change
        Assert.NotEqual(withBoots, FeetAuto.FeetFootwearSignature(new[] { TopId, JeansId }, r));          // boots off
    }

    [Fact] public void FootwearChange_PressesWithoutTheOutfitWindowOrPoseTransition()
    {
        // 12:53:15-12:53:23: worn attachments changed 0-4 s ago, then an idle-pose transition: both blocked the press
        Assert.False(FeetAuto.FeetPressReady(0, true, 0, false, 60).ok);                          // old path still blocks
        Assert.True(FeetAuto.FeetPressReady(0, true, 0, false, 60, footwearChange: true).ok);      // footwear change: press now
        Assert.False(FeetAuto.FeetPressReady(0, false, 99, true, 60, footwearChange: true).ok);    // still never while sitting (verification needs standing)
        Assert.False(FeetAuto.FeetPressReady(0, false, 99, false, 10, footwearChange: true).ok);   // login grace kept
    }

    [Fact] public void FastPress_AfterTheFootwearHolds_WithinFiveSeconds()
    {
        Assert.False(FeetAuto.FeetFastPress(true, 0.5, "f1", "f3"));                // not held yet
        Assert.True(FeetAuto.FeetFastPress(true, FeetAuto.FeetFootwearStableSec, "f1", "f3"));
        Assert.True(FeetAuto.FeetFastPress(true, 2, "f1", null));                  // unknown last pose
        Assert.False(FeetAuto.FeetFastPress(true, 2, "f1", "f1"));                 // already right
        Assert.False(FeetAuto.FeetFastPress(false, 99, "f1", "f3"));               // no footwear change: normal two-poll rule
        Assert.Equal(1000, FeetAuto.FeetPollMs(true, 99));
        Assert.Equal(4000, FeetAuto.FeetPollMs(false, 99));
        // worst case decision latency = hold + one fast poll, well under the 5 s target (the old path needed >= 8 s + two 4 s polls)
        Assert.True(FeetAuto.FeetFootwearStableSec + FeetAuto.FeetFastPollSec < 5);
    }
}
