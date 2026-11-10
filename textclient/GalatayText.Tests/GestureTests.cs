using GalatayText;
using LibreMetaverse;
using Xunit;
using static GalatayText.Program;
// 2026-11-09 (David t3205u): gesture support. Our own step player (viewer semantics), not LMV's PlayGestureAsync.
public class GestureTests
{
    static readonly UUID Wave = new("c541c47f-e0c0-058b-ad1a-d6ae3a4584d9");   // express_wave style ids (any uuid works)
    static readonly UUID Clap = new("9b0c1c4e-8ac7-7969-1494-28c874c4f668");
    static readonly UUID Snd = new("a71890f1-0dab-8744-fd47-7defaf411dbf");
    const string WaveAsset = "2\r\n0\r\n0\r\n/wave\r\n\r\n4\r\n0\r\nexpress_wave\r\nc541c47f-e0c0-058b-ad1a-d6ae3a4584d9\r\n0\r\n3\r\n0.000000\r\n2\r\n2\r\nHi everyone!\r\n0\r\n1\r\nboing\r\na71890f1-0dab-8744-fd47-7defaf411dbf\r\n0\r\n\0";

    [Fact] public void Parse_AViewerAsset_WithCrLfAndATrailingNul()
    {
        var (g, err) = GestureParse(WaveAsset);
        Assert.Null(err); Assert.Equal("/wave", g.Trigger); Assert.Equal("", g.ReplaceWith); Assert.Equal(4, g.Steps.Count);
        Assert.Equal(new GStep(GStepKind.Anim, "express_wave", Wave), g.Steps[0]);
        Assert.True(g.Steps[1].WaitAnims); Assert.False(g.Steps[1].WaitTime);
        Assert.Equal("Hi everyone!", g.Steps[2].Text);   // no '\r' left in the chat text (LMV keeps it)
        Assert.Equal(GStepKind.Sound, g.Steps[3].Kind); Assert.Equal(Snd, g.Steps[3].Id);
    }
    [Fact] public void Parse_AnimFlags_OnlyBitOneIsStop()   // LMV: any non-zero flag = stop
    {
        Assert.True(GestureParse($"2\n0\n0\n/x\n\n1\n0\nclap\n{Clap}\n1\n").def.Steps[0].Stop);
        Assert.False(GestureParse($"2\n0\n0\n/x\n\n1\n0\nclap\n{Clap}\n2\n").def.Steps[0].Stop);
    }
    [Fact] public void Parse_WaitFlags_TimeAndAnims()
    {
        var s = GestureParse("2\n0\n0\n/w\n\n2\n3\n1.500000\n1\n3\n0\n3\n").def.Steps;
        Assert.True(s[0].WaitTime); Assert.Equal(1.5f, s[0].Seconds); Assert.True(s[1].WaitTime && s[1].WaitAnims);
    }
    [Fact] public void Parse_EofEndsTheSequence() => Assert.Single(GestureParse($"2\n0\n0\n/e\n\n3\n2\nhello\n0\n4\n0\nclap\n{Clap}\n0\n").def.Steps);
    [Theory]
    [InlineData("1\n0\n0\n/x\n\n0\n", "version 1")]
    [InlineData("2\n0\n0\n/x\n\n1\n0\nclap\nnot-a-uuid\n0\n", "not a uuid")]
    [InlineData("2\n0\n0\n/x\n\n2\n2\nhi\n0\n", "ends before")]
    [InlineData("2\n0\n0\n/x\n\n1\n9\n", "unknown step type 9")]
    [InlineData("", "empty")]
    [InlineData("2\nzz\n", "not a number")]
    public void Parse_BrokenAssetsSayWhy(string text, string why) { var (g, err) = GestureParse(text); Assert.Null(g); Assert.Contains(why, err); }

    [Fact] public void ChatChannel()
    {
        Assert.Equal((0, "Hi"), GestureChatChannel("Hi"));
        Assert.Equal((5, "secret"), GestureChatChannel("/5 secret"));
        Assert.Equal((-3, "x"), GestureChatChannel("/-3x"));
        Assert.Equal((0, "/me waves"), GestureChatChannel("/me waves"));
    }

    static readonly List<(string name, string trigger, bool active)> Items = new()
    {
        ("Female - Hey", "hey", false), ("/wave", "/wave", true), ("Wave (old)", "/wave", false), ("dance1", "dance1", true), ("dance2", "dance2", true), ("/bow", null, false),
    };
    [Fact] public void Resolve_ActiveTriggerFirst_ThenName_ThenAUniquePart()
    {
        Assert.Equal(1, GestureResolve("/WAVE", Items).idx);
        Assert.Equal(0, GestureResolve("hey", Items).idx);
        Assert.Equal(5, GestureResolve("bow", Items).idx);          // leading '/' optional on a name
        Assert.Equal(0, GestureResolve("Female", Items).idx);       // unique part
        Assert.Contains("ambiguous", GestureResolve("dance", Items).error);
        var (i, e) = GestureResolve("moonwalk", Items); Assert.Equal(-1, i); Assert.StartsWith("unknown gesture 'moonwalk'", e);
        Assert.StartsWith("usage", GestureResolve("  ", Items).error);
    }

    static readonly List<(string, string)> Active = new() { ("/wave", ""), ("/bow", "bows deeply"), ("hi", "hi") };
    [Fact] public void ChatTrigger_ReplacedOrDropped_OnlyTheFirst()
    {
        Assert.Equal(("hello you", 0), GestureTriggerRewrite("/wave hello you", Active));
        Assert.Equal(("", 0), GestureTriggerRewrite("/WAVE", Active));
        Assert.Equal(("bows deeply to David", 1), GestureTriggerRewrite("/bow to David", Active));
        Assert.Equal(("Hi /wave", 2), GestureTriggerRewrite("Hi /wave", Active));   // her capitalization kept; only the first trigger plays
        Assert.Equal(("wave at him", -1), GestureTriggerRewrite("wave at him", Active));
        Assert.Equal(("x", -1), GestureTriggerRewrite("x", new List<(string, string)>()));
    }

    // ---- the player, with a fake clock: an animation plays from +0.2 s for its length (or forever when looped)
    sealed class FakeIo : IGestureIo
    {
        public DateTime T = new(2026, 11, 9, 17, 0, 0); public DateTime Now => T;
        public readonly Dictionary<UUID, double> Length = new();   // seconds; missing = never starts on the sim
        readonly Dictionary<UUID, DateTime> startedAt = new(); readonly HashSet<UUID> stopped = new();
        public readonly List<string> Out = new(); public string SoundSkip, ChatSkip; public readonly List<(int, string)> Said = new();
        public Task Delay(int ms, CancellationToken ct) { ct.ThrowIfCancellationRequested(); T = T.AddMilliseconds(Math.Max(ms, 1)); return Task.CompletedTask; }
        public bool AnimPlaying(UUID id) => startedAt.TryGetValue(id, out var s) && !stopped.Contains(id) && Length.TryGetValue(id, out var len) && T >= s.AddSeconds(0.2) && (double.IsInfinity(len) || T < s.AddSeconds(0.2 + len));
        public bool AnimSeenSince(UUID id, DateTime t) => startedAt.TryGetValue(id, out var s) && Length.ContainsKey(id) && T >= s.AddSeconds(0.2) && s.AddSeconds(0.2) >= t;
        public void AnimStart(UUID id) { startedAt[id] = T; stopped.Remove(id); Out.Add("start " + id.ToString()[..4]); }
        public void AnimStop(UUID id) { stopped.Add(id); Out.Add("stop " + id.ToString()[..4]); }
        public string Sound(UUID id) { Out.Add("sound"); return SoundSkip; }
        public string Chat(int ch, string text) { if (ChatSkip != null) return ChatSkip; Said.Add((ch, text)); Out.Add("chat " + text); return null; }
        public void Log(string msg) => Out.Add("log " + msg);
    }
    static GestureDef Def(params GStep[] s) => new("/t", "", s.ToList());
    static GStep A(UUID id, bool stop = false) => new(GStepKind.Anim, "a", id, stop);
    static GStep WaitAnims() => new(GStepKind.Wait, WaitAnims: true);
    static GStep WaitSec(float s) => new(GStepKind.Wait, Seconds: s, WaitTime: true);
    static GStep Say(string t) => new(GStepKind.Chat, Text: t);

    [Fact] public async Task Run_StepsInOrder_WaitForAnimationsReallyWaits()
    {
        var io = new FakeIo(); io.Length[Wave] = 2.5;
        var r = await GestureRun("/wave", Def(A(Wave), WaitAnims(), Say("Hi!"), new GStep(GStepKind.Sound, "boing", Snd)), io, new GestureLimits(), default, sounds: true, chat: true);
        Assert.True(r.Ok); Assert.Equal(4, r.StepsRun); Assert.Equal(1, r.Chats); Assert.Empty(r.StoppedAtEnd);
        Assert.Equal(new[] { "start c541", "chat Hi!", "sound" }, io.Out.Where(x => !x.StartsWith("log")).ToArray());
        Assert.InRange(r.Seconds, 2.7, 3.0);   // the chat came after the wave ended (LMV: at once)
        Assert.Contains(io.Out, x => x.Contains("step 2/4 wait for animations: done"));
        Assert.Contains(io.Out, x => x.Contains("'/wave': done"));
    }
    [Fact] public async Task Run_WaitTime_AndChannelChat()
    {
        var io = new FakeIo();
        var r = await GestureRun("/t", Def(WaitSec(1.5f), Say("/7 ping")), io, new GestureLimits(), default, chat: true);
        Assert.True(r.Ok); Assert.Equal((7, "ping"), io.Said.Single()); Assert.InRange(r.Seconds, 1.5, 1.6);
    }
    [Fact] public async Task Run_LoopedAnimation_StoppedAtTheEnd_SoSheStandsAgain()
    {
        var io = new FakeIo(); io.Length[Clap] = double.PositiveInfinity;
        var r = await GestureRun("/clap", Def(A(Clap)), io, new GestureLimits(EndWaitSec: 30), default);
        Assert.True(r.Ok); Assert.Equal(new[] { Clap }, r.StoppedAtEnd); Assert.Equal("stop 9b0c", io.Out.Last(x => !x.StartsWith("log")));
        Assert.InRange(r.Seconds, 30, 30.5);
    }
    [Fact] public async Task Run_WaitForAnimations_GivesUpAfter30sAndContinues()
    {
        var io = new FakeIo(); io.Length[Clap] = double.PositiveInfinity;
        var r = await GestureRun("/c", Def(A(Clap), WaitAnims(), Say("after")), io, new GestureLimits(EndWaitSec: 0), default, chat: true);
        Assert.True(r.Ok); Assert.Contains(io.Out, x => x.Contains("waited too long")); Assert.Contains((0, "after"), io.Said);
    }
    [Fact] public async Task Run_AnAnimationTheSimNeverPlays_CountsAsDoneAfter3s()
    {
        var io = new FakeIo();   // no Length: never starts
        var r = await GestureRun("/x", Def(A(Wave), WaitAnims()), io, new GestureLimits(), default);
        Assert.True(r.Ok); Assert.InRange(r.Seconds, 3, 3.3); Assert.Empty(r.StoppedAtEnd);
    }
    [Fact] public async Task Run_Timeout_StopsWhatItStarted()
    {
        var io = new FakeIo(); io.Length[Clap] = double.PositiveInfinity;
        var r = await GestureRun("/long", Def(A(Clap), WaitSec(50), WaitSec(50), Say("never")), io, new GestureLimits(MaxSec: 60, MaxWaitSec: 60), default);
        Assert.False(r.Ok); Assert.StartsWith("timeout", r.Error); Assert.Empty(io.Said); Assert.Equal(new[] { Clap }, r.StoppedAtEnd);
        Assert.Contains(io.Out, x => x.Contains("ERROR timeout"));
    }
    [Fact] public async Task Run_Cancelled_StopsItsAnimations()
    {
        var io = new FakeIo(); io.Length[Clap] = double.PositiveInfinity; using var cts = new CancellationTokenSource();
        var slow = new CancelAfterIo(io, cts, 2.0);
        var r = await GestureRun("/c", Def(A(Clap), WaitSec(10), Say("never")), slow, new GestureLimits(), cts.Token);
        Assert.False(r.Ok); Assert.Equal("cancelled", r.Error); Assert.Equal(new[] { Clap }, r.StoppedAtEnd); Assert.Empty(io.Said);
    }
    sealed class CancelAfterIo : IGestureIo   // cancels the token once the fake clock passed `sec`
    {
        readonly FakeIo io; readonly CancellationTokenSource cts; readonly DateTime at;
        public CancelAfterIo(FakeIo io, CancellationTokenSource cts, double sec) { this.io = io; this.cts = cts; at = io.T.AddSeconds(sec); }
        public DateTime Now => io.Now;
        public async Task Delay(int ms, CancellationToken ct) { for (int k = 0; k < ms; k += 100) { if (io.T >= at) cts.Cancel(); await io.Delay(100, ct); } }
        public bool AnimPlaying(UUID id) => io.AnimPlaying(id); public bool AnimSeenSince(UUID id, DateTime t) => io.AnimSeenSince(id, t);
        public void AnimStart(UUID id) => io.AnimStart(id); public void AnimStop(UUID id) => io.AnimStop(id);
        public string Sound(UUID id) => io.Sound(id); public string Chat(int c, string t) => io.Chat(c, t); public void Log(string m) => io.Log(m);
    }
    [Fact] public async Task Run_SkippedChatAndSound_AreCountedAndLogged_NotErrors()
    {
        var io = new FakeIo { ChatSkip = "refused: quiet mode ON", SoundSkip = "quiet mode ON" };
        var r = await GestureRun("/t", Def(Say("hi"), new GStep(GStepKind.Sound, "s", Snd), Say("   ")), io, new GestureLimits(), default, sounds: true, chat: true);
        Assert.True(r.Ok); Assert.Equal(3, r.Skipped); Assert.Equal(0, r.Chats);
        Assert.Contains(io.Out, x => x.Contains("chat 'hi' skipped: refused: quiet mode ON"));
    }
    [Fact] public async Task Run_AStopStep_AndARestartOfAPlayingAnimation()
    {
        var io = new FakeIo(); io.Length[Clap] = double.PositiveInfinity;
        var r = await GestureRun("/t", Def(A(Clap), WaitSec(1), A(Clap), WaitSec(1), A(Clap, stop: true)), io, new GestureLimits(), default);
        Assert.True(r.Ok); Assert.Equal(new[] { "start 9b0c", "stop 9b0c", "start 9b0c", "stop 9b0c" }, io.Out.Where(x => !x.StartsWith("log")).ToArray());
        Assert.Contains(io.Out, x => x.Contains("restarted")); Assert.Empty(r.StoppedAtEnd);
    }

    // ---- silent mode (David t3214u, t3216u): by default only animation + wait steps; sound and chat are separate opt-ins
    static GestureDef WaveWithSound() => Def(A(Wave), Say("Hi!"), new GStep(GStepKind.Sound, "boing", Snd), WaitAnims());
    static string[] Acts(FakeIo io) => io.Out.Where(x => !x.StartsWith("log")).ToArray();
    [Fact] public async Task Default_OnlyAnimationsAndWaits_NoSoundPacket_NoChat()
    {
        var io = new FakeIo(); io.Length[Wave] = 1;
        var r = await GestureRun("/wave", WaveWithSound(), io, new GestureLimits(), default);
        Assert.True(r.Ok); Assert.Equal(new[] { "start c541" }, Acts(io));   // io.Sound (SoundTrigger) and io.Chat never called
        Assert.Empty(io.Said); Assert.Equal(0, r.Chats); Assert.Equal(2, r.Skipped); Assert.Equal(4, r.StepsRun);
        Assert.InRange(r.Seconds, 1.1, 1.5);   // the wait-for-animations step still waited for the wave
        Assert.Contains(io.Out, x => x.Contains("skipped: silent mode")); Assert.Contains(io.Out, x => x.Contains("skipped: chat off"));
    }
    [Fact] public async Task SoundTrue_SendsExactlyOneSound_ChatStillOff()
    {
        var io = new FakeIo(); io.Length[Wave] = 1;
        var r = await GestureRun("/wave", WaveWithSound(), io, new GestureLimits(), default, sounds: true);
        Assert.True(r.Ok); Assert.Equal(new[] { "start c541", "sound" }, Acts(io)); Assert.Empty(io.Said); Assert.Equal(1, r.Skipped);
    }
    [Fact] public async Task ChatTrue_SaysTheLine_SoundStillOff()
    {
        var io = new FakeIo(); io.Length[Wave] = 1;
        var r = await GestureRun("/wave", WaveWithSound(), io, new GestureLimits(), default, chat: true);
        Assert.True(r.Ok); Assert.Equal(new[] { "start c541", "chat Hi!" }, Acts(io)); Assert.Equal(1, r.Chats); Assert.Equal(1, r.Skipped);
    }
    [Fact] public async Task BothOn_EverythingPlays()
    {
        var io = new FakeIo(); io.Length[Wave] = 1;
        var r = await GestureRun("/wave", WaveWithSound(), io, new GestureLimits(), default, sounds: true, chat: true);
        Assert.Equal(new[] { "start c541", "chat Hi!", "sound" }, Acts(io)); Assert.Equal(0, r.Skipped);
    }
    [Fact] public void Marks_SoundAndChatSteps()
    {
        Assert.Equal(" +sound +chat", GestureMarks(WaveWithSound())); Assert.Equal("", GestureMarks(Def(A(Wave), WaitSec(1))));
        Assert.Equal(" +chat", GestureMarks(Def(Say("hey")))); Assert.Equal("", GestureMarks(Def(Say("  ")))); Assert.Equal("", GestureMarks(null));
        Assert.Equal(" +sound +chat", GestureMarks(GestureParse(WaveAsset).def));
    }
    [Fact] public void PlayArgs_SwitchesAndDefaults()
    {
        Assert.Equal((false, false, "/wave"), GesturePlayArgs("/wave", false, false));
        Assert.Equal((true, false, "/wave"), GesturePlayArgs("--sound /wave", false, false));
        Assert.Equal((true, true, "Female - Hey"), GesturePlayArgs("--chat --sound Female - Hey", false, false));
        Assert.Equal((false, false, "/wave"), GesturePlayArgs("--no-sound --no-chat /wave", true, true));
        Assert.Equal((true, true, "/wave"), GesturePlayArgs("/wave", true, true));   // GT_GESTURE_SOUND / GT_GESTURE_CHAT = on
        Assert.True(GestureEnvOn("on")); Assert.True(GestureEnvOn("1")); Assert.False(GestureEnvOn("off")); Assert.False(GestureEnvOn(null));
    }
    [Fact] public void ChatTrigger_ChatOff_TheTriggerAndItsReplaceTextAreNeverSaid()
    {
        Assert.Equal(("to David", 1), GestureTriggerRewrite("/bow to David", Active, sayReplace: false));
        Assert.Equal(("", 0), GestureTriggerRewrite("/wave", Active, sayReplace: false));
        Assert.Equal(("bows deeply to David", 1), GestureTriggerRewrite("/bow to David", Active, sayReplace: true));
    }
    [Fact] public void SoundAndChatAreOptIn_InTheToolTheCommandAndChatTriggers()
    {
        var m = File.ReadAllText(Path.Combine(Root(), "textclient/galatay-mcp/McpProgram.cs"));
        Assert.Contains("bool? sound = null", m); Assert.Contains("bool? chat = null", m); Assert.Contains("$\"gesture play {GestureFlags(sound, chat)}{n}\"", m);
        var g = File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text/Gestures.cs"));
        Assert.Contains("CancellationToken ct, bool sounds = false, bool chat = false)", g);          // the player's default: animations + waits only
        Assert.Contains("Env(\"GT_GESTURE_SOUND\", \"off\")", g); Assert.Contains("Env(\"GT_GESTURE_CHAT\", \"off\")", g);
        Assert.Contains("GestureTriggerRewrite(text, act, sayReplace: chat)", g);                       // chat off: nothing of the gesture is said
        Assert.Contains("GesturePlayStart(act[hit].trigger, def, \"chat trigger\", sound, chat)", g);
    }
    // ---- AO / idle: a gesture's animation is not the AO's, and not a posture change
    [Fact] public void GestureAnims_AreOwnedDuringPlayAndAShortGrace()
    {
        var now = new DateTime(2026, 11, 9, 17, 0, 0);
        var owned = new Dictionary<UUID, DateTime> { [Wave] = DateTime.MaxValue, [Clap] = now.AddSeconds(-1) };
        Assert.True(GestureAnimOwned(Wave, now, owned)); Assert.False(GestureAnimOwned(Clap, now, owned)); Assert.False(GestureAnimOwned(Snd, now, owned));
        Assert.Equal(new[] { Clap, Snd }, WithoutGestureAnims(new[] { Wave, Clap, Snd }, now, owned));
    }
    [Fact] public void TheAoAndIdleCodeLeaveGestureAnimationsAlone()
    {
        string Src(string f) => File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text", f));
        var aw = Src("AttachWatch.cs");
        int own = aw.IndexOf("if (GestureAnimOwnedNow(kv.Key)) continue;"), ao = aw.IndexOf("AoNoteStarted(kv.Key"), seatOff = aw.IndexOf("NoteSeatOffAnim(kv.Key, kv.Value.Item2); // learn");
        Assert.True(own > 0 && own < ao && own < seatOff, "a gesture animation must skip the AO stand/sit learning and the seat-off learning");
        Assert.Contains("HoverPostureNote(WithoutGestureAnims(now.Keys", aw);
        var ip = Src("IdlePose.cs");
        Assert.Contains("HoverPostureNote(WithoutGestureAnims(SelfAnims()", ip);
        Assert.Contains("dancing || GesturePlayingNow()) IdleNoteActivity();", ip);   // no idle sit/kneel on top of a gesture
        var g = Src("Gestures.cs"); var play = g[g.IndexOf("static async Task<string> GesturePlay(")..];
        Assert.True(play.IndexOf("IdlePoseExit(") < play.IndexOf("GestureRun("), "an idle pose is cleared before the gesture plays");
        Assert.Contains("AoGroundSitEnsureOff(\"after gesture", play);   // she ends standing
        Assert.DoesNotContain("PlayGestureAsync", g.Replace("AgentManager.PlayGestureAsync", ""));   // our own player
    }
    [Fact] public void TheConnectorHasTheGestureTool_AndTheClientTheCommand()
    {
        var m = File.ReadAllText(Path.Combine(Root(), "textclient/galatay-mcp/McpProgram.cs"));
        Assert.Contains("[McpServerTool(Name = \"gesture\")", m);
        Assert.Contains("Gestures.cs", File.ReadAllText(Path.Combine(Root(), "textclient/galatay-mcp/GalatayMcp.csproj")));
        var p = File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text/Program.cs"));
        Assert.Contains("case \"gesture\": return await GestureCmd(a, rest);", p);
        Assert.Contains("GestureChatTrigger(rest)", p);
    }
    static string Root() { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
}
