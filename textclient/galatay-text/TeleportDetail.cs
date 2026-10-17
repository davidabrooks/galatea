// TeleportDetail.cs (2026-10-16, David: Astra and Pino Island refuse her teleports with only "You are not allowed into the destination", and the
// client logged just the reason text). The grid sends more with a failure: the AlertInfo block (message key such as "NoAccess" plus ExtraParams) and
// often a separate AlertMessage / AgentAlertMessage. This file only LOGS all of it, as "[teleport] detail: ..." lines in textclient.log (and the
// console / events queue like every Log() line). It changes no behaviour: no retry, no maturity change, nothing sent.
//   [teleport] detail: reason=<text>, alert_msg=<key>, extra=<params>, via=udp|eq
//   [teleport] detail: alert=AlertMessage|AgentAlertMessage, text=<text>, alert_msg=<id>, extra=<params>, dt=<+/-s> vs the failure
// It is hooked on the receiving side (extra callbacks next to LibreMetaverse's own), so no LibreMetaverse patch is needed.
using LibreMetaverse;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;

namespace GalatayText;

public static partial class Program
{
    internal const double TeleportAlertWindowSec = 5.0;   // an alert this close before/after a failure is logged with it
    internal const int TeleportDetailMaxChars = 600;

    // pure (test): one value on one line, capped
    internal static string TpOneLine(string? s)
    {
        var t = (s ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\0", "").Trim();
        return t.Length > TeleportDetailMaxChars ? t[..TeleportDetailMaxChars] + "...(cut)" : t;
    }

    // pure (test): ExtraParams bytes from a packet -> readable text. LLSD (xml / notation / binary) becomes compact JSON; anything else shows as text.
    internal static string TpExtraText(byte[]? raw)
    {
        if (raw == null || raw.Length == 0) return "";
        // a trailing NUL is common in SL string blocks
        var len = raw.Length; while (len > 0 && raw[len - 1] == 0) len--;
        if (len == 0) return "";
        var data = len == raw.Length ? raw : raw[..len];
        try { var osd = OSDParser.Deserialize(data); if (osd.Type != OSDType.Unknown) return TpOneLine(osd.ToString()); } catch { }
        // binary LLSD whose header the library's sniffing does not recognise: skip a "<?llsd/binary?>" line and read the body
        try
        {
            if (data[0] == (byte)'<' && data.Length > 2 && data[1] == (byte)'?')
            {
                var nl = Array.IndexOf(data, (byte)'\n');
                if (nl > 0 && nl + 1 < data.Length) { var osd = OSDParser.DeserializeLLSDBinary(data[(nl + 1)..]); if (osd.Type != OSDType.Unknown) return TpOneLine(osd.ToString()); }
            }
        }
        catch { }
        var text = System.Text.Encoding.UTF8.GetString(data);
        return TpOneLine(new string(text.Select(c => char.IsControl(c) && c != '\n' && c != '\r' ? '.' : c).ToArray()));
    }

    // pure (test): the failure line (after "[teleport] ")
    internal static string TeleportDetailMsg(string? reason, string? alertMsg, string? extra, string via) =>
        $"detail: reason={TpOneLine(reason)}, alert_msg={TpOneLine(alertMsg)}, extra={TpOneLine(extra)}, via={via}";

    // pure (test): an alert line (after "[teleport] "); dtSec = alert time minus failure time (negative = before the failure)
    internal static string TeleportAlertDetailMsg(string kind, string? text, string? alertMsg, string? extra, double dtSec) =>
        $"detail: alert={kind}, text={TpOneLine(text)}, alert_msg={TpOneLine(alertMsg)}, extra={TpOneLine(extra)}, dt={dtSec:+0.0;-0.0;+0.0}s";

    // pure (test): pairs alerts and failures that are within TeleportAlertWindowSec of each other, in either order.
    internal sealed class TpAlertWindow
    {
        readonly List<(DateTime At, string Kind, string Text, string Msg, string Extra)> alerts = new();
        DateTime lastFail = DateTime.MinValue;

        // an alert arrived: returns its line when a failure was seen within the window before it; otherwise remembers it for a failure that follows
        public string? Alert(DateTime now, string kind, string text, string msg, string extra)
        {
            alerts.RemoveAll(a => (now - a.At).TotalSeconds > TeleportAlertWindowSec);
            alerts.Add((now, kind, text, msg, extra));
            if (lastFail != DateTime.MinValue && (now - lastFail).TotalSeconds is >= 0 and <= TeleportAlertWindowSec)
                return TeleportAlertDetailMsg(kind, text, msg, extra, (now - lastFail).TotalSeconds);
            return null;
        }

        // a failure arrived: returns the lines of alerts that came just before it
        public List<string> Failure(DateTime now)
        {
            lastFail = now;
            var lines = new List<string>();
            foreach (var a in alerts)
            {
                var dt = (a.At - now).TotalSeconds;
                if (dt >= -TeleportAlertWindowSec && dt <= 0) lines.Add(TeleportAlertDetailMsg(a.Kind, a.Text, a.Msg, a.Extra, dt));
            }
            alerts.Clear();
            return lines;
        }
    }

    static readonly TpAlertWindow tpWindow = new();
    static readonly object tpLock = new();

    static void TpLogFailure(string reason, string alertMsg, string extra, string via)
    {
        List<string> before;
        lock (tpLock) before = tpWindow.Failure(DateTime.Now);
        Log("teleport", TeleportDetailMsg(reason, alertMsg, extra, via));
        foreach (var l in before) Log("teleport", l);
    }

    static void TpLogAlert(string kind, string text, string msg, string extra)
    {
        string? line;
        lock (tpLock) line = tpWindow.Alert(DateTime.Now, kind, text, msg, extra);
        if (line != null) Log("teleport", line);
    }

    static void HookTeleportDetail()
    {
        // UDP TeleportFailed: Info.Reason + every AlertInfo block (Message = key, ExtraParams = LLSD)
        client.Network.RegisterCallback(PacketType.TeleportFailed, (s, e) =>
        {
            try
            {
                var p = (TeleportFailedPacket)e.Packet;
                var reason = Utils.BytesToString(p.Info.Reason);
                var blocks = p.AlertInfo ?? Array.Empty<TeleportFailedPacket.AlertInfoBlock>();
                if (blocks.Length == 0) { TpLogFailure(reason, "", "", "udp"); return; }
                foreach (var b in blocks) TpLogFailure(reason, TpOneLine(Utils.BytesToString(b.Message)), TpExtraText(b.ExtraParams), "udp");
            }
            catch (Exception ex) { Log("teleport", "detail handler error (udp): " + ex.GetType().Name); }
        });
        // EventQueue TeleportFailed: Reason + AlertInfo Message (MessageKey) + ExtraParams, all of which LibreMetaverse's own handler drops
        client.Network.RegisterEventCallback("TeleportFailed", (key, msg, sim) =>
        {
            try { var m = (LibreMetaverse.Messages.Linden.TeleportFailedMessage)msg; TpLogFailure(m.Reason, m.MessageKey, m.ExtraParams, "eq"); }
            catch (Exception ex) { Log("teleport", "detail handler error (eq): " + ex.GetType().Name); }
        });
        client.Network.RegisterCallback(PacketType.AlertMessage, (s, e) =>
        {
            try
            {
                var p = (AlertMessagePacket)e.Packet;
                var text = Utils.BytesToString(p.AlertData.Message);
                var b = p.AlertInfo is { Length: > 0 } ? p.AlertInfo[0] : null;
                TpLogAlert("AlertMessage", text, b == null ? "" : Utils.BytesToString(b.Message), b == null ? "" : TpExtraText(b.ExtraParams));
            }
            catch (Exception ex) { Log("teleport", "detail handler error (alert): " + ex.GetType().Name); }
        });
        client.Network.RegisterCallback(PacketType.AgentAlertMessage, (s, e) =>
        {
            try { TpLogAlert("AgentAlertMessage", Utils.BytesToString(((AgentAlertMessagePacket)e.Packet).AlertData.Message), "", ""); }
            catch (Exception ex) { Log("teleport", "detail handler error (agent alert): " + ex.GetType().Name); }
        });
    }
}
