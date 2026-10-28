using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-10-28 11:30 live failure: `idle_pose status` said "standing; AO force sit off" while she visibly sat on the ground. The real state is judged from evidence
// (animations AND the render's pelvis), the render wins on a disagreement, the force-sit state after a press is inferred from the animations that started.
public class RealPoseStateTests
{
    const double Size = 1.566;
    static readonly UUID Sit = UUID.Parse("985d3be4-2b64-1123-c503-8dfa2eb083c6"), Stand = UUID.Parse("be2dba76-6004-28cb-29e4-68f73d54f13b"), Stand2 = UUID.Parse("50fd7ca6-b2e5-9e86-ea8c-8b80f8d89f6c"), Mystery = UUID.Random();

    [Fact] public void RenderPelvis_030IsSitting_085PlusIsStanding_InBetweenIsNoVerdict()
    {
        Assert.Equal(PoseCall.Sitting, PoseEvidence.FromRender(0.30, Size));
        Assert.Equal(PoseCall.Standing, PoseEvidence.FromRender(0.85 + 0.02, Size));
        Assert.Equal(PoseCall.Standing, PoseEvidence.FromRender(1.0, Size));
        Assert.Null(PoseEvidence.FromRender(0.78, Size));      // between the fractions: not decisive
        Assert.Null(PoseEvidence.FromRender(null, Size)); Assert.Null(PoseEvidence.FromRender(0.3, 0));
    }

    [Fact] public void Animations_UnknownAoAnimationIsNeverAStand()
    {
        Assert.Equal(PoseCall.Standing, PoseEvidence.FromAnims(false, false, 0));
        Assert.Equal(PoseCall.Unknown, PoseEvidence.FromAnims(false, false, 1));
        Assert.Equal(PoseCall.Sitting, PoseEvidence.FromAnims(false, true, 1));
        Assert.Equal(PoseCall.Sitting, PoseEvidence.FromAnims(true, false, 0));
    }

    [Fact] public void TheLiveFailure_RenderSitting_AnimationsStanding_IsAMismatch_AndTheRenderWins()
    {
        var r = PoseEvidence.Resolve(PoseCall.Standing, "only classified AO stands", PoseCall.Sitting, "render: pelvis 0.30 m of 1.57 m, 5 s ago");
        Assert.Equal(PoseCall.Sitting, r.State); Assert.True(r.Mismatch);
        Assert.Contains("MISMATCH: render says sitting, status says standing", r.Text);
        Assert.StartsWith("SITTING", r.Text);
    }
    [Fact] public void RenderStanding_AnimationsSitting_IsAMismatchToo()
    {
        var r = PoseEvidence.Resolve(PoseCall.Sitting, "AO sit 985d3be4", PoseCall.Standing, "render");
        Assert.Equal(PoseCall.Standing, r.State); Assert.Contains("MISMATCH: render says standing, status says sitting", r.Text);
    }
    [Fact] public void Agreement_IsReported_AndAnUnknownAnimationDoesNotOverrideTheRender()
    {
        var a = PoseEvidence.Resolve(PoseCall.Standing, "x", PoseCall.Standing, "r"); Assert.False(a.Mismatch); Assert.Contains("agree", a.Text);
        var u = PoseEvidence.Resolve(PoseCall.Unknown, "unclassified", PoseCall.Standing, "r"); Assert.Equal(PoseCall.Standing, u.State); Assert.Contains("inconclusive", u.Text);
    }
    [Fact] public void WithoutARender_TheStatusSaysAnimationsOnly_AndUnknownStaysUnknown()
    {
        var a = PoseEvidence.Resolve(PoseCall.Standing, "only classified AO stands", null, "no render yet");
        Assert.Equal(PoseCall.Standing, a.State); Assert.Contains("animations only", a.Text); Assert.Contains("idle_pose verify", a.Text);
        var u = PoseEvidence.Resolve(PoseCall.Unknown, "unclassified AO f07f8e1e", null, "no render yet");
        Assert.Equal(PoseCall.Unknown, u.State); Assert.StartsWith("UNKNOWN", u.Text);
    }

    [Fact] public void RenderFreshness_AfterTheLastPostureChange_AndRecent()
    {
        var now = new DateTime(2026, 10, 28, 11, 30, 0);
        Assert.True(PoseEvidence.RenderFresh(now.AddSeconds(-20), now, now.AddSeconds(-60)));
        Assert.False(PoseEvidence.RenderFresh(now.AddSeconds(-20), now, now.AddSeconds(-10)));   // taken before the posture changed: shows the old posture
        Assert.False(PoseEvidence.RenderFresh(now.AddSeconds(-61), now, DateTime.MinValue));      // too old
    }
    [Fact] public void HoverKeepsTheLastRender_AndOnlyTheFreshOneCounts()
    {
        var hv = new Hover(null);
        Assert.Null(hv.RenderEvidenceNow().call);
        hv.NoteRender(0.30, Size); var (c, d) = hv.RenderEvidenceNow();
        Assert.Equal(PoseCall.Sitting, c); Assert.Contains("pelvis 0.30 m", d);
        hv.NoteRender(null, Size); Assert.Null(hv.RenderEvidenceNow().call);   // an unreadable pelvis proves nothing
    }

    // ---- force sit after a press: inferred from the animations, never a stored flag ----
    [Fact] public void AfterAPress_ASitAnimationPlaying_MeansForceSitIsOn()
        => Assert.Equal(ForceSitState.On, PoseEvidence.ForceSitAfterPress(new[] { Animations.STAND, Sit }, false, new[] { Sit }, new[] { Stand }, 0));
    [Fact] public void AfterAPress_AClassifiedStandAndNoSit_MeansOff()
        => Assert.Equal(ForceSitState.Off, PoseEvidence.ForceSitAfterPress(new[] { Stand, Stand2 }, false, new[] { Sit }, new[] { Stand, Stand2 }, 0));
    [Fact] public void AfterAPress_AnUnclassifiedAoAnimationOrNoStand_IsUnknown_NeverOff()
    {
        Assert.Equal(ForceSitState.Unknown, PoseEvidence.ForceSitAfterPress(new[] { Stand, Mystery }, false, new[] { Sit }, new[] { Stand }, 1));
        Assert.Equal(ForceSitState.Unknown, PoseEvidence.ForceSitAfterPress(new UUID[0], false, new[] { Sit }, new[] { Stand }, 0));
        Assert.Equal(ForceSitState.Unknown, PoseEvidence.ForceSitAfterPress(new[] { Mystery }, false, new[] { Sit }, new[] { Stand }, 0));
    }
    [Fact] public void ThePressReport_NamesWhatStartedAndStopped()
    {
        var t = PoseEvidence.ForceSitReport(ForceSitState.On, new[] { Stand }, new[] { Sit });
        Assert.Contains("force sit now ON", t); Assert.Contains("started 985d3be4", t); Assert.Contains("stopped be2dba76", t);
        Assert.Contains("none", PoseEvidence.ForceSitReport(ForceSitState.Unknown, new[] { Stand }, new[] { Stand }));
    }
    [Fact] public void ASecondPress_IsNotAllowedUntilTheFirstWasReadBack()
    {
        Assert.False(PoseEvidence.ForcePressAllowed(false));
        Assert.True(PoseEvidence.ForcePressAllowed(true));
    }

    // ---- the hover check must never end silently when the pose is not rendered as expected ----
    [Fact] public void HoverGiveUp_OnAPoseNotRendered_IsLoud()
    {
        var t = Hover.HoverGiveUpText("login", 6, "pose not rendered: the render shows her pelvis only 0.30 m high: sitting, not standing");
        Assert.StartsWith("HOVER CHECK GAVE UP, hover NOT verified", t); Assert.Contains("0.30 m high: sitting", t); Assert.Contains("nothing was changed", t);
    }
    [Fact] public void HoverGiveUp_OtherReasons_KeepTheOldLine()
        => Assert.Equal("login: gave up after 6 tries (moving)", Hover.HoverGiveUpText("login", 6, "moving"));
}
