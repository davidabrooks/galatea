using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreMetaverse;
using GalatayText;
using Xunit;
namespace GalatayText.Tests;

public class LapAutoTests
{
    static string Src(string f) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../", f));

    [Theory]
    [InlineData("sit on my lap")]
    [InlineData("come sit on my lap babe")]
    [InlineData("on my lap")]
    [InlineData("Come here and sit on my lap please")]
    [InlineData("hop on my lap")]
    [InlineData("sit in my lap, love")]
    [InlineData("come sit on me")]
    public void Lap_requests(string t) => Assert.True(Program.IsLapRequest(t));

    [Theory]
    [InlineData("can you hear me babe")]
    [InlineData("get off my lap")]
    [InlineData("don't sit on my lap")]
    [InlineData("did you like sitting on my lap?")]
    [InlineData("come sit next to me")]
    [InlineData("")]
    public void Not_lap_requests(string t) => Assert.False(Program.IsLapRequest(t));

    [Fact]
    public void Lap_request_runs_before_the_come_approach_and_marks_the_webhook()
    {
        var p = Src("galatay-text/Program.cs");
        Assert.True(p.IndexOf("bool lapReq = LapRequestIn(e.SourceID") < p.IndexOf("Notify(\"local_chat\""));
        Assert.Contains("if (!lapReq) WanderChatIn(", p);
        Assert.Contains("if (!lapReq && ImAutoReact(im)) WanderChatIn(", p);
        Assert.Contains("SitLapCmd(\"David Nightingale\")", Src("galatay-text/LapAuto.cs"));
        Assert.Contains("WithAutoActions(batch, Core.AutoActionFor)", Src("galatay-mcp/Webhook.cs"));
    }

    [Fact]
    public void Auto_action_field_is_added_to_matching_lines()
    {
        var a = new GalatayMcp.Webhook.Ev("local_chat", "David Nightingale", "x", "sit on my lap", "t", 2) { msg_id = 7 };
        var b = new GalatayMcp.Webhook.Ev("local_chat", "David Nightingale", "x", "hi", "t", 2) { msg_id = 8 };
        var r = GalatayMcp.Webhook.WithAutoActions(new() { a, b }, id => id == 7 ? "sit_lap" : null);
        Assert.Equal("sit_lap", r[0].auto_action); Assert.Null(r[1].auto_action);
    }
}

public class HandBonesTests
{
    // minimal binary BVH: header + joints (name, priority, rot keys, pos keys) + 0 constraints
    static byte[] Anim(int prio, params (string name, int prio, int rot)[] joints)
    {
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        w.Write((ushort)1); w.Write((ushort)0); w.Write(prio); w.Write(1f); w.Write((byte)0);
        w.Write(0f); w.Write(1f); w.Write(1); w.Write(0.2f); w.Write(0.2f); w.Write(1u); w.Write((uint)joints.Length);
        foreach (var j in joints)
        {
            w.Write(Encoding.ASCII.GetBytes(j.name)); w.Write((byte)0); w.Write(j.prio); w.Write(j.rot);
            for (int k = 0; k < j.rot; k++) { w.Write((ushort)0); w.Write((ushort)32767); w.Write((ushort)32767); w.Write((ushort)32767); }
            w.Write(0);
        }
        w.Write(0); return ms.ToArray();
    }

    [Fact]
    public void Finger_bones_and_priority_are_read_from_the_asset()
    {
        var seat = new BinBVHAnimationReader(Anim(3, ("mPelvis", 3, 2), ("mChest", 3, 1)));
        Assert.Equal((false, -1), Program.FingerInfo(seat));
        var hands = new BinBVHAnimationReader(Anim(4, ("mHandIndex1Left", 4, 1), ("mHandThumb2Right", 5, 1), ("mHandMiddle1Left", 6, 0)));
        Assert.Equal((true, 5), Program.FingerInfo(hands));   // a joint without keys does not count
    }

    static Program.AnimFingers A(bool known, bool f, int p, bool seat) => new(UUID.Random(), known, f, p, seat);

    [Fact]
    public void Verdicts()
    {
        Assert.Equal("undriven", Program.HandsVerdict(new[] { A(true, false, -1, true), A(true, false, -1, false) }));
        Assert.Equal("ok", Program.HandsVerdict(new[] { A(true, false, -1, true), A(true, true, 4, false) }));
        Assert.Equal("seat-overrides", Program.HandsVerdict(new[] { A(true, true, 5, true), A(true, true, 4, false) }));
        Assert.Equal("ok", Program.HandsVerdict(new[] { A(true, true, 4, true) }));   // the pose itself shapes the hands
        Assert.Equal("unknown", Program.HandsVerdict(new[] { A(false, false, -1, true), A(true, false, -1, false) }));
        Assert.Equal("ok", Program.HandsVerdict(new[] { A(false, false, -1, true), A(true, true, 4, false) }));
    }

    [Fact]
    public void Wired_after_every_pose_hand_set()
    {
        Assert.Contains("HandsVerifyAfterPose(why)", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../galatay-text/HandPose.cs")));
    }
}

public class HandsLogOnlyTests
{
    static string Src(string f) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../galatay-text", f));
    [Fact]
    public void Hands_are_changed_only_for_flagged_broken_poses()
    {
        Assert.True(Program.HandFixFlagged("Mirage bed: Reading* > magazine 2", new[] { "Reading*", "magazine 2" }));
        Assert.False(Program.HandFixFlagged("Nutmeg. Porch Rocking Chair: phone", new[] { "phone" }));
        Assert.False(Program.HandFixFlagged("Mirage bed: Sleep", new[] { "Sleep" }));
        Assert.DoesNotContain("SetHandPose(", Src("HandBones.cs"));
        Assert.DoesNotContain("SetHandPose(", Src("SeatLinger.cs"));
    }
}
