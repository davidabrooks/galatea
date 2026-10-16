using GalatayText;
using Xunit;
using static GalatayText.Program;

namespace GalatayText.Tests;

/// <summary>2026-10-16 (David): conversation poses (Reiley Net Chair PG: SINGLE* > Talk) are for talking to him from the paired seat;
/// the solo picker always skips them unless he sits next to her.</summary>
public class ConversationPoseTests
{
    static readonly string[] Names = { "Talk", "TALK", "Talk 2", "Talking", "Chat", "Chatting", "Conversation", "Talk*" };

    [Fact]
    public void Default_rules_name_the_conversation_poses_and_nothing_plain()
    {
        foreach (var n in Names) Assert.True(PoseIsConversation(n), n);
        foreach (var n in new[] { "Sit", "Cross legs", "Relax", "Read", "Phone", "Laptop", "Stand preen", "Talent show", "Chair" }) Assert.False(PoseIsConversation(n), n);
    }

    [Fact]
    public void Solo_picker_never_picks_Talk_without_David_seated_next_to_her()
    {
        var rnd = new Random(1);
        for (int i = 0; i < 200; i++)
        {
            var (pick, _, _) = PickPoseButton(new() { "Talk", "Sit", "Cross legs", "Relax" }, null, rnd, preferPgSolo: true, inSubmenu: true);
            Assert.NotEqual("Talk", pick);
            var (p2, _, _) = PickPoseButton(new() { "Chatting", "Talk 2", "Read" }, null, rnd, inSubmenu: true);
            Assert.Equal("Read", p2);
        }
        Assert.Null(PickPoseButton(new() { "Talk" }, null, rnd, inSubmenu: true).pick);                     // nothing else: no pick
        Assert.Null(PickPoseButton(new() { "Talk*", "[BACK]" }, null, rnd, preferPgSolo: true).pick);          // a Talk submenu is skipped too
    }

    [Fact]
    public void Talk_is_allowed_again_only_when_David_sits_nearby()
    {
        var rnd = new Random(2); bool seen = false;
        for (int i = 0; i < 200; i++) seen |= PickPoseButton(new() { "Talk", "Sit" }, null, rnd, inSubmenu: true, conversationOk: true).pick == "Talk";
        Assert.True(seen);
        Assert.True(DavidSeatedNear(true, false, 99f));       // on my seat
        Assert.True(DavidSeatedNear(false, true, 2.0f));      // on the paired chair 2 m away
        Assert.False(DavidSeatedNear(false, true, 6f));       // sitting far away
        Assert.False(DavidSeatedNear(false, false, 1f));      // standing next to the chair is not seated
    }

    [Fact]
    public void Rules_data_can_replace_the_pattern_and_add_names()
    {
        var r = PoseRuleSet.FromJson("{\"extra_conversation_names\": [\"Natter\"]}");
        Assert.True(r.IsConversationName("Natter")); Assert.True(r.IsConversationName("Talk")); Assert.False(r.IsConversationName("Sit"));
        var r2 = PoseRuleSet.FromJson("{\"conversation_pose\": \"^gossip\"}");
        Assert.True(r2.IsConversationName("Gossip 3")); Assert.False(r2.IsConversationName("Talk"));
    }
}
