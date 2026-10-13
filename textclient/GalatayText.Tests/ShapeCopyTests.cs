using GalatayText;
using LibreMetaverse;
using Xunit;

public class ShapeCopyTests
{
    const string Orig = "Galatea Petite shape - Jani short neck";
    static readonly HashSet<string> Extra = Program.ParseEditableShapes("# copies\nPetite - Android\n\n");

    [Fact] public void OriginalStillEditable() => Assert.Null(Program.ShapeEditRefusal(Orig, null, Extra, Orig));
    [Fact] public void ListedCopyEditable() => Assert.Null(Program.ShapeEditRefusal("Petite - Android", "petite - android", Extra, Orig));
    [Fact] public void OtherShapeRefused() => Assert.NotNull(Program.ShapeEditRefusal("SHAPE Default lel EvoX AVALON", null, Extra, Orig));
    [Fact] public void TargetMustMatchWorn() => Assert.Contains("not 'Petite - Android'", Program.ShapeEditRefusal(Orig, "Petite - Android", Extra, Orig));
    [Fact] public void CommentsIgnored() => Assert.DoesNotContain("# copies", Extra);

    [Fact] public void SplitTargetQuoted()
    {
        var (t, k) = Program.SplitShapeTarget("@\"Petite - Android\" Breast Size");
        Assert.Equal("Petite - Android", t); Assert.Equal("Breast Size", k);
    }
    [Fact] public void SplitNoTarget() { var (t, k) = Program.SplitShapeTarget("Breast Size"); Assert.Null(t); Assert.Equal("Breast Size", k); }

    [Fact] public void InvCopyArgs()
    {
        var (id, name, err) = Program.ParseInvCopyArgs("f3e52622-4a9c-649e-5bd5-e0bbeb9e9788 \"Petite - Android\"");
        Assert.Null(err); Assert.Equal(new UUID("f3e52622-4a9c-649e-5bd5-e0bbeb9e9788"), id); Assert.Equal("Petite - Android", name);
        Assert.NotNull(Program.ParseInvCopyArgs("f3e52622-4a9c-649e-5bd5-e0bbeb9e9788").err);
        Assert.NotNull(Program.ParseInvCopyArgs("nope name").err);
    }
}
