// UDP datagrams the KERNEL dropped before the client read them (2026-10-14).
// David's nearby chat "get a little closer babe" (12:24-12:26 PT) never reached her: two Blender HD renders hit the
// box-wide OOM killer at 12:24:54 and 12:25:28, the whole box stalled under memory pressure, her UDP reads stopped for
// ~28 s (the 10 s seat-anim RESTARTs jumped 12:24:27 -> 12:24:55) and the 208 KB default socket buffer overflowed:
// /proc/net/snmp Udp RcvbufErrors = 1327. ChatFromSimulator is not resent, so the line was gone for good.
// Fix: an 8 MB SO_RCVBUF (needs net.core.rmem_max >= 8 MB; scripts/sysctl-galatay-udp.conf) and a log line whenever
// the kernel counter rises, so a lost line is visible instead of silent.
namespace GalatayText;

public static partial class Program
{
    internal const int UdpSockBufWanted = 8 * 1024 * 1024;
    static long udpRcvbufLast = -1; static DateTime udpRcvbufAt = DateTime.MinValue;

    /// <summary>Udp RcvbufErrors from /proc/net/snmp text (header line + value line); -1 if absent.</summary>
    internal static long ParseUdpRcvbufErrors(string snmp)
    {
        if (string.IsNullOrEmpty(snmp)) return -1;
        var lines = snmp.Split('\n');
        for (int i = 0; i + 1 < lines.Length; i++)
        {
            if (!lines[i].StartsWith("Udp: ") || !lines[i + 1].StartsWith("Udp: ")) continue;
            var h = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var v = lines[i + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int k = Array.IndexOf(h, "RcvbufErrors");
            if (k > 0 && k < v.Length && long.TryParse(v[k], out var n)) return n;
            return -1;
        }
        return -1;
    }

    /// <summary>Pure: the log text for a counter change, or null (first sample, no change, counter reset).</summary>
    internal static string UdpDropNote(long prev, long now, double secs)
    {
        if (prev < 0 || now < 0 || now <= prev) return null;
        return $"kernel dropped {now - prev} incoming UDP datagram(s) (socket buffer full; box-wide counter, may include other programs) in the last {secs:F0} s - chat or IMs in that window may be lost";
    }

    // Raw ChatFromSimulator arrivals from other AVATARS, logged before any client filtering, so a missing [chat] line
    // can be told apart: no [chat-raw] = the sim/network never delivered it; [chat-raw] without [chat] = our filter.
    static void HookRawChat()
    {
        client.Network.RegisterCallback(LibreMetaverse.Packets.PacketType.ChatFromSimulator, (s, e) =>
        {
            try
            {
                var c = ((LibreMetaverse.Packets.ChatFromSimulatorPacket)e.Packet).ChatData;
                if (c.SourceType != 1 || c.SourceID == client.Self.AgentID || c.ChatType is 4 or 5) return;   // agents only; no typing
                Log("chat-raw", $"from {LibreMetaverse.Utils.BytesToString(c.FromName)} type {c.ChatType} audible {c.Audible} len {c.Message?.Length ?? 0} via {e.Simulator?.Name}");
            }
            catch { }
        });
    }

    static void CheckUdpDrops()
    {
        try
        {
            long n = ParseUdpRcvbufErrors(File.ReadAllText("/proc/net/snmp"));
            var now = DateTime.Now;
            var note = UdpDropNote(udpRcvbufLast, n, (now - udpRcvbufAt).TotalSeconds);
            if (note != null) Log("net", note + $"; total {n}; SO_RCVBUF wanted {UdpSockBufWanted / 1048576} MB");
            udpRcvbufLast = n; udpRcvbufAt = now;
        }
        catch { }
    }
}
