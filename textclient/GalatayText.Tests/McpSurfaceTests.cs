using System;
using System.IO;
using System.Linq;
using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-26: the MCP look / seat_info / client_cmd surface (McpSurface.cs) and the look render options (vision/look_scope.py).</summary>
public class McpSurfaceTests
{
    static string Root() { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
    static string Src(params string[] p) => File.ReadAllText(Path.Combine(new[] { Root() }.Concat(p).ToArray()));

    // ---- look option words ----
    [Fact] public void LookOptions_PicksOutKnownKeysAndSmall_LeavesViewsAlone()
    {
        var (rest, opts, pre) = Program.LookOptions(new[] { "self", "left,front", "fast", "scope=seat", "floor=off", "small", "jpeg=70", "preset=hover-seat" });
        Assert.Equal(new[] { "self", "left,front", "fast" }, rest);
        Assert.Equal(new[] { "--scope=seat", "--floor=off", "--small", "--jpeg=70", "--preset=hover-seat" }, opts);
        Assert.Equal("hover-seat", pre);
    }
    [Fact] public void LookOptions_NoOptions_IsUntouched()
    {
        var (rest, opts, pre) = Program.LookOptions(new[] { "self", "back" });
        Assert.Equal(new[] { "self", "back" }, rest); Assert.Empty(opts); Assert.Null(pre);
    }
    [Fact] public void LookOptions_UnknownKeyStaysAWord()
    {
        var (rest, opts, _) = Program.LookOptions(new[] { "self", "bogus=1", "hide-attach=skull,chin", "cam-fov=40", "cam-height=0.4", "cam-margin=1.2", "cam-lens=35", "textures=off", "materials=off", "avatars=off", "scale=0.5", "size=320x400", "samples=4", "exposure=-1" });
        Assert.Equal(new[] { "self", "bogus=1" }, rest); Assert.Equal(12, opts.Count);
        Assert.Contains("--hide-attach=skull,chin", opts); Assert.Contains("--size=320x400", opts); Assert.Contains("--exposure=-1", opts);
    }
    [Fact] public void EveryPythonOptionHasAClientKey()
    {
        var py = Src("vision", "look_scope.py");
        foreach (var k in Program.LookOptKeys) Assert.Contains($"\"{k}\"", py.Replace("k == \"", "\"").Replace("(\"", "\""), StringComparison.Ordinal);
        foreach (var p in Program.LookPresets.Where(p => p != "photo-hd")) Assert.Contains($"\"{p}\":", py);
    }
    [Fact] public void Side_IsHerLeft()
    {
        Assert.Equal("left", Program.LookSelfViews("side")); Assert.Equal("front,left", Program.LookSelfViews("front,side")); Assert.Equal("left", Program.LookSelfViews("left,side"));
    }

    // ---- MCP look -> command line ----
    [Fact] public void McpLookLine_DefaultsToTheQuickSelfLook()
    {
        Assert.Equal("look self fast", Program.McpLookLine("self", "", true, false));
        Assert.Equal("look self fast", Program.McpLookLine(null, null, true, false));
        Assert.Equal("look self", Program.McpLookLine("self", "", false, false));
    }
    [Fact] public void McpLookLine_ViewsTargetsAndFlags()
    {
        Assert.Equal("look self left,front fast", Program.McpLookLine("self", "left, front", true, false));
        Assert.Equal("look self side fast", Program.McpLookLine("self", "side", true, false));
        Assert.Equal("look around fast far", Program.McpLookLine("around", "front", true, true));          // views are for self only
        Assert.Equal("look fast", Program.McpLookLine("view", "", true, false));
        Assert.Equal("look at Reiley Net Chair fast", Program.McpLookLine("Reiley Net Chair", "", true, false));
    }
    [Fact] public void McpLookLine_OptionsBecomeWords_EmptyOnesAreSkipped()
    {
        var o = new (string, string)[] { ("preset", "hover-seat"), ("scope", ""), ("floor", "on"), ("hide-attach", "skull, chin"), ("small", "true"), ("jpeg", "  ") };
        Assert.Equal("look self left fast preset=hover-seat floor=on hide-attach=skull,chin small", Program.McpLookLine("self", "left", true, false, o));
        Assert.Equal("look self fast", Program.McpLookLine("self", "", true, false, new[] { ("small", "false") }));
    }
    [Fact] public void McpLookLine_NewlinesCannotSmuggleASecondCommand()
    {
        Assert.DoesNotContain('\n', Program.McpLookLine("Bob\nsay hi", "", true, false));
    }
    [Fact] public void ParseLookOutput_ImagesSummaryAndStates()
    {
        var ok = Program.ParseLookOutput("/workspace/secondlife/vision/look/look-1-self-body-left.jpg\n/x/b.png\n{\"mode\": \"self\", \"total\": 4.2}\n");
        Assert.Equal(2, ok.images.Count); Assert.False(ok.failed); Assert.False(ok.pending); Assert.StartsWith("{", ok.summary);
        var pend = Program.ParseLookOutput("look: still rendering after 75 s (pid 1); the image path(s) will be logged as [look]");
        Assert.True(pend.pending); Assert.False(pend.failed); Assert.Empty(pend.images);
        Assert.True(Program.ParseLookOutput("look failed (exit 1): boom").failed);
        Assert.True(Program.ParseLookOutput("usage: look ...").failed);
    }

    // ---- seat_info geometry ----
    [Fact] public void SeatOffset_UnrotatedSeat_IsTheWorldDelta()
    {
        var (w, l) = Program.SeatOffset(new Vector3(10, 10, 20), Quaternion.Identity, new Vector3(10.2f, 9.9f, 20.45f));
        Assert.Equal(0.45f, w.Z, 3); Assert.Equal(0.2f, l.X, 3); Assert.Equal(-0.1f, l.Y, 3); Assert.Equal(0.45f, l.Z, 3);
    }
    [Fact] public void SeatOffset_RotatedSeat_GivesTheOffsetInTheSeatsAxes_ZUnchanged()
    {
        var yaw90 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(Math.PI / 2));
        var (w, l) = Program.SeatOffset(new Vector3(0, 0, 5), yaw90, new Vector3(0, 1, 5.3f));         // she is 1 m along world +Y = the seat's own +X when the seat is turned 90 deg
        Assert.Equal(1f, w.Y, 3); Assert.Equal(1f, l.X, 3); Assert.Equal(0f, l.Y, 3); Assert.Equal(0.3f, l.Z, 3); Assert.Equal(0.3f, w.Z, 3);
    }
    [Fact] public void SeatOffset_AdjustZ_ChangesOnlyZ()
    {
        var before = Program.SeatOffset(new Vector3(1, 2, 3), Quaternion.Identity, new Vector3(1, 2, 3.20f));
        var after = Program.SeatOffset(new Vector3(1, 2, 3), Quaternion.Identity, new Vector3(1, 2, 3.25f));
        Assert.Equal(0.05f, after.world.Z - before.world.Z, 3); Assert.Equal(before.world.X, after.world.X); Assert.Equal(before.local.Y, after.local.Y);
    }

    // ---- client_cmd allow-list ----
    [Theory]
    [InlineData("look", "look")] [InlineData("look self left", "look self left")] [InlineData("LOOK self side preset=hover-seat", "look self side preset=hover-seat")]
    [InlineData("hover", "hover")] [InlineData("hover get", "hover get")] [InlineData("Hover CHECK", "hover check")]
    [InlineData("height", "height")] [InlineData("diag", "diag")]
    [InlineData("posehover check", "hover check")] [InlineData("posehover status", "posehover status")] [InlineData("posehover", "posehover status")]
    public void ClientCmdGate_AllowsTheReadOnlyCommands(string line, string run)
    {
        var (ok, l, err) = Program.ClientCmdGate(line); Assert.True(ok, err); Assert.Equal(run, l);
    }
    [Theory]
    [InlineData("say hello")] [InlineData("im David hi")] [InlineData("pay object x 5")] [InlineData("logout")] [InlineData("login")] [InlineData("sit abc")] [InlineData("stand")]
    [InlineData("hover set 0.1")] [InlineData("hover repin")] [InlineData("height now")] [InlineData("posehover set 1")] [InlineData("photo hd self")] [InlineData("rlv clear")]
    [InlineData("")] [InlineData("   ")] [InlineData("look self\nsay hi")] [InlineData("teleport Home 1 1 1")] [InlineData("accept")] [InlineData("wear add x")] [InlineData("touch abc")]
    public void ClientCmdGate_RefusesEverythingElse(string line)
    {
        var (ok, l, err) = Program.ClientCmdGate(line); Assert.False(ok); Assert.Null(l); Assert.False(string.IsNullOrEmpty(err));
    }
    [Fact] public void ClientCmdGate_RefusesAnOverlongLine() => Assert.False(Program.ClientCmdGate("look " + new string('a', 500)).ok);

    // ---- wiring ----
    [Fact] public void TheConnectorDeclaresTheThreeReadOnlyTools()
    {
        var m = Src("textclient", "galatay-mcp", "McpProgram.cs");
        foreach (var n in new[] { "look", "seat_info", "client_cmd" }) Assert.Contains($"[McpServerTool(Name = \"{n}\", ReadOnly = true)", m);
        Assert.Contains("Core.LookObj(", m); Assert.Contains("Core.SeatInfoObj()", m); Assert.Contains("Core.ClientCmd(command)", m);
        Assert.Contains("McpSurface.cs", Src("textclient", "galatay-mcp", "GalatayMcp.csproj"));
    }
    [Fact] public void ClientCmd_RunsOnlyTheGatedLine()
    {
        var s = Src("textclient", "galatay-text", "McpSurface.cs"); var body = s[s.IndexOf("public static async Task<string> ClientCmd(")..];
        Assert.Contains("var (ok, run, err) = ClientCmdGate(line);", body); Assert.Contains("Exec(run)", body); Assert.DoesNotContain("Exec(line)", body);
    }
    [Fact] public void LookCmd_ReusesTheRenderer_AndForwardsOptionsOnlyForSelf()
    {
        var l = Src("textclient", "galatay-text", "Look.cs");
        Assert.Contains("foreach (var o in lookOpts) psi.ArgumentList.Add(o);", l); Assert.Contains("if (mode == \"self\" && hd == null) foreach (var o in lookOpts)", l);
        Assert.Contains("if (pre == \"photo-hd\") return await PhotoCmd(new[] { \"hd\", \"self\" });", l);   // photo-hd = the 4K `photo hd`, current behaviour
        Assert.DoesNotContain("blender", Src("textclient", "galatay-text", "McpSurface.cs"), StringComparison.OrdinalIgnoreCase);   // no new renderer
    }
    [Fact] public void QuickExposureAndFloorAreUntouched()
    {
        var r = Src("vision", "cloud-render", "render_mesh.py");
        Assert.Contains("QUICK_EXPOSURE = -1.5", r); Assert.Contains("self_floor = mode == \"self\" and not hd", Src("vision", "look.py"));
    }
}
