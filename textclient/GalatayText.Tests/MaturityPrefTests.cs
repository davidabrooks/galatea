using System.Text;
using GalatayText;
using LibreMetaverse.StructuredData;
using Xunit;

public class MaturityPrefTests
{
    static byte[] Reply(string max) => OSDParser.SerializeLLSDXmlBytes(new OSDMap { ["access_prefs"] = new OSDMap { ["max"] = max } });
    static Func<string, Task<(int, string, byte[])>> Post(int status, string reason, byte[] body, List<string> sent = null) =>
        c => { sent?.Add(c); return Task.FromResult((status, reason, body)); };
    static Func<Task<string>> Back(string v) => () => Task.FromResult(v);

    [Theory]
    [InlineData("general", "PG")] [InlineData("PG", "PG")] [InlineData("Moderate", "M")] [InlineData("mature", "M")] [InlineData("ADULT", "A")] [InlineData("a", "A")]
    public void LevelWords(string w, string code) => Assert.Equal(code, Program.MaturityCode(w));

    [Theory] [InlineData("")] [InlineData("teen")] [InlineData(null)] [InlineData("adults")]
    public void BadLevelWords(string w) => Assert.Null(Program.MaturityCode(w));

    [Fact] public void Names() { Assert.Equal("Adult (A)", Program.MaturityName("A")); Assert.Equal("not reported", Program.MaturityName("")); Assert.Equal("unknown 'T'", Program.MaturityName("T")); }

    [Fact] public void PrefsMaxParsed()
    {
        Assert.Equal("M", Program.MaturityPrefsMax(new OSDMap { ["access_prefs"] = new OSDMap { ["max"] = "M" } }));
        Assert.Null(Program.MaturityPrefsMax(new OSDMap { ["hover_height"] = 0.0 }));
        Assert.Null(Program.MaturityPrefsMax(null));
    }

    [Fact] public async Task AppliedAndVerifiedByReadBack()
    {
        var sent = new List<string>();
        var r = await Program.MaturitySetCore("A", "A", Post(200, "OK", Reply("A"), sent), Back("A"));
        Assert.StartsWith("VERIFIED", r); Assert.Equal(new[] { "A" }, sent); Assert.Contains("access_prefs.max=A", r);
    }

    [Fact] public async Task AppliedButGridDoesNotReportReadBack()
    {
        var r = await Program.MaturitySetCore("M", "A", Post(200, "OK", Reply("M")), Back(null));
        Assert.StartsWith("UNVERIFIED", r); Assert.Contains("only the UpdateAgentInformation reply", r);
    }

    [Fact] public async Task GridClampsToCeiling_NotApplied_NoRetry()
    {
        var sent = new List<string>();
        var r = await Program.MaturitySetCore("A", "M", Post(200, "OK", Reply("M"), sent), Back("M"));
        Assert.StartsWith("NOT APPLIED", r); Assert.Contains("answered access_prefs.max=M", r); Assert.Contains("age verification", r); Assert.Single(sent);
    }

    [Fact] public async Task ReadBackDisagrees()
    {
        var r = await Program.MaturitySetCore("A", "A", Post(200, "OK", Reply("A")), Back("M"));
        Assert.StartsWith("NOT APPLIED", r); Assert.Contains("read-back says M", r);
    }

    [Fact] public async Task HttpErrorReportedExactly_NoRetry()
    {
        var sent = new List<string>();
        var r = await Program.MaturitySetCore("A", "M", Post(403, "Forbidden", Encoding.UTF8.GetBytes("age verification required"), sent), Back("M"));
        Assert.StartsWith("REFUSED by the grid: HTTP 403 Forbidden", r); Assert.Contains("age verification required", r); Assert.Contains("not retried", r); Assert.Single(sent);
    }

    [Fact] public async Task ReplyWithoutAccessPrefs_IsUnverified()
    {
        var r = await Program.MaturitySetCore("A", "A", Post(200, "OK", OSDParser.SerializeLLSDXmlBytes(new OSDMap { ["ok"] = true })), Back("A"));
        Assert.StartsWith("UNVERIFIED", r);
    }

    [Fact] public async Task TransportFailureReported()
    {
        var r = await Program.MaturitySetCore("A", "A", _ => throw new HttpRequestException("boom"), Back("A"));
        Assert.StartsWith("FAILED", r); Assert.Contains("boom", r); Assert.Contains("not retried", r);
    }

    [Fact] public async Task ReadBackFailureLeavesUnverified()
    {
        var r = await Program.MaturitySetCore("PG", "A", Post(200, "OK", Reply("PG")), () => throw new TimeoutException());
        Assert.StartsWith("UNVERIFIED", r); Assert.Contains("read-back failed", r);
    }

    [Fact] public void GateRefusesAutomatedPath()
    {
        var g = Program.MaturitySetGate("adult", hasOk: true, explicitCmd: false, out _);
        Assert.StartsWith("refused: maturity set only runs as an explicit operator command", g);
    }
    [Fact] public void GateNeedsDavidsOk()
    {
        var g = Program.MaturitySetGate("adult", hasOk: false, explicitCmd: true, out var code);
        Assert.StartsWith("not changed:", g); Assert.Contains("David's OK", g); Assert.Equal("A", code);
    }
    [Fact] public void GateBadLevel() => Assert.StartsWith("usage:", Program.MaturitySetGate("teen", true, true, out _));
    [Fact] public void GateOpensOnlyWhenExplicitAndOk() { Assert.Equal("", Program.MaturitySetGate("moderate", true, true, out var c)); Assert.Equal("M", c); }

    [Fact] public void ExplicitFlagDefaultsOffAndIsPerAsyncFlow()
    {
        Assert.False(Program.ExplicitCmd.Value);
        var t = Task.Run(async () => { Program.ExplicitCmd.Value = true; return await Task.Run(() => Program.ExplicitCmd.Value); });
        Assert.True(t.Result);
        Assert.False(Program.ExplicitCmd.Value); // the socket handler's flag never leaks into the caller or other commands
    }

    [Fact] public void ClientNeverSetsMaturityOutsideTheCommand()
    {
        // guard against someone wiring SetAgentAccess into login / handlers: only MaturityPref.cs may POST access_prefs
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../galatay-text"));
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "*.cs"))
            if (Path.GetFileName(f) != "MaturityPref.cs") Assert.DoesNotContain("SetAgentAccess", File.ReadAllText(f));
    }
}
