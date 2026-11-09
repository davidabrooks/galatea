using LibreMetaverse;
using Xunit;
using GalatayText;

// 2026-11-09 14:15 PT: the login add-missing outfit send read the COF (bikini top + panties still linked) at the moment the daily
// outfit removed those links, waited for attachments to settle, then re-attached the bikini under 'ARTi'S Strapless Top shorts'.
// Fix: wait while the wardrobe lock is held, then re-read the COF and send only what is still linked.
public class OutfitSendStaleCofTests
{
    static readonly UUID BikiniTop = UUID.Random(), BikiniPanties = UUID.Random(), Top = UUID.Random(), Shorts = UUID.Random(),
        Boots = UUID.Random(), Hair = UUID.Random(), TopLink = UUID.Random(), ShortsLink = UUID.Random(), BootsLink = UUID.Random(), HairLink = UUID.Random();

    [Fact]
    public void ItemsRemovedFromTheCofDuringTheSettleWaitAreNotSent()
    {
        // what the send read before its settle wait (14:15:14): the bikini still linked
        var staleBlocks = new[] { BikiniTop, BikiniPanties, Hair, Top, Shorts, Boots };
        // the COF after the swap: link ids and their targets (FolderContentsAsync follows links)
        var fresh = new HashSet<UUID> { HairLink, Hair, TopLink, Top, ShortsLink, Shorts, BootsLink, Boots };
        var keep = AppearanceManager.OutfitSendStillLinked(staleBlocks, fresh);
        Assert.DoesNotContain(BikiniTop, keep);
        Assert.DoesNotContain(BikiniPanties, keep);
        Assert.Equal(new HashSet<UUID> { Hair, Top, Shorts, Boots }, keep);
    }

    [Fact]
    public void NothingIsDroppedWhenTheCofDidNotChange()
    {
        var blocks = new[] { Hair, Top, Shorts };
        Assert.Equal(new HashSet<UUID>(blocks), AppearanceManager.OutfitSendStillLinked(blocks, new HashSet<UUID>(blocks)));
    }

    [Fact]
    public void AnEmptyFreshCofSendsNothing() =>
        Assert.Empty(AppearanceManager.OutfitSendStillLinked(new[] { Hair, Top }, new HashSet<UUID>()));

    [Fact]
    public async Task BusyIsTrueOnlyWhileTheLockIsHeld()
    {
        // the wardrobe lock itself is shared with other tests running in parallel, so this checks the same predicate on a private lock
        var l = new SemaphoreSlim(1, 1);
        Assert.False(Program.LockHeld(l));
        await l.WaitAsync();
        Assert.True(Program.LockHeld(l));
        l.Release();
        Assert.False(Program.LockHeld(l));
    }

    [Fact]
    public void TheLibraryHasTheBusyHook()
    {
        var s = new AgentSettings();
        Assert.Null(s.OutfitSendBusy);
        bool busy = true;
        s.OutfitSendBusy = () => busy;
        Assert.True(s.OutfitSendBusy());
        busy = false;
        Assert.False(s.OutfitSendBusy());
    }
}
