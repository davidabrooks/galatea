using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GalatayText;
using LibreMetaverse.StructuredData;
using Xunit;

namespace GalatayText.Tests;

/// <summary>
/// 2026-11-23: `look ... env` is the Photographer opt-in for a full shared ExtEnvironment block.
/// Quick looks, including plain far, stay noenv and must do zero environment work.
/// </summary>
public class LookEnvTests
{
    [Theory]
    [InlineData(new string[] { "around" }, false, false, false, "around")]
    [InlineData(new string[] { "around", "far" }, false, true, false, "around")]
    [InlineData(new string[] { "around", "fast", "far" }, true, true, false, "around")]
    [InlineData(new string[] { "around", "far", "env" }, false, true, true, "around")]
    [InlineData(new string[] { "self", "front", "env" }, false, false, true, "self,front")]
    [InlineData(new string[] { "at", "Chair" }, false, false, false, "at,Chair")]
    public void LookFlags_StripsKnownOptionWords(string[] a, bool fast, bool far, bool env, string rest)
    {
        var g = Program.LookFlags(a);
        Assert.Equal(fast, g.fast); Assert.Equal(far, g.far); Assert.Equal(env, g.env);
        Assert.Equal(rest, string.Join(",", g.rest));
    }

    [Fact]
    public void LookSceneExportArgs_PlainLooksIncludingFar_AreNoenv()
    {
        Assert.False(Program.LookWantsSharedEnv(false, false));
        Assert.True(Program.LookWantsSharedEnv(false, true));
        Assert.True(Program.LookWantsSharedEnv(true, false));
        Assert.Equal(new[] { "export", "96", "noenv" }, Program.LookSceneExportArgs(96f, Program.LookWantsSharedEnv(false, false)));
        Assert.Equal(new[] { "export", "32", "noenv" }, Program.LookSceneExportArgs(32f, false));
        Assert.Equal(new[] { "export", "8", "noenv" }, Program.LookSceneExportArgs(8f, false));
        Assert.Equal(new[] { "export", "96" }, Program.LookSceneExportArgs(96f, Program.LookWantsSharedEnv(false, true)));
        Assert.Equal(new[] { "export", "128" }, Program.LookSceneExportArgs(128f, Program.LookWantsSharedEnv(true, false)));
        Assert.DoesNotContain("noenv", Program.LookSceneExportArgs(96f, true));
    }

    [Fact]
    public void McpLookLine_AndClientCmd_PassTheEnvFlagThrough()
    {
        Assert.Equal("look around far", Program.McpLookLine("around", "", false, true));
        Assert.Equal("look around far env", Program.McpLookLine("around", "", false, true, null, true));
        Assert.Equal("look around fast far env", Program.McpLookLine("around", "", true, true, null, true));
        var (ok, line, err) = Program.ClientCmdGate("look around far env");
        Assert.True(ok, err); Assert.Equal("look around far env", line);
        Assert.True(Program.ClientCmdGate("look around far").ok);
        Assert.True(Program.ClientCmdGate("LOOK around FAR env").ok);
    }

    static OSDMap SampleReply(int parcelId = 42, bool altitude = true)
    {
        var frames = new OSDMap
        {
            ["Sunrise"] = new OSDMap { ["type"] = "sky", ["sun_rotation"] = 0.25 },
            ["Water1"] = new OSDMap { ["type"] = "water", ["fog_color"] = "blue" },
            ["HighAlt"] = new OSDMap { ["type"] = "sky", ["cloud_density"] = 0.4 },
        };
        var tracks = new OSDArray
        {
            new OSDArray { new OSDMap { ["key_keyframe"] = 0.0, ["key_name"] = "Water1" } },
            new OSDArray { new OSDMap { ["key_keyframe"] = 0.25, ["key_name"] = "Sunrise" } },
        };
        if (altitude)
            tracks.Add(new OSDArray { new OSDMap { ["key_keyframe"] = 0.25, ["key_name"] = "HighAlt" } });
        return new OSDMap
        {
            ["parcel_id"] = parcelId,
            ["version"] = 3,
            ["environment"] = new OSDMap
            {
                ["day_length"] = 14400,
                ["day_offset"] = 57600,
                ["flags"] = 0,
                ["day_cycle"] = new OSDMap { ["frames"] = frames, ["tracks"] = tracks },
            },
        };
    }

    static Task<OSDMap> FailParcel(int _) => throw new InvalidOperationException("parcel fetcher invoked");
    static Task<OSDMap> FailRegion() => throw new InvalidOperationException("region fetcher invoked");

    [Fact]
    public async Task Noenv_NeverCallsTheFetcher_OrReadsTheCache()
    {
        var cached = (7UL, 3, DateTime.UtcNow, (OSD)new OSDMap { ["source"] = "parcel" });
        var (env, cache) = await Program.ResolveExportEnvironment(true, 7, 3, "Peacock Park", DateTime.UtcNow, cached, FailParcel, FailRegion);
        Assert.Equal("skipped: noenv", env.AsString());
        Assert.Null(cache);
        Assert.False(env is OSDMap);
    }

    [Fact]
    public async Task EnvFlag_KeepsEveryDayCycleFrameAndTrack_PlusSourceAndTimestamp()
    {
        var now = new DateTime(2026, 11, 24, 2, 10, 0, DateTimeKind.Utc);
        int parcelCalls = 0, regionCalls = 0;
        var (env, cache) = await Program.ResolveExportEnvironment(false, 11, 42, "Peacock Park", now, default, async id => { parcelCalls++; Assert.Equal(42, id); return SampleReply(42); }, () => { regionCalls++; return FailRegion(); });
        Assert.Equal(1, parcelCalls); Assert.Equal(0, regionCalls);
        var m = Assert.IsType<OSDMap>(env);
        Assert.Equal("parcel", m["source"].AsString());
        Assert.Equal("Peacock Park", m["region"].AsString());
        Assert.Equal(42, m["parcel_id"].AsInteger());
        Assert.Equal(now.ToString("o"), m["exported_at_utc"].AsString());
        Assert.True(m.ContainsKey("region_time_s"));
        var em = Assert.IsType<OSDMap>(m["environment"]);
        Assert.Equal(14400, em["day_length"].AsInteger());
        Assert.Equal(57600, em["day_offset"].AsInteger());
        var dc = Assert.IsType<OSDMap>(em["day_cycle"]);
        var frames = Assert.IsType<OSDMap>(dc["frames"]);
        Assert.True(frames.ContainsKey("Sunrise"));
        Assert.True(frames.ContainsKey("Water1"));
        Assert.True(frames.ContainsKey("HighAlt"));
        var tracks = Assert.IsType<OSDArray>(dc["tracks"]);
        Assert.Equal(3, tracks.Count); // water, ground sky, altitude sky
        Assert.NotNull(cache);
        Assert.Same(env, cache.Value.env);
        // region time: (unix + offset) mod length
        long unix = new DateTimeOffset(now).ToUnixTimeSeconds();
        Assert.Equal((int)(((unix + 57600) % 14400 + 14400) % 14400), m["region_time_s"].AsInteger());
    }

    [Fact]
    public async Task ParcelWithoutEnvironment_FallsBackToTheRegion()
    {
        int regionCalls = 0;
        var empty = new OSDMap { ["parcel_id"] = -1 };
        var (env, _) = await Program.ResolveExportEnvironment(false, 1, 9, "Ahern", DateTime.UtcNow, default,
            async _ => empty,
            () => { regionCalls++; return Task.FromResult(SampleReply(-1)); });
        Assert.Equal(1, regionCalls);
        var m = Assert.IsType<OSDMap>(env);
        Assert.Equal("region", m["source"].AsString());
        Assert.Equal("Ahern", m["region"].AsString());
        Assert.IsType<OSDMap>(m["environment"]);
    }

    [Fact]
    public async Task ParcelReplyAlreadyRegionLevel_DoesNotFetchAgain()
    {
        int regionCalls = 0;
        var reply = SampleReply(-1);
        var (env, _) = await Program.ResolveExportEnvironment(false, 1, 9, "Ahern", DateTime.UtcNow, default,
            async _ => reply,
            () => { regionCalls++; return FailRegion(); });
        Assert.Equal(0, regionCalls);
        Assert.Equal("region", Assert.IsType<OSDMap>(env)["source"].AsString());
    }

    [Fact]
    public async Task FetchFailure_WritesAnErrorString_AndDoesNotThrow()
    {
        var (env, cache) = await Program.ResolveExportEnvironment(false, 1, 4, "X", DateTime.UtcNow, default,
            async _ => throw new InvalidOperationException("sim unavailable"),
            FailRegion);
        Assert.Equal("error: sim unavailable", env.AsString());
        Assert.Null(cache);
    }

    [Fact]
    public async Task CapUrlNeverAppearsInTheExportOrError()
    {
        const string cap = "https://simhost.example:12043/cap/ExtEnvironment-s3cretToken";
        var (err, _) = await Program.ResolveExportEnvironment(false, 1, 4, "X", DateTime.UtcNow, default,
            async _ => throw new InvalidOperationException("GET " + cap + " failed"),
            FailRegion);
        var text = err.AsString();
        Assert.StartsWith("error:", text);
        Assert.DoesNotContain("s3cretToken", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExtEnvironment-s3cret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[redacted]", text);

        var planted = SampleReply(7);
        planted["cap"] = cap;
        planted["environment_url"] = cap;
        var wrapped = Program.WrapExportEnvironment(planted, "parcel", 7, "Peacock Park", DateTime.UtcNow);
        var json = OSDParser.SerializeJsonString(wrapped);
        Assert.DoesNotContain("s3cretToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(cap, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://simhost.example", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("parcel", wrapped["source"].AsString());
        Assert.True(((OSDMap)wrapped["environment"])["day_cycle"] is OSDMap);
    }

    [Fact]
    public async Task CacheHit_SkipsTheFetcher()
    {
        var now = DateTime.UtcNow;
        var cachedEnv = Program.WrapExportEnvironment(SampleReply(5), "parcel", 5, "Cached", now);
        var cache = (9UL, 5, now, (OSD)cachedEnv);
        var (env, write) = await Program.ResolveExportEnvironment(false, 9, 5, "Cached", now.AddMinutes(1), cache, FailParcel, FailRegion);
        Assert.Same(cachedEnv, env);
        Assert.Null(write);
    }

    static string Root() { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
    static string Src(params string[] p) => File.ReadAllText(Path.Combine(new[] { Root() }.Concat(p).ToArray()));

    [Fact]
    public void QuickPath_IsGated_AndTheEnvFlagIsDocumented()
    {
        var look = Src("textclient", "galatay-text", "Look.cs");
        Assert.Contains("LookSceneExportArgs(er, LookWantsSharedEnv(hd != null, envFlag))", look);
        Assert.DoesNotContain("if (hd == null) exArgs.Add(\"noenv\")", look);
        var ex = Src("textclient", "galatay-text", "SceneExport.cs");
        var noEnv = ex.IndexOf("if (noEnv)", ex.IndexOf("static async Task<string> SceneExport"));
        var elseAt = ex.IndexOf("else", noEnv);
        var noEnvBlock = ex[noEnv..elseAt];
        Assert.Contains("skipped: noenv", noEnvBlock);
        Assert.DoesNotContain("ParcelIdAt", noEnvBlock);
        Assert.DoesNotContain("ResolveExportEnvironment", noEnvBlock);
        Assert.DoesNotContain("GetParcelEnvironmentAsync", noEnvBlock);
        Assert.Contains("ParcelIdAt", ex[elseAt..]);
        var mcp = Src("textclient", "galatay-mcp", "McpProgram.cs");
        Assert.Contains("JsonElement? env = null", mcp);
        Assert.Contains("Core.McpFlag(env, false)", mcp);
        var help = Src("textclient", "galatay-text", "Program.cs");
        Assert.Contains("[fast] [far] [env]", help);
        Assert.Contains("[fast] [far] [env]", look);
    }
}
