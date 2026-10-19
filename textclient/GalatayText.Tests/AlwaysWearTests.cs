using System; using System.Collections.Generic; using System.Linq; using LibreMetaverse; using Xunit; using GalatayText;

/// 2026-10-19 (David): the N° 9 collar (routes/_always-wear.txt) is part of every outfit, Naked included.
public class AlwaysWearTests
{
    static readonly UUID Collar = new("11111111-2222-3333-4444-555555555555");
    static readonly UUID Body = new("aaaaaaaa-2222-3333-4444-555555555555");

    [Fact]
    public void NakedOutfit_StillGetsCollar_WhenNotWorn()
    {
        // the Naked outfit lists no collar; it is attached anyway
        Assert.Equal(new[] { Collar }, Program.AlwaysWearMissing(new[] { Body }, new[] { Collar }));
    }

    [Fact]
    public void Collar_AlreadyWorn_NothingToAdd()
    {
        Assert.Empty(Program.AlwaysWearMissing(new[] { Body, Collar }, new[] { Collar }));
    }

    [Fact]
    public void AlwaysWearFile_UsesKeepAttachedFormat()
    {
        var l = Program.ParseKeepAttached("# collar\n11111111-2222-3333-4444-555555555555 N9 collar\n");
        Assert.Single(l); Assert.Equal(Collar, l[0]);
    }
}
