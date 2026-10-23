using GalatayText;
using Xunit;
// 2026-10-23 (David): the leash and the N° 9 collar, by form.
//   human: leashed with the collar in Stealth -> Stealth off, and it stays visible after the unleash.
//   android: the collar stays invisible (Stealth on); the chain goes invisible (Magic Leash); unleash -> Magic Leash RESET.
public class LeashStealthTests
{
    [Fact] public void Human_LeashShowsCollar_NoChainChange()
    {
        var p = Program.LeashCollarOnLeash(android: false);
        Assert.False(p.stealthWant); Assert.False(p.chainInvisible);
        Assert.False(Program.LeashChainResetOnUnleash(false, "leash", "the chain is gone"));   // nothing on unleash: stays visible
    }
    [Fact] public void Android_CollarStaysHidden_ChainInvisible_ResetOnUnleash()
    {
        var p = Program.LeashCollarOnLeash(android: true);
        Assert.True(p.stealthWant); Assert.True(p.chainInvisible);
        Assert.True(Program.LeashChainResetOnUnleash(true, "leash", "the chain is gone"));
        Assert.True(Program.LeashChainResetOnUnleash(true, "leash", "safeword MAYDAY"));
        Assert.False(Program.LeashChainResetOnUnleash(true, "leash", "replaced by leash to Someone"));   // still leashed
        Assert.False(Program.LeashChainResetOnUnleash(true, "follow", "collar said so"));
    }
    [Fact] public void AndroidOutfitNames()
    {
        Assert.True(Program.IsAndroidOutfit("Android")); Assert.True(Program.IsAndroidOutfit("Android Base"));
        Assert.False(Program.IsAndroidOutfit("Beach")); Assert.False(Program.IsAndroidOutfit(null));
    }
    [Fact] public void StealthPressOnlyWhenStateDiffers()
    {
        Assert.True(Program.StealthNeedsPress("☑ Stealth", true, want: false));
        Assert.False(Program.StealthNeedsPress("☐ Stealth", false, want: false));
        Assert.False(Program.StealthNeedsPress(null, false, want: false));
        Assert.True(Program.StealthNeedsPress("☐ Stealth", false, want: true));
        Assert.False(Program.StealthNeedsPress("☑ Stealth", true, want: true));
    }
    [Fact] public void MagicLeashButtons()
    {
        Assert.Equal("Magic Leash", Program.MenuPick(new[] { "Pose", "Magic Leash", "Leash" }, "Magic"));
        Assert.Equal("Invisible", Program.MenuPick(new[] { "Hearts", "Silk", "Invisible", "RESET" }, "Invisible", "Invis", "None"));
        Assert.Equal("RESET", Program.MenuPick(new[] { "Hearts", "Invisible", "RESET" }, "RESET", "Default", "Chain"));
        Assert.Null(Program.MenuPick(new[] { "Pose" }, "Magic"));
    }
    [Fact] public void WiredIntoLeashStartAndEnd()
    {
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "textclient/galatay-text/Leash.cs"));
        string Body(string sig) { var b = src[src.IndexOf(sig)..]; return b[..b.IndexOf("\n    }\n")]; }
        Assert.Contains("LeashCollarOnLeashRun()", Body("static void LeashStart("));
        Assert.Contains("LeashCollarOnUnleash(", Body("static void LeashEnd("));
    }
    static string Root() { var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
}
