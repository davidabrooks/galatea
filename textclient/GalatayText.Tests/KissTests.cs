using GalatayText;
using LibreMetaverse;
using Xunit;
using KP = GalatayText.Program.KissPrim;

public class KissTests
{
    // David's kisser as seen 2026-10-11 17:09 (scene export): root 132 on his avatar, 6 children
    static readonly List<KP> Roots = new() { new(132, 900, new Vector3(0.01f, 0.01f, 0.01f)), new(200, 900, new Vector3(0.2f, 0.27f, 0.17f)) };
    static readonly List<KP> Kids = new()
    {
        new(126, 132, new Vector3(0.0676f, 0.0591f, 0.0641f)), new(127, 132, new Vector3(0.0698f, 0.0670f, 0.0836f)),
        new(128, 132, new Vector3(0.0103f, 0.0598f, 0.0341f)), new(129, 132, new Vector3(0.01f, 0.01f, 0.01f)),
        new(130, 132, new Vector3(0.0233f, 0.0351f, 0.0260f)), new(131, 132, new Vector3(0.0233f, 0.0233f, 0.0233f)),
        new(201, 200, new Vector3(0.0233f, 0.0351f, 0.0260f)),
    };

    [Fact] public void FindsLipsPrim() => Assert.Equal(130u, Program.FindKisserPart(Roots, Kids));
    [Fact] public void FindsCheekPrim() => Assert.Equal(126u, Program.FindKisserPart(Roots, Kids, "cheek"));
    [Fact] public void NoKisserGivesZero() => Assert.Equal(0u, Program.FindKisserPart(Roots, Kids.Where(k => k.Local != 131).ToList()));

    [Fact] public void WanderKissRuleDefaultsAndClamp()
    {
        var d = Program.ParseWanderKiss("{}"); Assert.Equal(0.25, d.Chance); Assert.Equal(20, d.CooldownMin); Assert.Equal(5f, d.MaxM);
        var r = Program.ParseWanderKiss("{\"kiss\":{\"chance\":0.5,\"cooldownMin\":5,\"maxM\":9}}");
        Assert.Equal(0.5, r.Chance); Assert.Equal(20, r.CooldownMin); Assert.Equal(5f, r.MaxM);
    }

    [Fact] public void WanderKissDueRules()
    {
        var r = new Program.WanderKissRule(0.25, 20, 5f); var now = DateTime.Now; var old = now.AddHours(-1);
        Assert.True(Program.WanderKissDue(r, 3, 0, false, now, old, 0.1));
        Assert.False(Program.WanderKissDue(r, 3, 0, false, now, old, 0.3));        // roll
        Assert.False(Program.WanderKissDue(r, 6, 0, false, now, old, 0.1));        // too far
        Assert.False(Program.WanderKissDue(r, 3, 0, true, now, old, 0.1));         // seated
        Assert.False(Program.WanderKissDue(r, 3, 3, false, now, old, 0.1));        // other level
        Assert.False(Program.WanderKissDue(r, 3, 0, false, now, now.AddMinutes(-10), 0.1)); // cooldown
    }

    [Fact] public void FastKissFlagClearsAction()
    {
        string Wrap(string c) => "{\"choices\":[{\"message\":{\"content\":" + System.Text.Json.JsonSerializer.Serialize(c) + "}}]}";
        var r = Program.ParseFastResponse(Wrap("{\"reply\":\"mwah\",\"action\":true,\"kiss\":true}"));
        Assert.True(r.Kiss); Assert.False(r.Action);
        var h = Program.ParseFastResponse(Wrap("{\"reply\":\"let me check\",\"action\":true,\"helper\":true,\"kiss\":true}"));
        Assert.False(h.Kiss);
    }

    [Fact] public void KeepAttachedParses()
    {
        var l = Program.ParseKeepAttached("# c\n8169e1ef-575d-334c-86eb-cff18b2f380f Real Kisser\n\nbad\n");
        Assert.Single(l); Assert.Equal(new UUID("8169e1ef-575d-334c-86eb-cff18b2f380f"), l[0]);
    }

    [Fact] public void KeptItemNeverDetachedBySwap()
    {
        var k = UUID.Random();
        var p = Program.PlanAttachmentSwap(new[] { new Program.WornAtt(k, " ", false) }, new Program.TargetObj[0], new HashSet<UUID> { k });
        Assert.DoesNotContain(k, p.detach);
    }
}
