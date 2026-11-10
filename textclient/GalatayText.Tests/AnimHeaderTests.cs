using GalatayText;
using LibreMetaverse;
using System.Text;
using Xunit;
using static GalatayText.Program;
// 2026-11-09 (David t3223u-t3225u): animation headers. A non-looping gesture animation ends at its length (no 30 s hold);
// the visibility note names the joints a higher-priority playing animation (her AO stand) takes from the gesture.
public class AnimHeaderTests
{
    static readonly UUID Hello = new("9b29cd61-c45b-5689-ded2-91756b8d76a9"), Clap = new("9b0c1c4e-8ac7-7969-1494-28c874c4f668");
    static readonly UUID Stand = new("6fea01aa-b4bc-fc7b-e699-27cee5744978"), Hands = new("08630d35-8417-803b-d9f9-946af813af65");

    // a binary animation (the viewer's .anim layout): header, joints with keys, no constraints
    internal static byte[] Anim(int prio, float len, bool loop, params (string name, int prio, int rotKeys)[] joints)
    {
        var m = new MemoryStream(); var w = new BinaryWriter(m);
        w.Write((ushort)1); w.Write((ushort)0); w.Write(prio); w.Write(len); w.Write((byte)0);
        w.Write(0f); w.Write(len); w.Write(loop ? 1 : 0); w.Write(0.3f); w.Write(0.3f); w.Write(0u); w.Write((uint)joints.Length);
        foreach (var (n, p, k) in joints)
        {
            w.Write(Encoding.ASCII.GetBytes(n)); w.Write((byte)0); w.Write(p); w.Write(k);
            for (int i = 0; i < k; i++) { w.Write((ushort)(i * 1000)); w.Write((ushort)32768); w.Write((ushort)32768); w.Write((ushort)32768); }
            w.Write(0);   // position keys
        }
        w.Write(0);   // constraints
        return m.ToArray();
    }
    static AnimHead H(int prio, float len, bool loop, params (string, int, int)[] j) => AnimHeadParse(Anim(prio, len, loop, j));

    [Fact] public void Parse_PriorityLengthLoop_AndOnlyKeyedJoints()
    {
        var h = H(2, 2.5f, false, ("mShoulderRight", -1, 3), ("mElbowRight", 4, 2), ("mHead", 3, 0));
        Assert.Equal(2, h.Priority); Assert.Equal(2.5, h.Length, 3); Assert.False(h.Loop);
        Assert.Equal(2, h.Joints["mShoulderRight"]);   // -1 = the animation's priority
        Assert.Equal(4, h.Joints["mElbowRight"]);
        Assert.False(h.Joints.ContainsKey("mHead"));   // no keys: does not drive the bone
    }
    [Fact] public void NaturalEnd_NonLoopingAtItsLengthPlusMargin_LoopingOrUnknownNever()
    {
        Assert.Equal(3.0, AnimNaturalEnd(H(2, 2.5f, false, ("mShoulderRight", -1, 2)))!.Value, 3);
        Assert.Null(AnimNaturalEnd(H(4, 6.9f, true, ("mPelvis", -1, 2))));
        Assert.Null(AnimNaturalEnd(null));
        Assert.Null(AnimNaturalEnd(H(2, 0f, false, ("mPelvis", -1, 2))));
        Assert.Equal(60.5, AnimNaturalEnd(H(2, 500f, false, ("mPelvis", -1, 2)))!.Value, 3);   // capped
    }
    [Fact] public void Hidden_AHigherPriorityStandTakesTheArms_EqualPriorityDoesNot()
    {
        var wave = H(2, 2.5f, false, ("mShoulderRight", -1, 2), ("mElbowRight", -1, 2), ("mWristRight", -1, 2));
        var aoStand = H(4, 8f, true, ("mShoulderRight", -1, 2), ("mElbowRight", -1, 2), ("mPelvis", -1, 2));
        var fingers = H(6, 0f, true, ("mHandIndex1Right", -1, 2));
        var hidden = AnimHiddenJoints(wave, new[] { (Stand, aoStand), (Hands, fingers), (Clap, (AnimHead)null) });
        Assert.Equal(new[] { "mElbowRight", "mShoulderRight" }, hidden.Select(x => x.Joint));
        Assert.All(hidden, x => { Assert.Equal(Stand, x.By); Assert.Equal(4, x.Other); Assert.Equal(2, x.Mine); });
        Assert.Empty(AnimHiddenJoints(H(4, 2f, false, ("mShoulderRight", -1, 2)), new[] { (Stand, aoStand) }));   // equal: the later start (the gesture) wins
        var t = AnimVisibilityText("Wave", wave, hidden, 1, id => id == Stand ? "AO stand" : "?");
        Assert.Contains("HIDDEN on 2/3 joint(s) by AO stand P4 on 2", t); Assert.Contains("1 playing animation(s) unreadable", t);
        Assert.Contains("visible over everything playing", AnimVisibilityText("Wave", wave, new(), 0, _ => ""));
        Assert.Contains("priority unknown", AnimVisibilityText("Wave", null, new(), 0, _ => ""));
    }

    // ---- the player: a non-looping animation the sim keeps listed ends at its length, not at the 30 s cap
    sealed class StickyIo : IGestureIo   // the sim lists every started animation until it is stopped (what it did for HELLO / CLAP)
    {
        public DateTime T = new(2026, 11, 9, 21, 8, 0); public DateTime Now => T;
        public readonly Dictionary<UUID, double?> End = new(); readonly HashSet<UUID> playing = new(); readonly Dictionary<UUID, DateTime> seen = new();
        public readonly List<string> Out = new();
        public Task Delay(int ms, CancellationToken ct) { ct.ThrowIfCancellationRequested(); T = T.AddMilliseconds(Math.Max(ms, 1)); return Task.CompletedTask; }
        public bool AnimPlaying(UUID id) => playing.Contains(id);
        public bool AnimSeenSince(UUID id, DateTime t) => seen.TryGetValue(id, out var s) && s >= t;
        public void AnimStart(UUID id) { playing.Add(id); seen[id] = T; Out.Add("start " + id.ToString()[..4]); }
        public void AnimStop(UUID id) { playing.Remove(id); Out.Add("stop " + id.ToString()[..4]); }
        public string Sound(UUID id) { Out.Add("sound"); return null; }
        public string Chat(int c, string t) { Out.Add("chat"); return null; }
        public void Log(string m) => Out.Add("log " + m);
        public double? AnimEndSec(UUID id) => End.TryGetValue(id, out var e) ? e : null;
    }
    static GestureDef Def(params GStep[] s) => new("/t", "", s.ToList());

    [Fact] public async Task NonLooping_EndsAtItsLength_AndIsStopped_NotA30sHold()
    {
        var io = new StickyIo(); io.End[Hello] = 3.0;
        var r = await GestureRun("/wave", Def(new GStep(GStepKind.Anim, "Wave", Hello)), io, new GestureLimits(), default);
        Assert.True(r.Ok); Assert.InRange(r.Seconds, 3.0, 3.3);
        Assert.Equal(new[] { Hello }, r.EndedAtLength); Assert.Empty(r.StoppedAtEnd);
        Assert.Equal(new[] { "start 9b29", "stop 9b29" }, io.Out.Where(x => !x.StartsWith("log")).ToArray());
        Assert.Contains(io.Out, x => x.Contains("ended at their length (not looped), stop sent"));
    }
    [Fact] public async Task WaitForAnimations_UsesTheLengthToo()
    {
        var io = new StickyIo(); io.End[Clap] = 1.5;
        var r = await GestureRun("/clap", Def(new GStep(GStepKind.Anim, "Clap", Clap), new GStep(GStepKind.Wait, WaitAnims: true), new GStep(GStepKind.Chat, Text: "after")), io, new GestureLimits(), default, chat: true);
        Assert.True(r.Ok); Assert.InRange(r.Seconds, 1.5, 1.8); Assert.Contains(io.Out, x => x.Contains("wait for animations: done"));
    }
    [Fact] public async Task UnknownOrLooping_StillTheCapFallback()
    {
        var io = new StickyIo();   // no header: AnimEndSec null
        var r = await GestureRun("/x", Def(new GStep(GStepKind.Anim, "x", Stand)), io, new GestureLimits(EndWaitSec: 30), default);
        Assert.True(r.Ok); Assert.InRange(r.Seconds, 30, 30.5); Assert.Equal(new[] { Stand }, r.StoppedAtEnd); Assert.Empty(r.EndedAtLength);
    }

    // ---- wiring
    [Fact] public void Wiring_LiveIoReadsTheHeader_PlayReportsVisibility_ClientCmdAllowsAnimInfo()
    {
        var g = File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text/Gestures.cs"));
        Assert.Contains("public double? AnimEndSec(UUID id) => AnimNaturalEnd(AnimHeadCached(id));", g);
        var play = g[g.IndexOf("static async Task<string> GesturePlay(")..];
        Assert.True(play.IndexOf("GestureVisibility(def)") < play.IndexOf("GestureRun("), "headers are fetched with the play, so the length is known when it ends");
        Assert.Contains("visibility: {vis}", play);
        Assert.Contains("AnimHeaders.cs", File.ReadAllText(Path.Combine(Root(), "textclient/galatay-mcp/GalatayMcp.csproj")));
        Assert.Equal((true, "anim check", (string)null), ClientCmdGate("anim check"));
        Assert.True(ClientCmdGate($"anim info {Hello}").ok);
        Assert.False(ClientCmdGate($"anim stop {Hello}").ok); Assert.False(ClientCmdGate($"anim start {Hello}").ok); Assert.False(ClientCmdGate("anim info nope").ok);
    }
    static string Root() { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
}
