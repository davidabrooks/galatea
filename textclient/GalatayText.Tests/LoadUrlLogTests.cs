using System; using System.IO; using System.Linq; using LibreMetaverse; using Xunit; using GalatayText;

/// 2026-10-15: llLoadURL (CasperSafe CTRL PANEL) is logged as [loadurl] + last-loadurl.txt (600), never as an event/console/webhook line.
public class LoadUrlLogTests
{
    static readonly UUID Obj = new("121f3ddc-8c22-0efc-feb3-ed2d402d80e4"), Own = new("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void LineHasObjectOwnerMessageAndFullUrl_OnOneLine()
    {
        var m = Program.LoadUrlMsg("CasperSafe\nv1.48", Obj, Own, false, "Open the\r\ncontrol panel", "https://www.casperpanel.com/safe/abc?token=XYZ&a=1");
        Assert.Equal($"object 'CasperSafe v1.48' {Obj} owner {Own} message 'Open the  control panel' url https://www.casperpanel.com/safe/abc?token=XYZ&a=1", m);
        Assert.DoesNotContain("\n", m); Assert.Contains("(group)", Program.LoadUrlMsg("o", Obj, Own, true, "m", "u"));
    }

    [Fact]
    public void FileTextAndMode()
    {
        var t = Program.LoadUrlFileText(new DateTime(2026, 10, 15, 12, 0, 0), "Orb", Obj, Own, false, "msg", "https://x.test/?t=1");
        Assert.Equal($"time: 2026-10-15 12:00:00\nobject: Orb ({Obj})\nowner: {Own}\nmessage: msg\nurl: https://x.test/?t=1\n", t);
        var dir = Path.Combine(Path.GetTempPath(), "lu-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "last-loadurl.txt");
            Program.WriteLastLoadUrl(f, "one\n"); Program.WriteLastLoadUrl(f, t);   // second write replaces the first
            Assert.Equal(t, File.ReadAllText(f)); Assert.Equal(new[] { f }, Directory.GetFiles(dir));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(f));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NeverGoesThroughLog_SoNoEventsConsoleOrWebhook()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../galatay-text"));
        var src = File.ReadAllText(Path.Combine(root, "LoadUrlLog.cs"));
        var code = string.Join("\n", src.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
        Assert.DoesNotContain("Log(\"loadurl\", $", code); Assert.DoesNotContain("Events.Enqueue", code); Assert.DoesNotContain("Console.", code);
        Assert.Contains("File.AppendAllText(LogPath", code);
        Assert.Contains("HookLoadUrl();", File.ReadAllText(Path.Combine(root, "Program.cs")));
    }
    [Fact]
    public void SiblingLogFiles_AreNextToTheLog_NotInTheProcessCwd()
    {   // 2026-10-15: static fields of a partial class initialise in no fixed order; these two read LogPath too early and landed in the cwd
        foreach (var f in new[] { Program.LastLoadUrlFile, Program.AnimEventsFile })
            Assert.True(Path.IsPathRooted(f) && Path.GetDirectoryName(f) is { Length: > 0 }, f);
    }
}
