// SlDns.cs (2026-10-09): since 2026-10-08 20:13 PT the box's system DNS answers Second Life hosts with 198.18.0.1, a proxy
// that only passes port 443, so the seed caps / event queue on simhost-*.agni.secondlife.io:12043 time out (no inventory,
// no AO HUD, no parcel lookup, no InterestList). Connecting to the host's REAL address works. So the caps HttpClient
// (LibreMetaverse GridClient.CapsConnectCallback, libremetaverse-doh-connect.patch) connects through SlConnectAsync:
//   SL host (*.secondlife.io / *.lindenlab.com / *.secondlife.com) on a port other than 443, and the system DNS gives a
//   198.18.0.0/15 address (or fails) -> resolve the A record with DNS-over-HTTPS (dns.google, then cloudflare-dns.com, both
//   on 443), cache it for its TTL (30 s .. 1 h; a stale entry is used if DoH is down) and connect to that IP.
//   Anything else (or DoH failed with no cache) -> normal DNS, exactly as before.
// TLS runs on top inside SocketsHttpHandler with the request's host name, so SNI and the certificate check are unchanged.
// Toggle: routes/_net.json {"doh_for_sl": true, "doh_on_443": false} (read fresh; missing file = on, 443 untouched).
// UDP sim circuits use raw IPs from login / EnableSimulator and are not affected.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace GalatayText;

public static partial class Program
{
    internal sealed record NetCfg(bool DohForSl = true, bool DohOn443 = false);
    internal static readonly string[] SlSuffixes = { ".secondlife.io", ".lindenlab.com", ".secondlife.com" };
    internal static readonly string[] DohProviders = { "https://dns.google/resolve", "https://cloudflare-dns.com/dns-query" };
    static readonly ConcurrentDictionary<string, (IPAddress[] Ips, DateTime Exp)> dohCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly HttpClient dohHttp = new() { Timeout = TimeSpan.FromSeconds(5) };
    // tests: (url) -> json; null = real HTTP
    internal static Func<string, CancellationToken, Task<string>> DohGetOverride;

    internal static NetCfg ParseNetCfg(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var r = d.RootElement;
            bool B(string k, bool def) => r.TryGetProperty(k, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => def } : def;
            return new NetCfg(B("doh_for_sl", true), B("doh_on_443", false));
        }
        catch { return new NetCfg(); }
    }

    static NetCfg NetCfgNow()
    {
        try { var f = Path.Combine(RouteDir, "_net.json"); return File.Exists(f) ? ParseNetCfg(File.ReadAllText(f)) : new NetCfg(); }
        catch { return new NetCfg(); }
    }

    internal static bool IsSlHost(string host)
    {
        if (string.IsNullOrEmpty(host) || IPAddress.TryParse(host, out _)) return false;
        var h = host.TrimEnd('.');
        return SlSuffixes.Any(s => h.EndsWith(s, StringComparison.OrdinalIgnoreCase) || h.Equals(s[1..], StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>198.18.0.0/15 (RFC 2544 benchmark range): the box's DNS answers with it for intercepted hosts.</summary>
    internal static bool IsProxyAddr(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 198 && (b[1] == 18 || b[1] == 19);
    }

    internal static bool ShouldTryDoh(NetCfg c, string host, int port) => c.DohForSl && IsSlHost(host) && (port != 443 || c.DohOn443);

    /// <summary>dns.google /resolve and cloudflare dns-json share this format: real A records + the lowest TTL (30..3600 s).</summary>
    internal static (IPAddress[] Ips, int Ttl) ParseDohJson(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json); var r = d.RootElement;
            if (!r.TryGetProperty("Status", out var st) || st.GetInt32() != 0 || !r.TryGetProperty("Answer", out var ans) || ans.ValueKind != JsonValueKind.Array)
                return (Array.Empty<IPAddress>(), 0);
            var ips = new List<IPAddress>(); int ttl = int.MaxValue;
            foreach (var a in ans.EnumerateArray())
            {
                if (!a.TryGetProperty("type", out var t) || t.GetInt32() != 1) continue;
                if (!a.TryGetProperty("data", out var dd) || !IPAddress.TryParse(dd.GetString(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || IsProxyAddr(ip)) continue;
                ips.Add(ip);
                if (a.TryGetProperty("TTL", out var tt) && tt.TryGetInt32(out var n)) ttl = Math.Min(ttl, n);
            }
            return (ips.ToArray(), ips.Count == 0 ? 0 : Math.Clamp(ttl == int.MaxValue ? 300 : ttl, 30, 3600));
        }
        catch { return (Array.Empty<IPAddress>(), 0); }
    }

    internal static string DohUrl(string provider, string host) => $"{provider}?name={Uri.EscapeDataString(host.TrimEnd('.'))}&type=A";

    /// <summary>Real IPv4 addresses for an SL host via DoH (cached for the TTL); null = use normal DNS.</summary>
    internal static async Task<IPAddress[]> DohResolveAsync(string host, CancellationToken ct)
    {
        if (dohCache.TryGetValue(host, out var c) && c.Exp > DateTime.UtcNow) return c.Ips;
        foreach (var p in DohProviders)
        {
            try
            {
                var url = DohUrl(p, host); string json;
                if (DohGetOverride != null) json = await DohGetOverride(url, ct);
                else
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Accept.ParseAdd("application/dns-json");
                    using var resp = await dohHttp.SendAsync(req, ct);
                    json = await resp.Content.ReadAsStringAsync(ct);
                }
                var (ips, ttl) = ParseDohJson(json);
                if (ips.Length == 0) continue;
                dohCache[host] = (ips, DateTime.UtcNow.AddSeconds(ttl));
                if (c.Ips == null || !c.Ips.SequenceEqual(ips)) Log("net", $"DoH {host} -> {string.Join(",", ips.Select(i => i.ToString()))} (ttl {ttl}s via {new Uri(p).Host})");
                return ips;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { Log("net", $"DoH {new Uri(p).Host} failed for {host}: {ex.GetType().Name} {ex.Message}"); }
        }
        if (c.Ips != null && c.Exp > DateTime.UtcNow.AddHours(-1)) { Log("net", $"DoH down; using stale {host} -> {c.Ips[0]}"); return c.Ips; }
        return null;
    }

    /// <summary>Addresses to connect to for host:port (null = let the socket use normal DNS).</summary>
    internal static async Task<IPAddress[]> SlConnectAddrsAsync(NetCfg cfg, string host, int port, Func<string, CancellationToken, Task<IPAddress[]>> sysDns, CancellationToken ct)
    {
        if (!ShouldTryDoh(cfg, host, port)) return null;
        IPAddress[] sys = null;
        try { sys = await sysDns(host, ct); } catch (Exception) when (!ct.IsCancellationRequested) { }
        if (sys != null && sys.Length > 0 && !sys.Any(IsProxyAddr)) return sys;
        return await DohResolveAsync(host, ct);
    }

    public static async ValueTask<Stream> SlConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var ep = ctx.DnsEndPoint;
        IPAddress[] addrs = null;
        try { addrs = await SlConnectAddrsAsync(NetCfgNow(), ep.Host, ep.Port, (h, t) => Dns.GetHostAddressesAsync(h, AddressFamily.InterNetwork, t), ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { Log("net", $"DoH path error for {ep.Host}: {ex.Message}; normal DNS"); }
        var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (addrs != null && addrs.Length > 0) await s.ConnectAsync(addrs, ep.Port, ct);
            else await s.ConnectAsync(ep, ct);
            return new NetworkStream(s, ownsSocket: true);
        }
        catch { s.Dispose(); throw; }
    }
}
