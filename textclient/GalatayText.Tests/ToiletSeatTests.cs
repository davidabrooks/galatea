using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-08 David: toilet = female poses only, take off whatever is on her legs while using it.</summary>
public class ToiletSeatTests
{
    [Fact]
    public void Female_buttons_only()
    {
        var menu = new[] { "Pee (m)", "Aim (m)", "[ADJUST]", "Poop", "Hover", "Vomit", "Texture", "Sit" };
        Assert.Equal(new[] { "Poop", "Hover", "Sit" }, Program.ToiletFemaleButtons(menu));
        Assert.Empty(Program.ToiletFemaleButtons(new[] { "Pee(m)", "Stand (M)", "", "Male pee" }));
    }

    [Fact]
    public void Toilet_seat_menu_picks_a_female_pose()
    {
        var seat = new Program.HomeSeatInfo(UUID.Random(), "BackBone Playtime Toilet", Vector3.Zero, "upper", null, "toilet",
            new List<string>(), new List<string> { "Sit", "Hover", "Poop", "Pee (m)", "Vomit" }, UUID.Zero, true);
        var picks = Enumerable.Range(0, 60).Select(k => Program.HomeSeatMenu(seat, new Random(k))!.Single()).ToHashSet();
        Assert.Equal(new HashSet<string> { "Sit", "Hover", "Poop" }, picks);
    }

    [Theory]
    [InlineData("Maitreya LaraX Vintage Jeans Low-Waist #3 (Flat)", AttachmentPoint.Pelvis, true)]
    [InlineData("Spicy Bikini Panties", AttachmentPoint.Pelvis, true)]
    [InlineData("Some Mini Skirt", AttachmentPoint.Stomach, true)]
    [InlineData("Denim Shorts - LaraX", AttachmentPoint.Pelvis, true)]
    [InlineData("Brand Capris", AttachmentPoint.LeftUpperLeg, true)]
    [InlineData("ARTi'S Strapless Top", AttachmentPoint.Chest, false)]
    [InlineData("Spicy Bikini Top", AttachmentPoint.Chest, false)]
    [InlineData("Valentine Dress", AttachmentPoint.Pelvis, false)]
    [InlineData("Lace Heels", AttachmentPoint.LeftFoot, false)]
    [InlineData("Ankle Boots", AttachmentPoint.LeftLowerLeg, false)]
    [InlineData("The V - Bento by Session Skins & ASA Studios", AttachmentPoint.Stomach, false)]
    [InlineData("Maitreya Mesh Body - Lara X", AttachmentPoint.Pelvis, false)]
    [InlineData("[BB] Nipple Rings - LaraX Petite (Puffy)", AttachmentPoint.Chest, false)]
    public void Lower_body_attachments(string name, AttachmentPoint pt, bool off) => Assert.Equal(off, Program.ToiletLowerAttachment(name, pt));

    [Theory]
    [InlineData(WearableType.Alpha, "Bimbette /// Alpha Layer /// Legs", true)]
    [InlineData(WearableType.Alpha, "Bimbette /// Alpha Layer /// Upper Legs & Knees", true)]
    [InlineData(WearableType.Alpha, "Bimbette /// Alpha Layer /// Butt 1", true)]
    [InlineData(WearableType.Alpha, "Bimbette /// Alpha Layer /// Upper Torso", false)]
    [InlineData(WearableType.Alpha, "Alpha Feet", false)]
    [InlineData(WearableType.Alpha, "<Alpha mask> Chill Shorts - Maitreya", true)]
    [InlineData(WearableType.Alpha, "<Alpha mask> Chill T-Shirt - Maitreya +Petite", false)]
    [InlineData(WearableType.Pants, "anything", true)]
    [InlineData(WearableType.Skirt, "anything", true)]
    [InlineData(WearableType.Underpants, "anything", true)]
    [InlineData(WearableType.Shirt, "Legs shirt", false)]
    [InlineData(WearableType.Shoes, "shoe base", false)]
    public void Lower_body_layers(WearableType t, string name, bool off) => Assert.Equal(off, Program.ToiletLowerLayer(t, name));
}
