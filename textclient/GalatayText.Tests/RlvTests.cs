using System; using System.Collections.Generic; using System.Linq; using LibreMetaverse; using Xunit; using GalatayText;

/// 2026-10-19 (David): RLV phase 1 (undress + furniture) for the N° 9 collar. Rlv.cs.
public class RlvTests
{
    static readonly UUID Collar = new("11111111-2222-3333-4444-555555555555");
    static readonly UUID CollarPrim = new("11111111-2222-3333-4444-000000000001");
    static readonly UUID Top = new("aaaaaaaa-0000-0000-0000-000000000001");
    static readonly UUID Body = new("aaaaaaaa-0000-0000-0000-000000000002");
    static readonly UUID Hud = new("aaaaaaaa-0000-0000-0000-000000000003");

    [Fact]
    public void Parse_CommaSeparated_WithOptionsAndParams()
    {
        var c = Program.RlvParse("@detach=n,sit:12345678-1234-1234-1234-123456789012=force,versionnew=-1812221819,clear");
        Assert.Equal(4, c.Count);
        Assert.Equal(new Program.RlvCmd("detach", "", "n"), c[0]);
        Assert.Equal(new Program.RlvCmd("sit", "12345678-1234-1234-1234-123456789012", "force"), c[1]);
        Assert.Equal("versionnew", c[2].Behav); Assert.Equal("clear", c[3].Behav);
        Assert.Null(Program.RlvParse("hello"));
    }

    [Fact]
    public void Version_Replies_AreRlvaStyle()
    {
        Assert.StartsWith("RestrainedLove viewer v3.4.3 (RLVa", Program.RlvVersionReply("versionnew"));
        Assert.StartsWith("RestrainedLife viewer", Program.RlvVersionReply("version"));
        Assert.Equal("3040300", Program.RlvVersionReply("versionnum"));
        Assert.Equal("3040300", Program.RlvVersionReply("versionnumbl"));
        Assert.Null(Program.RlvVersionReply("getoutfit"));
    }

    [Fact]
    public void Store_PerSource_ClearAndDrop()
    {
        var s = new Program.RlvStore(); var other = UUID.Random();
        s.Add(CollarPrim, "collar", "unsit", ""); s.Add(CollarPrim, "collar", "detach", ""); s.Add(other, "x", "tplm", "");
        Assert.True(s.Has("unsit")); Assert.Equal("/detach/unsit", s.Status(CollarPrim, "", "/"));
        Assert.Equal(1, s.Clear(CollarPrim, "unsit")); Assert.False(s.Has("unsit"));
        s.Drop(other); Assert.False(s.Has("tplm"));
        Assert.Equal(1, s.ClearAll()); Assert.False(s.Has("detach"));
    }

    [Fact]
    public void Detach_NeverTakesCollar_Body_Hud_OrLocked()
    {
        var worn = new[] {
            new Program.RlvWorn(Collar, CollarPrim, "No 9 :: Premium Collar", 39),
            new Program.RlvWorn(Top, UUID.Random(), "Silk top", 1),
            new Program.RlvWorn(Body, UUID.Random(), "Maitreya Mesh Body - Lara", 10),
            new Program.RlvWorn(Hud, UUID.Random(), "Some HUD", 35) };
        var plan = Program.RlvDetachPlan(worn, "", new HashSet<UUID> { Collar }, new HashSet<UUID>());
        Assert.Equal(new[] { Top }, plan.Select(p => p.Item));
        // the collar is protected even when its own point is named, and a detach-locked prim stays on
        Assert.Empty(Program.RlvDetachPlan(worn, "neck", new HashSet<UUID> { Collar }, new HashSet<UUID>()));
        Assert.Empty(Program.RlvDetachPlan(worn, "chest", new HashSet<UUID>(), new HashSet<UUID> { worn[1].Prim }));
        Assert.Single(Program.RlvDetachPlan(worn, "chest", new HashSet<UUID>(), new HashSet<UUID>()));
    }

    [Fact]
    public void Remoutfit_NeverBodyParts()
    {
        var all = Program.RlvRemovableLayers("");
        Assert.DoesNotContain(WearableType.Shape, all); Assert.DoesNotContain(WearableType.Skin, all);
        Assert.Contains(WearableType.Alpha, all);
        Assert.Empty(Program.RlvRemovableLayers("skin"));
        Assert.Equal(new[] { WearableType.Underpants }, Program.RlvRemovableLayers("underpants"));
    }

    [Fact]
    public void Queries_OutfitAndAttachStrings()
    {
        var o = Program.RlvOutfitString(new HashSet<WearableType> { WearableType.Shape, WearableType.Alpha }, "");
        Assert.Equal(17, o.Length); Assert.Equal('1', o[12]); Assert.Equal('1', o[13]); Assert.Equal('0', o[0]);
        Assert.Equal("1", Program.RlvOutfitString(new HashSet<WearableType> { WearableType.Alpha }, "alpha"));
        Assert.Equal("1", Program.RlvAttachString(new HashSet<int> { 39 }, "neck"));
        Assert.Equal('1', Program.RlvAttachString(new HashSet<int> { 39 }, "")[39]);
    }

    [Fact]
    public void Safeword_AlwaysPasses_AndRestrictionsBlockOwnActions()
    {
        Func<string, bool> all = _ => true;
        Assert.Null(Program.RlvBlocks("say", "MAYDAY", false, all));
        Assert.Null(Program.RlvBlocks("say", "((MAYDAY))", false, all));
        Assert.NotNull(Program.RlvBlocks("say", "hello", false, all));
        Assert.NotNull(Program.RlvBlocks("stand", "", false, all));
        Assert.Null(Program.RlvBlocks("stand", "", true, all));   // the collar's own @unsit=force
        Assert.Null(Program.RlvBlocks("stand", "", false, _ => false));
        Assert.False(Program.IsSafeword("mayday please"));
    }
}
