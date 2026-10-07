using System.Collections.Generic;
using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-07 'inv move' / 'inv mkdir' / 'inv move undo' rules (pure).</summary>
public class InvMoveTests
{
    static readonly UUID Root = UUID.Random(), Trash = UUID.Random(), Cof = UUID.Random(), MyOut = UUID.Random(), Clothing = UUID.Random();
    static List<(UUID, FolderType)> Anc(params (UUID, FolderType)[] a) => new(a);
    static Program.InvNodeInfo Folder(UUID id, string name, FolderType t, UUID parent, List<(UUID, FolderType)> anc) => new(id, name, true, t, parent, anc);
    static Program.InvNodeInfo Item(UUID id, UUID parent, List<(UUID, FolderType)> anc) => new(id, "x", false, FolderType.None, parent, anc);

    static readonly Program.InvNodeInfo ClothingF = Folder(Clothing, "Clothing", FolderType.Clothing, Root, Anc((Root, FolderType.Root)));
    static readonly Program.InvNodeInfo TrashF = Folder(Trash, "Trash", FolderType.Trash, Root, Anc((Root, FolderType.Root)));
    static readonly Program.InvNodeInfo MyOutF = Folder(MyOut, "My Outfits", FolderType.MyOutfits, Root, Anc((Root, FolderType.Root)));

    [Fact]
    public void Plain_folder_and_item_move_into_a_system_folder()
    {
        var f = Folder(UUID.Random(), "TETRA", FolderType.None, Root, Anc((Root, FolderType.Root)));
        Assert.Equal("move", Program.InvMoveVerdict(f, ClothingF));
        Assert.Equal("move", Program.InvMoveVerdict(Item(UUID.Random(), f.Id, Anc((f.Id, FolderType.None), (Root, FolderType.Root))), ClothingF));
        Assert.Equal("already there", Program.InvMoveVerdict(Item(UUID.Random(), Clothing, Anc((Clothing, FolderType.Clothing), (Root, FolderType.Root))), ClothingF));
    }

    [Fact]
    public void Refusals()
    {
        var plain = Folder(UUID.Random(), "box", FolderType.None, Root, Anc((Root, FolderType.Root)));
        Assert.StartsWith("refused: system folder", Program.InvMoveVerdict(ClothingF, plain));
        Assert.StartsWith("refused: destination is Trash", Program.InvMoveVerdict(plain, TrashF));
        Assert.StartsWith("refused: destination is Current Outfit / My Outfits", Program.InvMoveVerdict(plain, MyOutF));
        var link = Item(UUID.Random(), Cof, Anc((Cof, FolderType.CurrentOutfit), (Root, FolderType.Root)));
        Assert.StartsWith("refused: source is inside", Program.InvMoveVerdict(link, plain));
        var outfit = Folder(UUID.Random(), "Naked", FolderType.Outfit, MyOut, Anc((MyOut, FolderType.MyOutfits), (Root, FolderType.Root)));
        Assert.StartsWith("refused", Program.InvMoveVerdict(outfit, plain));
        var child = Folder(UUID.Random(), "sub", FolderType.None, plain.Id, Anc((plain.Id, FolderType.None), (Root, FolderType.Root)));
        Assert.StartsWith("refused: a folder cannot go into itself", Program.InvMoveVerdict(plain, child));
        Assert.StartsWith("refused: destination is not a folder", Program.InvMoveVerdict(plain, Item(UUID.Random(), Root, Anc((Root, FolderType.Root)))));
    }

    [Fact]
    public void Mkdir_reuses_an_existing_child_and_refuses_protected_parents()
    {
        var existing = UUID.Random();
        Assert.Equal($"exists {existing}", Program.InvMkdirVerdict(ClothingF, "shoes", new[] { (existing, "Shoes", true) }));
        Assert.Equal("create", Program.InvMkdirVerdict(ClothingF, "Shoes", new[] { (UUID.Random(), "Shoes", false) }));
        Assert.StartsWith("refused", Program.InvMkdirVerdict(TrashF, "x", new (UUID, string, bool)[0]));
        Assert.StartsWith("refused", Program.InvMkdirVerdict(ClothingF, " ", new (UUID, string, bool)[0]));
    }

    [Fact]
    public void Undo_takes_the_last_move_not_yet_undone()
    {
        UUID a = UUID.Random(), b = UUID.Random(), p1 = UUID.Random(), p2 = UUID.Random();
        var lines = new[]
        {
            $"t\tMOVE\t{a}\titem\t{p1}\t{p2}\tA",
            $"t\tMOVE\t{b}\tfolder\t{p1}\t{p2}\tB",
            $"t\tUNDO\t{b}\tfolder\t{p2}\t{p1}\tB",
        };
        var u = Program.LastUndoableMove(lines);
        Assert.NotNull(u);
        Assert.Equal(a, u.Value.id);
        Assert.Equal(p1, u.Value.from);
        Assert.False(u.Value.isFolder);
        Assert.Null(Program.LastUndoableMove(new[] { lines[1], lines[2] }));
    }
}
