// OfflineIm.cs (2026-09-27): fetch the IMs Second Life stored while Galatay was logged out (or her session was dead,
// e.g. during a box pause) at EVERY login, and run them through the normal IM handler (HandleIm in Program.cs):
// logged as [im] ... [offline, sent <time> PT], queued for poll_events, and sent to the chat webhook (text prefixed
// "[offline IM, sent ...]"). Old messages never trigger anything automatic: no wander chat pause/approach, no greeting,
// no lure accept, no reply.
// Why: LibreMetaverse only fetches them when asked (AgentManager.RetrieveInstantMessagesAsync is never called by the
// library itself); SL does not push stored IMs to a client that doesn't ask. Viewers do it right after login
// (llimprocessing.cpp requestOfflineMessages: ReadOfflineMsgs capability, else the UDP RetrieveInstantMessages packet).
// SL deletes the stored messages once they are read, so each one is delivered once.
using System.Globalization;
using LibreMetaverse;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;

namespace GalatayText;

public static partial class Program
{
    static int offlineImCount;                 // offline-flagged IMs seen in this process (any dialog)
    static string offlineImLast = "-";         // last fetch summary for 'offlineim status'
    static readonly TimeZoneInfo PacificTz = FindPacific();
    static TimeZoneInfo FindPacific()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); } catch { return TimeZoneInfo.Local; }
    }

    // SL timestamps are unix seconds; the library stores them as DateTime *ticks* (new DateTime(seconds)).
    static DateTimeOffset? OfflineSentAt(InstantMessage im)
    {
        long v = im.Timestamp.Ticks;
        if (v < 946684800L || v > 4102444800L) return null; // 2000..2100 as unix seconds, else unknown
        return DateTimeOffset.FromUnixTimeSeconds(v);
    }
    static string OfflineSentText(InstantMessage im)
    {
        var t = OfflineSentAt(im);
        if (t == null) return "sent time unknown";
        var pt = TimeZoneInfo.ConvertTime(t.Value, PacificTz);
        return "sent " + pt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " PT";
    }

    // Called once per successful login (LoginAsync). Waits for the region capabilities, then asks for stored IMs.
    static async Task FetchOfflineIms()
    {
        try
        {
            Uri cap = null;
            for (int i = 0; i < 30 && LoggedIn; i++)   // seed caps usually resolve within a few seconds
            {
                cap = client.Network.CurrentSim?.Caps?.CapabilityURI("ReadOfflineMsgs");
                if (cap != null) break;
                await Task.Delay(1000);
            }
            if (!LoggedIn) return;
            int before = offlineImCount;
            if (cap == null)
            {
                SendRetrieveImsPacket();
                offlineImLast = $"{DateTime.Now:HH:mm:ss} legacy RetrieveInstantMessages sent (no ReadOfflineMsgs cap after 30 s)";
                Log("offline-im", "no ReadOfflineMsgs capability: sent the UDP RetrieveInstantMessages request (stored IMs arrive as normal IMs flagged offline)");
                _ = Task.Run(async () => { await Task.Delay(30000); Log("offline-im", $"legacy fetch: {offlineImCount - before} offline IM(s) arrived in 30 s"); });
                return;
            }
            var (resp, data) = await client.HttpCapsClient.GetAsync(cap, CancellationToken.None);
            int status = (int)(resp?.StatusCode ?? 0);
            if (resp == null || !resp.IsSuccessStatusCode)
            {
                SendRetrieveImsPacket();
                offlineImLast = $"{DateTime.Now:HH:mm:ss} ReadOfflineMsgs HTTP {status}; legacy request sent";
                Log("offline-im", $"ReadOfflineMsgs failed (HTTP {status}): sent the UDP RetrieveInstantMessages request instead");
                return;
            }
            var list = ParseOfflineIms(data, out var err);
            if (list == null)
            {
                SendRetrieveImsPacket();
                offlineImLast = $"{DateTime.Now:HH:mm:ss} ReadOfflineMsgs unreadable ({err}); legacy request sent";
                Log("offline-im", $"ReadOfflineMsgs reply not understood ({err}, {data?.Length ?? 0} bytes): sent the UDP RetrieveInstantMessages request instead");
                return;
            }
            offlineImLast = $"{DateTime.Now:HH:mm:ss} ReadOfflineMsgs HTTP {status}: {list.Count} stored message(s)";
            Log("offline-im", $"ReadOfflineMsgs (HTTP {status}): {list.Count} stored message(s) waiting{(list.Count > 0 ? "; handing them to the normal IM handler (no auto-actions for old messages)" : "")}");
            foreach (var im in list.OrderBy(m => m.Timestamp.Ticks))
            {
                try { HandleIm(im); } catch (Exception ex) { Log("offline-im", "handler error: " + ex.Message); }
            }
        }
        catch (Exception ex)
        {
            offlineImLast = $"{DateTime.Now:HH:mm:ss} error {ex.GetBaseException().GetType().Name}";
            Log("offline-im", "fetch error: " + ex.GetBaseException().Message + "; sending the UDP RetrieveInstantMessages request");
            try { if (LoggedIn) SendRetrieveImsPacket(); } catch { }
        }
    }

    static void SendRetrieveImsPacket()
    {
        var p = new RetrieveInstantMessagesPacket();
        p.AgentData.AgentID = client.Self.AgentID; p.AgentData.SessionID = client.Self.SessionID;
        client.Network.SendPacket(p);
    }

    // Same decoding as LibreMetaverse's OfflineMessageHandlerCallback (bare array wrapping the message array, or a
    // map with "messages"). Returns null if the reply isn't in either shape.
    static List<InstantMessage> ParseOfflineIms(byte[] data, out string err)
    {
        err = null;
        OSD result;
        try { result = OSDParser.Deserialize(data ?? Array.Empty<byte>()); } catch (Exception ex) { err = "parse: " + ex.GetType().Name; return null; }
        OSDArray messages = null;
        if (result is OSDArray top && top.Count > 0 && top[0] is OSDArray inner) messages = inner;
        else if (result is OSDArray top0 && top0.Count == 0) messages = new OSDArray();
        else if (result is OSDMap m && m.TryGetValue("messages", out var mo) && mo is OSDArray named) messages = named;
        if (messages == null) { err = "unexpected shape " + (result?.Type.ToString() ?? "null"); return null; }
        var list = new List<InstantMessage>();
        foreach (var osd in messages)
        {
            if (osd is not OSDMap msg) continue;
            InstantMessage im = default;
            im.FromAgentID = msg["from_agent_id"].AsUUID();
            im.FromAgentName = msg["from_agent_name"].AsString();
            im.ToAgentID = msg["to_agent_id"].AsUUID();
            im.RegionID = msg["region_id"].AsUUID();
            im.Dialog = (InstantMessageDialog)msg["dialog"].AsInteger();
            im.IMSessionID = msg["transaction-id"].AsUUID();
            im.Timestamp = new DateTime(Math.Max(0L, msg["timestamp"].AsLong()));   // unix seconds as ticks, like the library
            im.Message = msg["message"].AsString();
            im.Offline = InstantMessageOnline.Offline;                               // everything from this cap is stored/offline
            im.ParentEstateID = msg.ContainsKey("parent_estate_id") ? msg["parent_estate_id"].AsUInteger() : 1;
            im.Position = msg.ContainsKey("position") ? msg["position"].AsVector3() : new Vector3((float)msg["local_x"].AsReal(), (float)msg["local_y"].AsReal(), (float)msg["local_z"].AsReal());
            im.BinaryBucket = msg.ContainsKey("binary_bucket") ? msg["binary_bucket"].AsBinary() : new byte[] { 0 };
            im.GroupIM = msg.ContainsKey("from_group") && msg["from_group"].AsBoolean();
            list.Add(im);
        }
        return list;
    }

    static string OfflineImStatus() => $"offline IMs: fetched at every login (ReadOfflineMsgs cap, else UDP RetrieveInstantMessages); last fetch: {offlineImLast}; offline-flagged IMs seen by this process: {offlineImCount}";

    // offline self-test: decoding + time conversion + the 'no auto-actions' gate. Touches nothing in SL, sends no webhook.
    static string OfflineImSelfTest()
    {
        var sb = new System.Text.StringBuilder(); int pass = 0, fail = 0;
        void Check(string name, bool ok) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {name}"); }
        long ts = 1790500000; // 2026-09-27 ~01:26 PT
        OSDMap Msg(int dialog, string text) => new OSDMap
        {
            ["from_agent_id"] = OSD.FromUUID(new UUID("11111111-2222-3333-4444-555555555555")), ["from_agent_name"] = OSD.FromString("Test Resident"),
            ["to_agent_id"] = OSD.FromUUID(UUID.Zero), ["region_id"] = OSD.FromUUID(UUID.Zero), ["dialog"] = OSD.FromInteger(dialog),
            ["transaction-id"] = OSD.FromUUID(UUID.Zero), ["timestamp"] = OSD.FromInteger((int)ts), ["message"] = OSD.FromString(text),
            ["local_x"] = OSD.FromReal(1), ["local_y"] = OSD.FromReal(2), ["local_z"] = OSD.FromReal(3), ["from_group"] = OSD.FromBoolean(false),
        };
        var arrShape = new OSDArray { new OSDArray { Msg(0, "hello while you were away"), Msg(22, "come here") } };
        var mapShape = new OSDMap { ["messages"] = new OSDArray { Msg(0, "map shape") } };
        var l1 = ParseOfflineIms(OSDParser.SerializeLLSDXmlBytes(arrShape), out var e1);
        Check("array shape decodes 2 messages", l1 != null && l1.Count == 2 && e1 == null);
        Check("fields: name/dialog/text/offline", l1 != null && l1[0].FromAgentName == "Test Resident" && l1[0].Dialog == InstantMessageDialog.MessageFromAgent
              && l1[0].Message == "hello while you were away" && l1[0].Offline == InstantMessageOnline.Offline && l1[1].Dialog == InstantMessageDialog.RequestTeleport);
        Check("position from local_x/y/z", l1 != null && l1[0].Position == new Vector3(1, 2, 3));
        var l2 = ParseOfflineIms(OSDParser.SerializeLLSDXmlBytes(mapShape), out var e2);
        Check("map shape decodes 1 message", l2 != null && l2.Count == 1 && l2[0].Message == "map shape");
        var l3 = ParseOfflineIms(OSDParser.SerializeLLSDXmlBytes(new OSDArray()), out var e3);
        Check("empty array = 0 messages", l3 != null && l3.Count == 0);
        var l4 = ParseOfflineIms(OSDParser.SerializeLLSDXmlBytes(OSD.FromString("nope")), out var e4);
        Check("bad shape -> null (legacy fallback)", l4 == null && e4 != null);
        Check("sent time decoded as PT", l1 != null && OfflineSentText(l1[0]) == "sent " + TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(ts), PacificTz).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " PT");
        Check("zero timestamp -> unknown", OfflineSentText(default) == "sent time unknown");
        Check("gate: offline IM -> no wander reaction", l1 != null && !ImAutoReact(l1[0]));
        Check("gate: offline lure -> never accepted", l1 != null && !LureMayAutoAccept(l1[1]));
        var live = l1 != null ? l1[0] : default; live.Offline = InstantMessageOnline.Online;
        Check("gate: live IM -> wander reaction allowed", ImAutoReact(live));
        sb.Insert(0, $"offlineim selftest: {pass}/{pass + fail} passed\n");
        return sb.ToString().TrimEnd();
    }
    static bool IsOfflineIm(InstantMessage im) => im.Offline == InstantMessageOnline.Offline;
    static bool ImAutoReact(InstantMessage im) => !IsOfflineIm(im);        // wander chat pause / approach / greeting logic
    static bool LureMayAutoAccept(InstantMessage im) => !IsOfflineIm(im);  // stored teleport offers are stale
}
