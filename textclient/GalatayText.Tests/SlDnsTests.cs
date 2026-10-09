using System.Net;
using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>SlDns.cs: SL hosts the box DNS points at the 198.18.0.1 proxy are resolved via DoH and connected by real IP.</summary>
public class SlDnsTests
{
    const string Google = "{\"Status\":0,\"Answer\":[{\"name\":\"h.\",\"type\":5,\"TTL\":60,\"data\":\"x.\"},{\"name\":\"x.\",\"type\":1,\"TTL\":120,\"data\":\"35.162.135.137\"},{\"name\":\"x.\",\"type\":1,\"TTL\":90,\"data\":\"35.162.135.138\"}]}";

    [Theory]
    [InlineData("simhost-04bfe6964c4d32d50.agni.secondlife.io", true)]
    [InlineData("login.agni.lindenlab.com", true)]
    [InlineData("asset-cdn.glb.agni.lindenlab.com", true)]
    [InlineData("SECONDLIFE.COM", true)]
    [InlineData("notsecondlife.io", false)]
    [InlineData("api.x.ai", false)]
    [InlineData("35.162.135.137", false)]
    public void Sl_host_match(string host, bool sl) => Assert.Equal(sl, Program.IsSlHost(host));

    [Theory]
    [InlineData("198.18.0.1", true)]
    [InlineData("198.19.255.1", true)]
    [InlineData("198.20.0.1", false)]
    [InlineData("35.162.135.137", false)]
    [InlineData("::ffff:198.18.0.1", true)]
    public void Proxy_range(string ip, bool proxy) => Assert.Equal(proxy, Program.IsProxyAddr(IPAddress.Parse(ip)));

    [Fact]
    public void Parses_A_records_and_min_ttl()
    {
        var (ips, ttl) = Program.ParseDohJson(Google);
        Assert.Equal(new[] { "35.162.135.137", "35.162.135.138" }, ips.Select(i => i.ToString()));
        Assert.Equal(90, ttl);
    }

    [Theory]
    [InlineData("{\"Status\":3}")]
    [InlineData("{\"Status\":0,\"Answer\":[{\"type\":1,\"TTL\":5,\"data\":\"198.18.0.1\"}]}")]
    [InlineData("not json")]
    public void Bad_or_proxied_answers_give_nothing(string json) => Assert.Empty(Program.ParseDohJson(json).Ips);

    [Fact]
    public void Ttl_is_clamped() => Assert.Equal(30, Program.ParseDohJson("{\"Status\":0,\"Answer\":[{\"type\":1,\"TTL\":1,\"data\":\"1.2.3.4\"}]}").Ttl);

    [Fact]
    public void Config_defaults_and_toggle()
    {
        Assert.Equal(new Program.NetCfg(true, false), Program.ParseNetCfg("{}"));
        Assert.False(Program.ParseNetCfg("{\"doh_for_sl\": false}").DohForSl);
        Assert.True(Program.ParseNetCfg("{\"doh_on_443\": true}").DohOn443);
        Assert.Equal(new Program.NetCfg(), Program.ParseNetCfg("garbage"));
    }

    [Fact]
    public void Doh_only_for_sl_hosts_off_443_when_enabled()
    {
        var on = new Program.NetCfg();
        Assert.True(Program.ShouldTryDoh(on, "simhost-a.agni.secondlife.io", 12043));
        Assert.False(Program.ShouldTryDoh(on, "simhost-a.agni.secondlife.io", 443));
        Assert.True(Program.ShouldTryDoh(on with { DohOn443 = true }, "simhost-a.agni.secondlife.io", 443));
        Assert.False(Program.ShouldTryDoh(on with { DohForSl = false }, "simhost-a.agni.secondlife.io", 12043));
        Assert.False(Program.ShouldTryDoh(on, "example.com", 12043));
    }

    [Fact]
    public async Task Proxied_system_answer_goes_to_doh_real_answer_is_kept()
    {
        var urls = new List<string>();
        Program.DohGetOverride = (u, _) => { urls.Add(u); return Task.FromResult(Google); };
        try
        {
            var cfg = new Program.NetCfg();
            Task<IPAddress[]> Proxy(string h, CancellationToken t) => Task.FromResult(new[] { IPAddress.Parse("198.18.0.1") });
            Task<IPAddress[]> Real(string h, CancellationToken t) => Task.FromResult(new[] { IPAddress.Parse("8.8.4.4") });
            var a = await Program.SlConnectAddrsAsync(cfg, "simhost-test1.agni.secondlife.io", 12043, Proxy, default);
            Assert.Equal("35.162.135.137", a[0].ToString());
            Assert.StartsWith("https://dns.google/resolve?name=simhost-test1.agni.secondlife.io&type=A", urls[0]);
            await Program.SlConnectAddrsAsync(cfg, "simhost-test1.agni.secondlife.io", 12043, Proxy, default);
            Assert.Single(urls);                                     // cached for the TTL
            var r = await Program.SlConnectAddrsAsync(cfg, "simhost-test2.agni.secondlife.io", 12043, Real, default);
            Assert.Equal("8.8.4.4", r[0].ToString());
            Assert.Null(await Program.SlConnectAddrsAsync(cfg, "simhost-test2.agni.secondlife.io", 443, Proxy, default));
        }
        finally { Program.DohGetOverride = null; }
    }

    [Fact]
    public async Task Doh_failure_falls_back_to_normal_dns()
    {
        Program.DohGetOverride = (u, _) => throw new HttpRequestException("down");
        try
        {
            Task<IPAddress[]> Proxy(string h, CancellationToken t) => Task.FromResult(new[] { IPAddress.Parse("198.18.0.1") });
            Assert.Null(await Program.SlConnectAddrsAsync(new Program.NetCfg(), "simhost-test3.agni.secondlife.io", 12043, Proxy, default));
        }
        finally { Program.DohGetOverride = null; }
    }
}
