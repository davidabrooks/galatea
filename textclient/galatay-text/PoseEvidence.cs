// PoseEvidence.cs (2026-10-28 11:30 live failure): `idle_pose status` said "standing; AO force sit off" while she visibly sat on the ground. The status was
// built from the animation list alone, and that list had no sit animation she knew (f07f8e1e is played by the head and was filed under "other"); a press of the
// AO force-sit button then turned force sit ON while the client believed it was off. The real state is now judged from EVIDENCE: the animations playing AND the
// render's pelvis height (mesh.json of a scene export: about 0.30 m = sitting, 0.85 m or more = standing). When they disagree the render wins and the status
// says "MISMATCH: render says X, status says Y". The force-sit state after a press is inferred from the animations that started, never from a stored flag.
using LibreMetaverse;

namespace GalatayText;

internal enum PoseCall { Standing, Sitting, Unknown }
internal enum ForceSitState { On, Off, Unknown }

internal static class PoseEvidence
{
    internal const double RenderFreshSec = 60;
    internal sealed record RealPose(PoseCall State, bool Mismatch, string Text);

    /// <summary>Pure: what the animations say. On an object or an AO/built-in sit playing = sitting; an unclassified AO animation = unknown (never a stand); else standing.</summary>
    internal static PoseCall FromAnims(bool onObject, bool aoSitPlaying, int unclassifiedAo) =>
        onObject || aoSitPlaying ? PoseCall.Sitting : unclassifiedAo > 0 ? PoseCall.Unknown : PoseCall.Standing;

    /// <summary>Pure: what the render says from the posed pelvis height (m above her feet level) and her height; null = no decisive render
    /// (no pelvis, or between the sit and stand fractions of Hover.PoseRenderMismatch).</summary>
    internal static PoseCall? FromRender(double? pelvisZ, double size)
    {
        if (pelvisZ is not double pz || size <= 0) return null;
        if (pz <= Hover.SitPelvisMaxFrac * size) return PoseCall.Sitting;
        if (pz >= Hover.StandPelvisMinFrac * size) return PoseCall.Standing;
        return null;
    }

    /// <summary>Pure: a render counts while it is recent and taken after the last posture change (an older one shows the previous posture).</summary>
    internal static bool RenderFresh(DateTime takenAt, DateTime now, DateTime postureChangedAt) =>
        takenAt >= postureChangedAt && now >= takenAt && (now - takenAt).TotalSeconds <= RenderFreshSec;

    static string Word(PoseCall c) => c switch { PoseCall.Sitting => "sitting", PoseCall.Standing => "standing", _ => "unknown" };

    /// <summary>Pure: the real state and the status line. The render wins over the animations; a disagreement is named.</summary>
    internal static RealPose Resolve(PoseCall anims, string animsDetail, PoseCall? render, string renderDetail)
    {
        if (render is PoseCall r)
        {
            bool mismatch = anims != PoseCall.Unknown && anims != r;
            string t = mismatch ? $"{Word(r).ToUpperInvariant()} (MISMATCH: render says {Word(r)}, status says {Word(anims)}; the render wins) [{renderDetail}; animations: {animsDetail}]"
                : anims == PoseCall.Unknown ? $"{Word(r).ToUpperInvariant()} (render says {Word(r)}; animations are inconclusive) [{renderDetail}; animations: {animsDetail}]"
                : $"{Word(r).ToUpperInvariant()} (render and animations agree) [{renderDetail}; animations: {animsDetail}]";
            return new(r, mismatch, t);
        }
        return new(anims, false, anims == PoseCall.Unknown ? $"UNKNOWN (animations unclassified: {animsDetail}; no fresh render: run `idle_pose verify`)"
            : $"{Word(anims).ToUpperInvariant()} (animations only: {animsDetail}; no fresh render: run `idle_pose verify`)");
    }

    /// <summary>Pure: the AO force-sit state after a press, inferred from the animations now playing: a sit animation playing = ON; no sit playing, a classified AO stand
    /// playing and no unclassified AO animation = OFF; anything else = UNKNOWN (the render and David's eyes decide). Never read from a stored flag.</summary>
    internal static ForceSitState ForceSitAfterPress(IEnumerable<UUID> after, bool onObject, IEnumerable<UUID> sitIds, IEnumerable<UUID> standIds, int unclassifiedAoAfter)
    {
        var a = (after ?? Enumerable.Empty<UUID>()).ToList();
        if (Program.AoSitPlaying(a, onObject, sitIds)) return ForceSitState.On;
        var st = new HashSet<UUID>(standIds ?? Enumerable.Empty<UUID>());
        return a.Any(st.Contains) && unclassifiedAoAfter == 0 ? ForceSitState.Off : ForceSitState.Unknown;
    }

    /// <summary>Pure: the log / status line of a press: what started and stopped, and what force sit is now.</summary>
    internal static string ForceSitReport(ForceSitState s, IEnumerable<UUID> before, IEnumerable<UUID> after)
    {
        var b = new HashSet<UUID>(before ?? Enumerable.Empty<UUID>()); var a = new HashSet<UUID>(after ?? Enumerable.Empty<UUID>());
        string Ids(IEnumerable<UUID> x) { var l = x.Select(u => u.ToString()[..8]).OrderBy(u => u, StringComparer.Ordinal).ToList(); return l.Count == 0 ? "none" : string.Join(",", l); }
        return $"AO force sit now {s.ToString().ToUpperInvariant()} (inferred from the animations: started {Ids(a.Where(u => !b.Contains(u)))}; stopped {Ids(b.Where(u => !a.Contains(u)))})";
    }

    /// <summary>Pure: may the AO force-sit button be pressed now? Never while the previous press has not been read back (~11 s later, anim list re-read).</summary>
    internal static bool ForcePressAllowed(bool previousPressReadBack) => previousPressReadBack;
}
