// ConnectionDiag.cs (2026-11-20): at 10:15:29 PT LibreMetaverse dropped the Peronaut session with a NetworkTimeout (no packet
// processed for 30-60 s). There was no VM snapshot before it, no Linden incident and no region restart, and the MCP connector left
// no trace of the 75 s before the drop: the watchdog only ran in daemon mode, and the one hint was a run/last-online.txt write
// due at ~10:14:56 that never happened (the 1 s Ticker was blocked). Now, in every mode:
//  - per region: when a packet last arrived on the socket (raw count, LibreMetaverse Stats) and when one was last processed
//    (Default packet callback). One [net-stats] line a minute while logged in, and the same summary on the NetworkTimeout
//    disconnect line. Raw arriving but not processed = our process stalled; neither = the sim or the network went quiet.
//  - the Ticker times each step: a step over 5 s is logged when it ends, and the watchdog thread logs a step still running
//    after 5 s, so a step that never returns shows up too.
//  - LastOnlineHeartbeat runs on its own timer, so a blocked Ticker no longer stops run/last-online.txt.
using System.Collections.Concurrent;
using System.Globalization;
using LibreMetaverse;
using LibreMetaverse.Packets;

namespace GalatayText;

public static partial class Program
{
    internal const double TickerStepSlowS = 5;
    internal const int PacketStatsEverySec = 60;
    static readonly ConcurrentDictionary<ulong, long> simProcessedTicks = new();
    static readonly ConcurrentDictionary<ulong, (long Count, long Ticks)> simRawRecv = new();
    static readonly ConcurrentDictionary<ulong, string> simNames = new();
    static long currentSimHandle, lastPacketStatsTicks;
    static volatile string tickerStep;
    static long tickerStepStartTicks;
    static int tickerStepReported;
    static Timer lastOnlineTimer;
    static readonly object lastOnlineTimerLock = new();

    /// <summary>One row of the per-region packet summary (ages in seconds, null = nothing seen yet).</summary>
    internal readonly record struct SimPacketAge(string Name, bool Current, double? RawAgeS, double? ProcessedAgeS, long RawCount);

    /// <summary>Pure (tested): "Peronaut* rx 0.4 s / processed 0.4 s ago (12345 pkts); Terric ..." (current region first, marked *).</summary>
    internal static string FormatPacketAges(IEnumerable<SimPacketAge> sims)
    {
        static string Age(double? s) => s == null ? "never" : s.Value.ToString("F1", CultureInfo.InvariantCulture) + " s";
        var rows = sims.OrderByDescending(s => s.Current).ThenBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => $"{s.Name}{(s.Current ? "*" : "")} rx {Age(s.RawAgeS)} / processed {Age(s.ProcessedAgeS)} ago ({s.RawCount} pkts)").ToList();
        return rows.Count == 0 ? "no regions seen" : string.Join("; ", rows);
    }

    /// <summary>Pure (tested): the Ticker note for a slow step, or null when it took 5 s or less.</summary>
    internal static string TickerSlowStepNote(string step, double seconds, bool finished)
    {
        if (step == null || seconds <= TickerStepSlowS) return null;
        return finished
            ? $"slow step {step}: {seconds.ToString("F1", CultureInfo.InvariantCulture)} s (the 1 s ticker waited for it)"
            : $"step {step} still running after {seconds.ToString("F0", CultureInfo.InvariantCulture)} s (the 1 s ticker is blocked)";
    }

    // called from LoginCoreAsync for every new GridClient (next to HookWatchdog)
    static void HookPacketAges()
    {
        simProcessedTicks.Clear(); simRawRecv.Clear(); simNames.Clear(); Interlocked.Exchange(ref currentSimHandle, 0);
        client.Network.RegisterCallback(PacketType.Default, (s, e) =>
        {
            var sim = e.Simulator;
            if (sim != null) simProcessedTicks[sim.Handle] = Environment.TickCount64;
        }, false);   // synchronous: a dictionary write on the packet thread, no thread-pool hop
    }

    // watchdog thread, every 2 s: notice raw socket receives per region (LibreMetaverse counts them before processing)
    static void SamplePacketAges(long now)
    {
        var c = client; if (c?.Network == null) return;
        try { var cur = c.Network.CurrentSim; if (cur != null) Interlocked.Exchange(ref currentSimHandle, (long)cur.Handle); } catch { }
        foreach (var sim in SimsSnapshot())
        {
            if (sim == null) continue;
            long count; try { count = sim.Stats.GetRecvPackets(); } catch { continue; }
            if (!string.IsNullOrEmpty(sim.Name)) simNames[sim.Handle] = sim.Name;
            simRawRecv.AddOrUpdate(sim.Handle, (count, now), (_, old) => count != old.Count ? (count, now) : old);
        }
    }

    // works after the disconnect too (LibreMetaverse has already emptied Simulators by then): built from our own stamps
    static string PacketAgesSummary()
    {
        var now = Environment.TickCount64; var cur = (ulong)Interlocked.Read(ref currentSimHandle);
        var handles = simRawRecv.Keys.Union(simProcessedTicks.Keys).Distinct();
        var rows = handles.Select(h =>
        {
            double? raw = simRawRecv.TryGetValue(h, out var r) ? (now - r.Ticks) / 1000.0 : null;
            double? done = simProcessedTicks.TryGetValue(h, out var p) ? (now - p) / 1000.0 : null;
            return new SimPacketAge(simNames.TryGetValue(h, out var n) ? n : $"region {h}", h == cur, raw, done, r.Count);
        });
        return FormatPacketAges(rows);
    }

    static void MaybeLogPacketStats(long now)
    {
        if (!LoggedIn || now - Interlocked.Read(ref lastPacketStatsTicks) < PacketStatsEverySec * 1000L) return;
        Interlocked.Exchange(ref lastPacketStatsTicks, now);
        Log("net-stats", "packets: " + PacketAgesSummary());
    }

    // ---- Ticker step timing ---------------------------------------------------------------------------------------
    static void TickerStepBegin(string name)
    {
        Interlocked.Exchange(ref tickerStepStartTicks, Environment.TickCount64);
        Interlocked.Exchange(ref tickerStepReported, 0);
        tickerStep = name;
    }
    static void TickerStepEnd()
    {
        var name = tickerStep; tickerStep = null;
        var note = TickerSlowStepNote(name, (Environment.TickCount64 - Interlocked.Read(ref tickerStepStartTicks)) / 1000.0, true);
        if (note != null) Log("ticker", note);
    }
    // watchdog thread: a step that is still running (maybe never returns)
    static void CheckTickerStall(long now)
    {
        var name = tickerStep; if (name == null) return;
        var note = TickerSlowStepNote(name, (now - Interlocked.Read(ref tickerStepStartTicks)) / 1000.0, false);
        if (note != null && Interlocked.Exchange(ref tickerStepReported, 1) == 0) Log("ticker", note);
    }
    static string TickerStatus()
    {
        var name = tickerStep;
        return name == null ? "ticker idle" : $"ticker in {name} for {(Environment.TickCount64 - Interlocked.Read(ref tickerStepStartTicks)) / 1000.0:F0} s";
    }

    // ---- run/last-online.txt on its own timer (was a Ticker step) --------------------------------------------------
    static void StartLastOnlineTimer()
    {
        lock (lastOnlineTimerLock)
        {
            if (lastOnlineTimer != null) return;
            lastOnlineTimer = new Timer(_ => { try { LastOnlineHeartbeat(); } catch { } }, null, 1000, 5000);
        }
    }
}
