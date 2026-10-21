// MaturityPref.cs (2026-10-16, David: Astra, an Adult region, refused her teleport because the account prefers Moderate)
// maturity                                 READ-ONLY: account ceiling + preferred maturity as the grid reports them, and the current region's rating
// maturity set <general|moderate|adult> --david-ok   change the account's preferred max maturity (PG / M / A) through the UpdateAgentInformation
//                                          capability (POST {"access_prefs":{"max":..}}, the same call the viewer's Preferences > General > Rating makes), once,
//                                          then read it back. Only from the operator command socket (text-galatay.sh), never from login, chat, IM, webhook,
//                                          wander or the MCP connector, and only with --david-ok (David's OK). A grid refusal (e.g. no age verification) is
//                                          reported with its exact HTTP status/body and is NOT retried.
using System.Text;
using LibreMetaverse;
using LibreMetaverse.StructuredData;

namespace GalatayText;

public static partial class Program
{
    // true only inside a command that arrived on the operator socket; Exec calls made by the client itself (wander, home, boats ...) never set it
    internal static readonly AsyncLocal<bool> ExplicitCmd = new();

    internal static string? MaturityCode(string? word) => (word ?? "").Trim().ToLowerInvariant() switch
    {
        "general" or "pg" or "g" => "PG",
        "moderate" or "mature" or "m" => "M",
        "adult" or "a" => "A",
        _ => null
    };

    internal static int MaturityRank(string? code) => (code ?? "").Trim().ToUpperInvariant() switch { "PG" => 1, "M" => 2, "A" => 3, _ => 0 };

    internal static string MaturityName(string? code) => MaturityRank(code) switch
    {
        1 => "General (PG)", 2 => "Moderate (M)", 3 => "Adult (A)",
        _ => string.IsNullOrWhiteSpace(code) ? "not reported" : $"unknown '{code}'"
    };

    // access_prefs.max from an AgentPreferences GET reply / UpdateAgentInformation reply, or null when absent
    internal static string? MaturityPrefsMax(OSD? osd) =>
        osd is OSDMap m && m.TryGetValue("access_prefs", out var ap) && ap is OSDMap apm && apm.TryGetValue("max", out var mx) && mx.Type == OSDType.String
            ? mx.AsString().Trim() : null;

    static string Snip(string s) { s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim(); return s.Length > 300 ? s[..300] + "…" : s; }

    // The set + read-back flow, with the two HTTP calls injected so tests can mock the grid.
    //   post(code)  -> (HTTP status, reason, body) of ONE UpdateAgentInformation POST (throws on transport failure)
    //   readBack()  -> access_prefs.max from an AgentPreferences GET, or null when the grid does not report it
    // Never retries. Verdict is the first word: VERIFIED / UNVERIFIED / NOT APPLIED / REFUSED / FAILED.
    internal static async Task<string> MaturitySetCore(string wantedCode, string? ceilingCode, Func<string, Task<(int status, string reason, byte[] body)>> post, Func<Task<string?>> readBack)
    {
        var want = MaturityName(wantedCode);
        (int status, string reason, byte[] body) r;
        try { r = await post(wantedCode); }
        catch (Exception ex) { return $"FAILED: could not reach the UpdateAgentInformation capability ({ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}). Nothing changed, not retried."; }
        var text = Encoding.UTF8.GetString(r.body ?? Array.Empty<byte>());
        if (r.status < 200 || r.status > 299)
            return $"REFUSED by the grid: HTTP {r.status} {r.reason}; reply: {(text.Length == 0 ? "(empty)" : Snip(text))}. Preference NOT changed to {want}; not retried." + CeilingHint(wantedCode, ceilingCode);
        string? replied = null;
        try { replied = MaturityPrefsMax(OSDParser.Deserialize(r.body ?? Array.Empty<byte>())); } catch { }
        if (replied == null)
            return $"UNVERIFIED: HTTP {r.status} but the reply has no access_prefs.max (reply: {(text.Length == 0 ? "(empty)" : Snip(text))}). The grid did not confirm {want}; not retried." + CeilingHint(wantedCode, ceilingCode);
        if (!string.Equals(replied, wantedCode, StringComparison.OrdinalIgnoreCase))
            return $"NOT APPLIED: asked for {want}, the grid answered access_prefs.max={replied} ({MaturityName(replied)}). The preference stays {MaturityName(replied)}; not retried." + CeilingHint(wantedCode, ceilingCode);
        string? back = null; string backNote;
        try
        {
            back = await readBack();
            backNote = back == null ? "AgentPreferences does not report access_prefs, so only the UpdateAgentInformation reply confirms it" : $"AgentPreferences read-back access_prefs.max={back}";
        }
        catch (Exception ex) { backNote = $"read-back failed ({ex.GetBaseException().GetType().Name})"; }
        if (back != null && !string.Equals(back, wantedCode, StringComparison.OrdinalIgnoreCase))
            return $"NOT APPLIED: the grid replied access_prefs.max={replied} to the change, but the read-back says {back} ({MaturityName(back)}). Check with `maturity`; not retried.";
        var verdict = back != null ? "VERIFIED" : "UNVERIFIED";
        return $"{verdict}: preferred maturity is now {want} (UpdateAgentInformation replied access_prefs.max={replied}; {backNote}). It applies to teleports and region entry from now on; no relog needed.";
    }

    static string CeilingHint(string wantedCode, string? ceilingCode) =>
        MaturityRank(ceilingCode) > 0 && MaturityRank(wantedCode) > MaturityRank(ceilingCode)
            ? $" At login the account's maximum was {MaturityName(ceilingCode)}, so this account probably lacks the access (Adult needs age verification / payment info on file)."
            : "";

    internal static string MaturitySetGate(string levelWord, bool hasOk, bool explicitCmd, out string? code)
    {
        code = MaturityCode(levelWord);
        if (!explicitCmd) return "refused: maturity set only runs as an explicit operator command, never from an automated path (login, chat, IM, webhook, wander, MCP)";
        if (code == null) return "usage: maturity set <general|moderate|adult> --david-ok";
        if (!hasOk) return $"not changed: `maturity set {levelWord.ToLowerInvariant()}` changes the account's maturity preference and needs David's OK; run it again with --david-ok after he agrees";
        return "";
    }

    static async Task<string?> AgentPrefsAccessMax()
    {
        using var t = new CancellationTokenSource(10000);
        var m = await client.Self.GetAgentPreferencesAsync(t.Token);
        return m == null ? null : MaturityPrefsMax(m.RawData);
    }

    static string lastMaturitySet = "";

    static async Task<string> MaturityCmd(string[] a)
    {
        if (a.Length == 0 || a[0].Equals("show", StringComparison.OrdinalIgnoreCase) || a[0].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            var lr = client.Network.LoginResponseData; string? live = null; string liveErr = "";
            try { live = await AgentPrefsAccessMax(); } catch (Exception ex) { liveErr = $" ({ex.GetBaseException().GetType().Name})"; }
            var sim = client.Network.CurrentSim;
            var sb = new StringBuilder("maturity (as the grid reports it; READ-ONLY)\n");
            sb.AppendLine($"  account ceiling at login (agent_access_max): {MaturityName(lr?.AgentAccessMax)}");
            sb.AppendLine($"  preferred at login (agent_region_access): {MaturityName(lr?.AgentRegionAccess)}");
            sb.AppendLine($"  preferred now (AgentPreferences access_prefs.max): {(live == null ? "not reported" + liveErr : MaturityName(live))}");
            sb.AppendLine($"  current region {sim?.Name ?? "-"}: {sim?.Access}");
            if (lastMaturitySet.Length > 0) sb.AppendLine($"  last maturity set this session: {lastMaturitySet}");
            sb.Append("  change: maturity set <general|moderate|adult> --david-ok (needs David's OK; the grid may refuse without age verification)");
            return sb.ToString();
        }
        if (!a[0].Equals("set", StringComparison.OrdinalIgnoreCase)) return "usage: maturity | maturity set <general|moderate|adult> --david-ok";
        var args = a.Skip(1).ToList(); bool ok = args.RemoveAll(x => x == "--david-ok") > 0;
        var gate = MaturitySetGate(args.Count == 1 ? args[0] : "", ok, ExplicitCmd.Value, out var code);
        if (args.Count != 1 && ExplicitCmd.Value) gate = "usage: maturity set <general|moderate|adult> --david-ok";
        if (gate.Length > 0) { Log("maturity", "set refused/not run: " + gate); return gate; }
        var cap = client.Network.CurrentSim?.Caps?.CapabilityURI("UpdateAgentInformation");
        if (cap == null) return "FAILED: the UpdateAgentInformation capability is not available in this region right now. Nothing changed.";
        Log("maturity", $"SET to {code} requested by the operator (David's OK given)");
        var res = await MaturitySetCore(code!, client.Network.LoginResponseData?.AgentAccessMax, async c =>
        {
            using var t = new CancellationTokenSource(20000);
            var (resp, data) = await client.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, new OSDMap { ["access_prefs"] = new OSDMap { ["max"] = c } }, t.Token);
            return ((int)resp.StatusCode, resp.ReasonPhrase ?? "", data ?? Array.Empty<byte>());
        }, AgentPrefsAccessMax);
        lastMaturitySet = $"{DateTime.Now:HH:mm:ss} -> {res.Split(':')[0]}";
        Log("maturity", res);
        return res;
    }
}
