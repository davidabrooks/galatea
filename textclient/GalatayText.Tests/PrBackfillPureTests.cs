using GalatayMcp;
using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>Focused pure-logic backfill for earlier PRs (no SL login).</summary>
public class PrBackfillPureTests
{
    // ---- PR #38 / webhook retry + debounce ----
    [Fact]
    public void Pr38_Webhook_RetriableStatus_covers_400_429_5xx()
    {
        Assert.True(Webhook.RetriableStatus(null));
        Assert.True(Webhook.RetriableStatus(400));
        Assert.True(Webhook.RetriableStatus(408));
        Assert.True(Webhook.RetriableStatus(429));
        Assert.True(Webhook.RetriableStatus(500));
        Assert.True(Webhook.RetriableStatus(503));
        Assert.False(Webhook.RetriableStatus(200));
        Assert.False(Webhook.RetriableStatus(404));
    }

    [Fact]
    public void Pr38_Webhook_Due_single_vs_burst()
    {
        var t0 = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        var detect = TimeSpan.FromSeconds(4);
        var quiet = TimeSpan.FromSeconds(20);
        var max = TimeSpan.FromSeconds(60);
        Assert.False(Webhook.Due(false, t0, t0, t0.AddSeconds(2), detect, quiet, max));
        Assert.True(Webhook.Due(false, t0, t0, t0.AddSeconds(5), detect, quiet, max));
        Assert.False(Webhook.Due(true, t0, t0.AddSeconds(1), t0.AddSeconds(10), detect, quiet, max));
        Assert.True(Webhook.Due(true, t0, t0.AddSeconds(1), t0.AddSeconds(25), detect, quiet, max));
        Assert.True(Webhook.Due(true, t0, t0.AddSeconds(50), t0.AddSeconds(61), detect, quiet, max)); // maxHold
    }

    [Fact]
    public void Pr38_Webhook_CapExempt_David_and_urgent()
    {
        var david = "44ce5a36-c1c7-4a68-ac9a-635ddfff6233";
        var other = "11111111-1111-1111-1111-111111111111";
        Assert.True(Webhook.CapExempt(new Webhook.Ev("group_invite", "x", other, "t", "2026-10-05T12:00:00-07:00", null)));
        Assert.True(Webhook.CapExempt(new Webhook.Ev("im", "David", david, "hi", "2026-10-05T12:00:00-07:00", null)));
        Assert.False(Webhook.CapExempt(new Webhook.Ev("im", "Other", other, "hi", "2026-10-05T12:00:00-07:00", null)));
    }

    // ---- PR #45 / #46 experiences + TEMP false positive ----
    [Fact]
    public void Pr45_DecideScriptQuestion_never_grants_Debit()
    {
        var exp = UUID.Parse("22271376-62b5-11f0-b0e9-0242ac110005");
        var (g, why, perms) = Program.DecideScriptQuestion(
            exp, ScriptPermission.Debit | ScriptPermission.Attach, true,
            prefAllowed: true, prefBlocked: false, regionOrParcelAllowed: false, nameOrIdAllowlisted: false);
        Assert.True(g);
        Assert.Equal(ScriptPermission.Attach, perms);
        Assert.Equal(0, (int)(perms & ScriptPermission.Debit));
        Assert.Contains("Allowed", why);
    }

    [Fact]
    public void Pr45_DecideScriptQuestion_Debit_only_refused()
    {
        var exp = UUID.Random();
        var (g, why, _) = Program.DecideScriptQuestion(
            exp, ScriptPermission.Debit, true,
            true, false, false, false);
        Assert.False(g);
        Assert.Contains("Debit", why);
    }

    // ---- seated furniture props: grant any experience not blocked (never Debit) ----
    [Fact]
    public void Seated_unknown_experience_grants_safe_perms_without_Debit()
    {
        var exp = UUID.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var ask = ScriptPermission.Attach | ScriptPermission.TriggerAnimation | ScriptPermission.Debit;
        var (g, why, perms) = Program.DecideScriptQuestion(
            exp, ask, seated: true,
            prefAllowed: false, prefBlocked: false, regionOrParcelAllowed: false, nameOrIdAllowlisted: false);
        Assert.True(g);
        Assert.Equal(ScriptPermission.Attach | ScriptPermission.TriggerAnimation, perms);
        Assert.Equal(0, (int)(perms & ScriptPermission.Debit));
        Assert.Contains("seated", why);
    }

    [Fact]
    public void Standing_unknown_experience_still_denied()
    {
        var exp = UUID.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        var (g, why, _) = Program.DecideScriptQuestion(
            exp, ScriptPermission.Attach, seated: false,
            prefAllowed: false, prefBlocked: false, regionOrParcelAllowed: false, nameOrIdAllowlisted: false);
        Assert.False(g);
        Assert.Contains("not allowed", why);
    }

    [Fact]
    public void Seated_blocked_experience_still_denied()
    {
        var exp = UUID.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
        var (g, why, _) = Program.DecideScriptQuestion(
            exp, ScriptPermission.Attach, seated: true,
            prefAllowed: false, prefBlocked: true, regionOrParcelAllowed: true, nameOrIdAllowlisted: true);
        Assert.False(g);
        Assert.Contains("blocked", why);
    }

    [Fact]
    public void Pr46_IsLikelyTempAttach_LaraX_false_positive_cases()
    {
        var laraxObj = UUID.Parse("aaaaaaaa-aaaa-4aaa-aaaa-aaaaaaaaaaaa");
        var laraxItem = UUID.Parse("bbbbbbbb-bbbb-4bbb-bbbb-bbbbbbbbbbbb");
        // login race / outfit item — must NOT mark TEMP
        Assert.False(Program.IsLikelyTempAttach(laraxObj, laraxItem, false, false, false, false, false));
        Assert.False(Program.IsLikelyTempAttach(laraxObj, laraxItem, true, false, false, false, false));
        Assert.False(Program.IsLikelyTempAttach(laraxObj, laraxItem, true, true, true, false, false));
        Assert.False(Program.IsLikelyTempAttach(laraxObj, laraxItem, true, true, false, true, false));
        // true TEMP: grant + inv ready + absent
        Assert.True(Program.IsLikelyTempAttach(laraxObj, laraxItem, true, true, false, false, false));
        // item id == object id (llAttachToAvatarTemp style)
        var mug = UUID.Parse("cccccccc-cccc-4ccc-cccc-cccccccccccc");
        Assert.True(Program.IsLikelyTempAttach(mug, mug, false, false, false, false, false));
    }

    // ---- PR #51–#54 / #53 follow-up pose helpers (already public) ----
    [Fact]
    public void Pr53_PoseMenuLooksLikeRoot_SoloSubmenu_is_not_root()
    {
        var root = new List<string> { "[ SWAP ]*", "Clean*", "[ADJUST]", "Solo*", "Couples PG*", "Adult M+F*", "[BACK]" };
        var soloSub = new List<string> { "Solo Adult*", "[ADJUST]", "[BACK]", "Solo Couch*", "Solo Pouf*" };
        Assert.True(Program.PoseMenuLooksLikeRoot(root));
        Assert.False(Program.PoseMenuLooksLikeRoot(soloSub));
    }

    [Fact]
    public void Pr55_PosePathStartIndex_leaf_on_open_submenu()
    {
        var couch = new List<string> { "[ADJUST]", "Hang out", "Cross legs", "[BACK]", "Contemplate" };
        var want = new[] { "Solo*", "Solo Couch*", "Cross legs" };
        Assert.Equal(2, Program.PosePathStartIndex(couch, want));
        Assert.Equal(0, Program.PosePathStartIndex(rootLabels(), want));
    }

    static List<string> rootLabels() => new() { "Solo*", "Couples PG*", "Clean*" };

    [Fact]
    public void Pr52_DavidChoosesPoses_only_when_David_on_seat()
    {
        Assert.True(Program.DavidChoosesPoses(true, false));
        Assert.False(Program.DavidChoosesPoses(false, false)); // Sophie alone
        Assert.False(Program.DavidChoosesPoses(true, true)); // explicit couples
    }

    [Fact]
    public void Pr55_PoseLeafIsCouples_Magnetize_from_path_not_name()
    {
        // Magnetize alone is not couples-named; path Couples PG* > Cuddles* makes it couples
        Assert.True(Program.PosePathIsCouples(new[] { "Couples PG*", "Cuddles*", "Magnetize" }));
        Assert.False(Program.PosePathIsCouples(new[] { "Solo*", "Solo Couch*", "Cross legs" }));
        Assert.True(Program.PoseLeafIsCouples("Magnetize", new[] { "Couples PG*", "Cuddles*", "Magnetize" }));
        Assert.False(Program.PoseLeafIsCouples("Cross legs", new[] { "Solo*", "Solo Couch*", "Cross legs" }));
        // Together is couples-named by regex even without path
        Assert.True(Program.PoseLeafIsCouples("Together", null));
    }

    // ---- PR #48 nav grid present ----
    [Fact]
    public void Pr48_Peronaut_nav_grid_file_exists_and_has_doors()
    {
        var root = FindRepoRoot();
        var grid = Path.Combine(root, "textclient", "routes", "_nav-peronaut-home.json");
        Assert.True(File.Exists(grid), grid);
        var json = File.ReadAllText(grid);
        Assert.Contains("doors", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("living", json, StringComparison.OrdinalIgnoreCase);
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TESTING.md"))
                && Directory.Exists(Path.Combine(dir.FullName, "textclient")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")
               ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}
