// HoverHost.cs: the explicit list of what the hover code (Hover: PoseHover.cs, HoverCalibrate.cs, AutoHover.cs) and the feet code
// (FeetAuto.cs) use from the rest of the client. Before 2026-10-28 these were `partial class Program` files that read any Program
// field directly; now each member they touch is one line below and a test can pass a fake host. ProgramHost is the only place
// that reaches into Program's statics. Stateless helpers (Program.Env, P3, BodyFamily, the pure planners) are still called as
// Program.X: they hold no shared state.
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LibreMetaverse;
using FurnitureBox = GalatayText.Program.FurnitureBox;
using HomeSeatInfo = GalatayText.Program.HomeSeatInfo;
using IdlePoseState = GalatayText.Program.IdlePoseState;
using NavGrid = GalatayText.Program.NavGrid;

namespace GalatayText;

/// <summary>What the feet code needs: the session, the worn attachments and the HUD touch.</summary>
internal interface IFeetHost
{
    // session + logging
    GridClient Client { get; }
    Simulator Sim { get; }
    void Log(string kind, string msg);
    string RouteDir { get; }
    // worn attachments
    List<Primitive> WornPrims();
    UUID AttachItemId(Primitive p);
    Task<string> AttachItem(UUID item, string pointArg);
    Task<string> DetachItemAsync(UUID item, string why);
    List<Primitive> LinkPrims(Primitive root);
    Task EnsureProperties(Simulator sim, List<Primitive> prims);
    Task<string> TouchAttachment(string rest);
    // 2026-10-28: when a press may happen, and the evidence that the foot fits the shoe
    List<UUID> SelfAnims();
    bool PoseBusy { get; }            // a pose / sit transition is in progress (idle pose entry or exit, AO sit settling)
    double PoseSettledSec { get; }    // seconds since the posture last changed
    bool Sitting { get; }             // sitting on an object or an AO ground sit
    /// <summary>One `look self feet` close-up at the given exposure (stops), analysed by vision/feet_fit.py: its one json line, or null when no image came out.</summary>
    Task<string> FeetCloseupJson(double exposure);
}

/// <summary>What the hover code needs: the session, hover state in force, the pose state, a few pose actions, the scene export and the paths.</summary>
internal interface IHoverHost
{
    // session + logging
    GridClient Client { get; }
    Simulator Sim { get; }
    bool LoggedIn { get; }
    Avatar SelfAv();
    List<UUID> SelfAnims();
    List<Primitive> WornPrims();
    void Log(string kind, string msg);
    void RlvLog(string line);
    // paths of the renderer and the calibration file
    string LogPath { get; }
    string LookPy { get; }
    string LookPython { get; }
    // the hover in force and how to change it (HeightGuard.cs)
    double? EffectiveHoverNow();
    Task<(double? v, string src)> EffectiveHoverFresh();
    double PinnedHover();
    Task<(bool ok, string info)> PostHover(double v, string why);
    // the pose she is really in (AoSit.cs, IdlePose.cs, Leash.cs, Follow)
    bool AoSitReallyOn();
    HashSet<UUID> AoSitIdsNow();
    List<UUID> AoUnclassifiedNow(IEnumerable<UUID> playing = null);
    bool AoForceSitOn(IEnumerable<UUID> playing, bool sittingOnObject);
    string AoSitCurrentName { get; }
    IdlePoseState IdlePosed { get; }
    bool IdleLeashedNow();
    bool LeashActive { get; }
    bool Following { get; }
    bool IdleBusy { get; }
    bool AndroidFormNow();
    string CurrentOutfit { get; }
    // pose actions the hover check may take (stand up off furniture, step to clear floor, release a stuck AO sit once)
    Task<string> IdleExplicitStand(string why);
    void SuppressAutoSit(double seconds);
    SemaphoreSlim StandOutLock { get; }
    Task StandOutMoveTo(string label, Vector3 me, Vector3 to, bool hopFirst, FurnitureBox inBox, CancellationToken ct);
    Task<string> StuckSitRelease(string reason);
    int StuckSitPresses { get; }
    DateTime StuckSitLoginAt { get; }
    // scene + map
    Task<string> SceneExport(string[] a);
    List<FurnitureBox> HomeFootprints();
    NavGrid NavGridHere();
    List<HomeSeatInfo> LoadHomeSeats();
    bool InPeronaut { get; }
}

/// <summary>The one adapter from the interfaces above to Program's statics. Nested in Program so it reaches the private members without widening them.</summary>
public static partial class Program
{
    internal sealed class ProgramHost : IHoverHost, IFeetHost
    {
        public GridClient Client => Program.client;
        public Simulator Sim => Program.Sim;
        public bool LoggedIn => Program.LoggedIn;
        public Avatar SelfAv() => Program.SelfAv();
        public List<UUID> SelfAnims() => Program.SelfAnims();
        public List<Primitive> WornPrims() => Program.WornPrims();
        public void Log(string kind, string msg) => Program.Log(kind, msg);
        public void RlvLog(string line) => Program.RlvLog(line);
        public string LogPath => Program.LogPath;
        public string LookPy => Program.LookPy;
        public string LookPython => Program.LookPython;
        public string RouteDir => Program.RouteDir;
        public double? EffectiveHoverNow() => Program.EffectiveHoverNow();
        public Task<(double? v, string src)> EffectiveHoverFresh() => Program.EffectiveHoverFresh();
        public double PinnedHover() => Program.PinnedHover();
        public Task<(bool ok, string info)> PostHover(double v, string why) => Program.PostHover(v, why);
        public bool AoSitReallyOn() => Program.AoSitReallyOn();
        public HashSet<UUID> AoSitIdsNow() => Program.AoSitIdsNow();
        public List<UUID> AoUnclassifiedNow(IEnumerable<UUID> playing = null) => Program.AoUnclassifiedNow(playing);
        public bool AoForceSitOn(IEnumerable<UUID> playing, bool sittingOnObject) => Program.AoForceSitOn(playing, sittingOnObject);
        public string AoSitCurrentName => Program.aoSitCurrentName;
        public IdlePoseState IdlePosed => Program.idlePosed;
        public bool IdleLeashedNow() => Program.IdleLeashedNow();
        public bool LeashActive => Program.leash != null;
        public bool Following => Program.followId != UUID.Zero;
        public bool IdleBusy => Program.idleLock.CurrentCount == 0;
        public bool AndroidFormNow() => Program.AndroidFormNow();
        public string CurrentOutfit => Program.currentOutfit;
        public Task<string> IdleExplicitStand(string why) => Program.IdleExplicitStand(why);
        public void SuppressAutoSit(double seconds) => Program.idleNoSitUntil = DateTime.Now.AddSeconds(seconds);
        public SemaphoreSlim StandOutLock => Program.standOutLock;
        public Task StandOutMoveTo(string label, Vector3 me, Vector3 to, bool hopFirst, FurnitureBox inBox, CancellationToken ct) => Program.StandOutMoveTo(label, me, to, hopFirst, inBox, ct);
        public Task<string> StuckSitRelease(string reason) => Program.StuckSitRelease(reason);
        public int StuckSitPresses => Program.stuckSitPresses;
        public DateTime StuckSitLoginAt => Program.stuckSitLoginAt;
        public Task<string> SceneExport(string[] a) => Program.SceneExport(a);
        public List<FurnitureBox> HomeFootprints() => Program.HomeFootprints();
        public NavGrid NavGridHere() => Program.NavGridHere();
        public List<HomeSeatInfo> LoadHomeSeats() => Program.LoadHomeSeats();
        public bool InPeronaut => Program.InPeronaut;
        public Task<string> AttachItem(UUID item, string pointArg) => Program.AttachItem(item, pointArg);
        public Task<string> DetachItemAsync(UUID item, string why) => Program.DetachItemAsync(item, why);
        public UUID AttachItemId(Primitive p) => Program.AttachItemId(p);
        public List<Primitive> LinkPrims(Primitive root) => Program.LinkPrims(root);
        public Task EnsureProperties(Simulator sim, List<Primitive> prims) => Program.EnsureProperties(sim, prims);
        public Task<string> TouchAttachment(string rest) => Program.TouchAttachment(rest);
        public bool PoseBusy => Program.idleLock.CurrentCount == 0 || DateTime.Now < Program.aoSitTransitionUntil;
        public double PoseSettledSec => (DateTime.Now - Program.hoverSvc.PostureChangedAt).TotalSeconds;
        public bool Sitting => Program.client.Self.SittingOn != 0 || Program.AoSitReallyOn();
        public async Task<string> FeetCloseupJson(double exposure)
        {
            var r = await Program.LookCmd(new[] { "self", "feet", "fast", "zoom=feet", "exposure=" + exposure.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) });
            var m = System.Text.RegularExpressions.Regex.Matches(r ?? "", @"(/[^\s]+\.jpg)"); if (m.Count == 0) return null;
            var jpg = m[^1].Value; var py = Path.Combine(Path.GetDirectoryName(Program.LookPy) ?? ".", "feet_fit.py");
            if (!File.Exists(py) || !File.Exists(jpg)) return null;
            var psi = new System.Diagnostics.ProcessStartInfo(Program.LookPython) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(py); psi.ArgumentList.Add(jpg);
            using var pr = System.Diagnostics.Process.Start(psi)!; var o = pr.StandardOutput.ReadToEndAsync(); _ = pr.StandardError.ReadToEndAsync();
            if (!await Task.Run(() => pr.WaitForExit(30000))) { try { pr.Kill(); } catch { } return null; }
            var line = (await o).Trim().Split('\n').LastOrDefault(); return line == null ? null : line.TrimEnd('}') + $", \"image\": \"{jpg}\"}}";
        }
    }
}
