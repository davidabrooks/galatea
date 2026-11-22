// WanderPoseRequest.cs (2026-11-22, fixes list; Nov 20 11:21 AM PT): David's in-world "change pose babe" IM put the wander in PAUSE (chat),
// cut the tub sit short ("interrupted after 163 s", planned 1324 s), and 3 min later the resume stood her up and walked a whole leg still
// undressed. David: a pose change asked for in chat should not stop the wander.
//  1) A chat line / IM that asks for a pose change while she is seated in a wander sit does NOT pause the wander: the sit loop changes the
//     pose at its next tick (the same mid-sit SeatPose the timed rotation uses) and the sit keeps its stay timer.
//  2) Resume after an interrupted special-undress sit (the tub): she is dressed again at the seat, before any leg is walked
//     (DressAfterSeatIfPending: clear floor, the change spot, the outfit), never after a whole leg undressed.
using System.Text.RegularExpressions;

namespace GalatayText;

public static partial class Program
{
    static volatile bool wPoseChangeAsked;
    static volatile string wPoseChangeAskedBy;

    static readonly Regex PoseAskRx = new(
        @"\b(change|switch|swap|try|another|other|different|new|next)\b[^.!?\n]{0,25}?\b(poses?|positions?)\b|\bpose change\b|\b(poses?|positions?)\s+(change|swap|switch)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex PoseAskNegRx = new(@"\b(don'?t|do not|dont|no need to|never|stop)\b|\bno\b[^.!?\n]{0,6}\b(change|switch|new|other|another)\b|\b(keep|stay in|love|like)\b[^.!?\n]{0,15}\b(this|that|the|your)\b[^.!?\n]{0,6}\b(pose|position)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // pure (tested): is this line a request to change her pose? ("change pose babe", "try another position", "switch poses?")
    internal static bool IsPoseChangeRequest(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || t.Length > 160) return false;
        return PoseAskRx.IsMatch(t) && !PoseAskNegRx.IsMatch(t);
    }

    // pure (tested): a pose-change request is handled inside the sit (no chat pause) only while she sits in a running wander's sit, no other
    // pause (hold / user / ao / david / rest) owns her, and the line came from David (the only one whose pose requests she follows)
    internal static bool PoseRequestKeepsWander(bool wanderOn, bool seated, string pause, bool fromDavid, string text)
        => wanderOn && seated && fromDavid && (pause == null || pause == "chat") && IsPoseChangeRequest(text);

    // pure (tested): the sit loop changes the pose for a request now: asked, still seated, not paused, no seat adjustment holding the pose
    internal static bool SitChangesPoseOnRequest(bool asked, bool seated, bool paused, bool adjusting) => asked && seated && !paused && !adjusting;

    // pure (tested): before any leg, an undressed resume dresses her at the seat she sat undressed on (the tub), standing first
    internal static bool RedressBeforeLeg(bool pendingDress, bool seated, string pause) => pendingDress && seated && pause == null;

    // the sit loops: consume a pending request (one pose change per request)
    static bool TakePoseChangeRequest(bool seated, bool paused, bool adjusting)
    {
        if (!SitChangesPoseOnRequest(wPoseChangeAsked, seated, paused, adjusting)) return false;
        wPoseChangeAsked = false;
        WLog($"POSE change asked in chat by {wPoseChangeAskedBy ?? "David"}: changing the pose now; the sit and its stay timer go on (no chat pause)");
        return true;
    }
}
