using System;
using GalatayText;
using Xunit;

public class VmForkWatchTests   // 2026-11-02 13:02 / 14:43 PT drops after host VM snapshots
{
    [Fact] public void CountsForkLines()
    {
        var d = "[1.0] x\n[5.2] random: crng reseeded due to virtual machine fork\n[9.1] random: crng reseeded due to virtual machine fork\n";
        Assert.Equal(2, Program.CountVmForks(d));
        Assert.Equal(0, Program.CountVmForks(""));
    }
    [Fact] public void RiseOnlyOnIncrease()
    {
        Assert.True(Program.VmForkRose(109, 110));
        Assert.False(Program.VmForkRose(110, 110));
        Assert.False(Program.VmForkRose(110, 3));   // ring buffer rolled over
        Assert.False(Program.VmForkRose(-1, 5));
    }
    [Fact] public void FastRelogin_OnlyAfterARecentFork()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), Program.EmbeddedReloginDelayAfter(1, false, true));
        Assert.Equal(TimeSpan.FromSeconds(30), Program.EmbeddedReloginDelayAfter(1, false, false));
        Assert.Equal(Program.EmbeddedReloginDelay(2, false), Program.EmbeddedReloginDelayAfter(2, false, true));
        Assert.Equal(Program.EmbeddedReloginDelay(1, true), Program.EmbeddedReloginDelayAfter(1, true, true));
        Assert.True(Program.VmForkRecent(1000, 1000 + 50_000));
        Assert.False(Program.VmForkRecent(1000, 1000 + 301_000));
    }
    [Fact] public void SessionDead_WhenNoPacketSinceFork()
    {
        Assert.True(Program.VmForkSessionDead(true, 100, 200));
        Assert.False(Program.VmForkSessionDead(true, 300, 200));
        Assert.True(Program.VmForkSessionDead(false, 300, 200));
    }
    [Fact] public void LogWording_AndWiring()
    {
        Assert.Equal("host VM snapshot (fork 109->110)", Program.VmForkLabel(109, 110));
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory); while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent;
        var rr = System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "textclient/galatay-text/RegionRestart.cs"));
        var prog = System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, "textclient/galatay-text/Program.cs"));
        Assert.Contains("EmbeddedReloginDelayAfter(n, kicked, afterFork)", rr);
        Assert.Contains("EmbeddedReloginDecision(DeliberateStopRequested()", rr);
        Assert.Contains("await LoginAsync()", rr);
        Assert.Contains("StartVmForkWatch();", prog);
    }
}
