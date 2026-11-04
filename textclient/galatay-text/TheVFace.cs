// TheVFace.cs (2026-11-03, David: whenever an adult pose is selected, play The V HUD's "Pure Pleasure" face animation, and
// press The V HUD's STOP when that pose ends: a switch to a non-adult pose, standing up, or leaving the seat).
// One tick (ParcelWatchTick, with the re-dress tick) decides from SexualPoseRunningNow(): start once an adult pose runs and the
// Naked flow is done; stop once it has not been adult for TheVFaceStopAfterS (the mid-sit menu walk BACKs through non-adult
// menus for a moment, 2026-11-03 14:48:44), and at once when she is no longer seated. The stop also runs right before any
// re-dress, because dressing again takes The V HUD off.
// Buttons come from routes/the-v-face.json (prim name + face, touched with TouchAttachment like the pubes / piercing buttons):
//   {"pleasure": {"prim": "face_menu_1_2", "face": 3, "open": [{"prim": "sidebar", "face": 1}]}, "stop": {"prim": "sidebar_1", "face": 4}}
// "open" = touches that show the right face page first. Until a button is mapped it is skipped and logged (never guessed).
using System.Text.Json;

namespace GalatayText;

public static partial class Program
{
    internal const double TheVFaceStopAfterS = 6;
    internal sealed record TheVButton(string Prim, int Face);
    internal sealed record TheVFaceMap(List<TheVButton> Open, TheVButton Pleasure, TheVButton Stop);
    static readonly string TheVFaceFile = Path.Combine(RouteDir, "the-v-face.json");
    static volatile bool theVFaceOn;
    static DateTime theVNotAdultSince = DateTime.MinValue;
    static int theVFaceBusy;
    static volatile string theVFacePose = "";   // the pose path the face was (re)started for

    // 2026-11-03, David: the face stops when the adult pose changes and pressing Pure Pleasure again brings it back -> restart it on every adult pose change.
    // pure: "restart" when it plays for an older adult pose than the one running now
    internal static string TheVFaceRestart(bool faceOn, bool seated, bool adultPoseNow, bool flowBusy, string facePose, string poseNow)
        => faceOn && seated && adultPoseNow && !flowBusy && !string.IsNullOrEmpty(poseNow) && !string.Equals(facePose ?? "", poseNow, StringComparison.OrdinalIgnoreCase) ? "restart" : null;
    static string PoseKeyNow() => string.Join(" > ", wLastPosePath ?? new());

    // 2026-11-03, David: STOP the face before anything else when the adult pose ends: every stand-up goes through here
    static void StandFaceFirst(string why = "standing up")
    {
        if (theVFaceOn) { try { TheVFaceStop(why).Wait(4000); } catch { } }
        client.Self.Stand();
    }

    internal static TheVFaceMap ParseTheVFaceMap(string json)
    {
        TheVButton B(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("prim", out var p) && p.ValueKind == JsonValueKind.String
            && e.TryGetProperty("face", out var f) && f.TryGetInt32(out var fi) && fi >= 0 ? new TheVButton(p.GetString(), fi) : null;
        try
        {
            using var d = JsonDocument.Parse(json); var r = d.RootElement;
            TheVButton pl = null, st = null; var open = new List<TheVButton>();
            if (r.TryGetProperty("pleasure", out var pe)) { pl = B(pe); if (pe.ValueKind == JsonValueKind.Object && pe.TryGetProperty("open", out var oe) && oe.ValueKind == JsonValueKind.Array) open = oe.EnumerateArray().Select(B).Where(x => x != null).ToList(); }
            if (r.TryGetProperty("stop", out var se)) st = B(se);
            return new TheVFaceMap(open, pl, st);
        }
        catch { return new TheVFaceMap(new(), null, null); }
    }
    static TheVFaceMap LoadTheVFaceMap() { try { return File.Exists(TheVFaceFile) ? ParseTheVFaceMap(File.ReadAllText(TheVFaceFile)) : new(new(), null, null); } catch { return new(new(), null, null); } }

    // pure: "start", "stop" or null. notAdultS = how long the pose has not been adult (0 while adult).
    internal static string TheVFaceAction(bool faceOn, bool seated, bool adultPoseNow, bool flowBusy, double notAdultS)
    {
        if (!faceOn) return seated && adultPoseNow && !flowBusy ? "start" : null;
        if (!seated) return "stop";
        if (adultPoseNow) return null;
        return notAdultS >= TheVFaceStopAfterS ? "stop" : null;
    }

    static void TheVFaceTick()
    {
        if (!LoggedIn || Volatile.Read(ref theVFaceBusy) == 1) return;
        bool seated = client.Self.SittingOn != 0, adult = SexualPoseRunningNow();
        if (adult || !theVFaceOn) theVNotAdultSince = DateTime.Now;
        var act = TheVFaceAction(theVFaceOn, seated, adult, nakedFlowBusy, (DateTime.Now - theVNotAdultSince).TotalSeconds);
        if (act == null && TheVFaceRestart(theVFaceOn, seated, adult, nakedFlowBusy, theVFacePose, PoseKeyNow()) != null) act = "restart";
        if (act == "start") _ = Task.Run(() => TheVFaceStart("adult pose selected"));
        else if (act == "restart") _ = Task.Run(() => TheVFaceStart("adult pose changed: " + PoseKeyNow(), restart: true));
        else if (act == "stop") _ = Task.Run(() => TheVFaceStop(seated ? "the pose is not adult any more" : "stood up"));
    }

    static async Task<string> TheVTouch(TheVButton b) => await TouchAttachment($"\"{TheVHudName}\" {b.Prim} {b.Face}");

    static async Task TheVFaceStart(string why, bool restart = false)
    {
        if (Interlocked.Exchange(ref theVFaceBusy, 1) == 1) return;
        try
        {
            var m = LoadTheVFaceMap();
            theVFaceOn = true; theVFacePose = PoseKeyNow();   // also when unmapped: one log line per adult pose, not one per tick
            if (m.Pleasure == null) { Log("thev", $"face: Pure Pleasure button not mapped in {TheVFaceFile}; skipped ({why})"); return; }
            foreach (var o in m.Open) { await TheVTouch(o); await Task.Delay(700); }
            Log("thev", $"face: Pure Pleasure {(restart ? "RESTART" : "ON")} ({why}): " + (await TheVTouch(m.Pleasure)).Split('\n')[0]);
        }
        catch (Exception ex) { Log("thev", "face start failed: " + ex.GetBaseException().Message); }
        finally { theVFaceBusy = 0; }
    }

    static async Task TheVFaceStop(string why)
    {
        if (!theVFaceOn) return;
        for (int k = 0; k < 40 && Volatile.Read(ref theVFaceBusy) == 1; k++) await Task.Delay(250);
        if (Interlocked.Exchange(ref theVFaceBusy, 1) == 1) return;
        try
        {
            theVFaceOn = false; theVFacePose = "";
            var m = LoadTheVFaceMap();
            if (m.Stop == null) { Log("thev", $"face: STOP button not mapped in {TheVFaceFile}; skipped ({why})"); return; }
            Log("thev", $"face: STOP ({why}): " + (await TheVTouch(m.Stop)).Split('\n')[0]);
        }
        catch (Exception ex) { Log("thev", "face stop failed: " + ex.GetBaseException().Message); }
        finally { theVFaceBusy = 0; }
    }
}
