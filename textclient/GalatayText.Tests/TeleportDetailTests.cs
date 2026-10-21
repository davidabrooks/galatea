using System; using System.IO; using System.Linq; using System.Text; using LibreMetaverse; using LibreMetaverse.Packets; using LibreMetaverse.StructuredData; using Xunit; using GalatayText;

/// 2026-10-16: a teleport refusal logs everything the grid sends ([teleport] detail: ...): reason, AlertInfo key + ExtraParams (UDP and EventQueue), and alerts within 5 s.
public class TeleportDetailTests
{
    static readonly DateTime T0 = new(2026, 10, 16, 19, 0, 0);

    [Fact]
    public void FailureLine_HasReasonAlertMsgExtraAndPath_OnOneLine()
    {
        var l = Program.TeleportDetailMsg("You are not allowed\ninto the destination.", "NoAccess", "{\"a\":1}", "udp");
        Assert.Equal("detail: reason=You are not allowed into the destination., alert_msg=NoAccess, extra={\"a\":1}, via=udp", l);
        Assert.DoesNotContain("\n", l);
        Assert.Equal("detail: reason=, alert_msg=, extra=, via=eq", Program.TeleportDetailMsg(null, null, null, "eq"));
    }

    [Fact]
    public void LongValuesAreCapped()
    {
        var l = Program.TeleportDetailMsg(new string('x', 5000), "k", "", "udp");
        Assert.True(l.Length < 800); Assert.Contains("...(cut)", l);
    }

    [Fact]
    public void ExtraParams_LlsdXmlAndBinaryBecomeJson_PlainTextStays_EmptyIsEmpty()
    {
        var map = new OSDMap { ["Reason"] = OSD.FromString("age"), ["Level"] = OSD.FromInteger(18) };
        var xml = Encoding.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(map));
        var bin = OSDParser.SerializeLLSDBinary(map);
        foreach (var raw in new[] { xml, bin })
        {
            var t = Program.TpExtraText(raw);
            Assert.Contains("\"Reason\"", t); Assert.Contains("age", t); Assert.Contains("18", t); Assert.DoesNotContain("\n", t);
        }
        Assert.Equal("", Program.TpExtraText(null)); Assert.Equal("", Program.TpExtraText(Array.Empty<byte>())); Assert.Equal("", Program.TpExtraText(new byte[] { 0 }));
        Assert.Equal("not llsd", Program.TpExtraText(Encoding.UTF8.GetBytes("not llsd\0")));
    }

    [Fact]
    public void AlertLine_ShowsKindTextIdExtraAndSignedOffset()
    {
        Assert.Equal("detail: alert=AlertMessage, text=Access denied, alert_msg=ParcelAgeRestricted, extra={}, dt=+1.5s",
            Program.TeleportAlertDetailMsg("AlertMessage", "Access denied", "ParcelAgeRestricted", "{}", 1.5));
        Assert.Contains("dt=-2.0s", Program.TeleportAlertDetailMsg("AgentAlertMessage", "x", "", "", -2.0));
    }

    [Fact]
    public void AlertAfterFailure_WithinWindow_IsLogged_LaterOneIsNot()
    {
        var w = new Program.TpAlertWindow();
        Assert.Empty(w.Failure(T0));
        var l = w.Alert(T0.AddSeconds(2), "AlertMessage", "Not allowed", "NoAccess", "");
        Assert.NotNull(l); Assert.Contains("text=Not allowed", l); Assert.Contains("dt=+2.0s", l);
        Assert.Null(w.Alert(T0.AddSeconds(6), "AlertMessage", "late", "", ""));
    }

    [Fact]
    public void AlertBeforeFailure_WithinWindow_IsLoggedWithTheFailure_OlderOnesAreNot_AndOnlyOnce()
    {
        var w = new Program.TpAlertWindow();
        Assert.Null(w.Alert(T0.AddSeconds(-10), "AlertMessage", "old", "", ""));
        Assert.Null(w.Alert(T0.AddSeconds(-3), "AgentAlertMessage", "recent", "", ""));
        var lines = w.Failure(T0);
        var one = Assert.Single(lines); Assert.Contains("text=recent", one); Assert.Contains("dt=-3.0s", one);
        Assert.Empty(w.Failure(T0.AddSeconds(1)));   // already reported
    }

    [Fact]
    public void AlertWithNoFailureNearby_IsNeverLogged()
    {
        var w = new Program.TpAlertWindow();
        Assert.Null(w.Alert(T0, "AlertMessage", "hello", "", ""));
    }

    [Fact]
    public void UdpPacket_AlertInfoBlocksSurviveTheWireAndReadBack()
    {   // the same field reads the handler uses, on a packet that went through ToBytes/parse
        var extra = Encoding.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(new OSDMap { ["Code"] = OSD.FromString("Age") }));
        var p = new TeleportFailedPacket
        {
            Info = { AgentID = UUID.Random(), Reason = Utils.StringToBytes("You are not allowed into the destination.") },
            AlertInfo = new[] { new TeleportFailedPacket.AlertInfoBlock { Message = Utils.StringToBytes("NoAccess"), ExtraParams = extra } }
        };
        var bytes = p.ToBytes(); int end = bytes.Length - 1; var back = (TeleportFailedPacket)Packet.BuildPacket(bytes, ref end, new byte[8192]);
        Assert.Equal("NoAccess", Utils.BytesToString(back.AlertInfo[0].Message));
        Assert.Contains("Age", Program.TpExtraText(back.AlertInfo[0].ExtraParams));
        Assert.Equal("You are not allowed into the destination.", Utils.BytesToString(back.Info.Reason));
    }

    [Fact]
    public void Source_HooksFourPaths_LogsOnlyAndNeverActs_AndIsWiredIn()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../galatay-text"));
        var src = File.ReadAllText(Path.Combine(root, "TeleportDetail.cs"));
        var code = string.Join("\n", src.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
        foreach (var k in new[] { "PacketType.TeleportFailed", "RegisterEventCallback(\"TeleportFailed\"", "PacketType.AlertMessage", "PacketType.AgentAlertMessage" })
            Assert.Contains(k, code);
        foreach (var bad in new[] { "Teleport(", "RequestTeleport", "Exec(", "SendPacket", "access_prefs", "UpdateAgent" }) Assert.DoesNotContain(bad, code);
        Assert.Contains("HookTeleportDetail();", File.ReadAllText(Path.Combine(root, "Program.cs")));
        Assert.Contains("TeleportDetail.cs", File.ReadAllText(Path.Combine(root, "../galatay-mcp/GalatayMcp.csproj")));
    }
}
