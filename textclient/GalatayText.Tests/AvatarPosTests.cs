using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-11-11 16:07: both in Ahern after David's lure; follow walked west into Dore
// toward a stale per-sim entry (Dore-local 235.4,-0.9) instead of his fresh Ahern
// position. Nearby also listed the same avatar twice. The tracker must pick the
// freshest source, drop other-sim leftovers, expire coarse, and prefer a current
// sim ObjectUpdate.
public class AvatarPosTests
{
    static readonly ulong Ahern = SimGeo.Handle(1001u * 256, 1001u * 256);
    static readonly ulong Dore = SimGeo.Handle(1000u * 256, 1001u * 256);
    static readonly UUID David = new("44ce5a36-c1c7-4a68-ac9a-635ddfff6233");
    static readonly DateTime T = new(2026, 11, 11, 16, 7, 25);

    static Program.AvPosSighting S(ulong handle, Vector3 local, DateTime at, Program.AvPosSource src) =>
        new(David, handle, local, at, src);

    [Fact]
    public void Freshest_current_sim_object_update_wins_over_stale_neighbor_entry()
    {
        // 16:07:25: stale Dore leftover (~15 m west of Ahern 7,11) vs a fresh
        // Ahern ObjectUpdate. Follow must use Ahern, not walk into Dore.
        var staleDore = S(Dore, new Vector3(235.4f, -0.9f, 40f), T.AddSeconds(-30), Program.AvPosSource.ObjectUpdate);
        var freshAhern = S(Ahern, new Vector3(9f, 9f, 41f), T, Program.AvPosSource.ObjectUpdate);
        var entries = new[] { staleDore, freshAhern };

        var dropped = Program.DropOlderOtherSimEntries(entries, Ahern, T, Program.AvCoarseExpireS);
        Assert.Single(dropped);
        Assert.Equal(Ahern, dropped[0].Handle);
        Assert.Equal(freshAhern.LocalPos, dropped[0].LocalPos);

        var pick = Program.PickAvatarPos(entries, Ahern, T, Program.AvCoarseExpireS);
        Assert.NotNull(pick);
        Assert.Equal(Ahern, pick.Value.Handle);
        Assert.Equal(Program.AvPosSource.ObjectUpdate, pick.Value.Source);
        Assert.Equal(9f, pick.Value.LocalPos.X, 1);
        Assert.Equal(9f, pick.Value.LocalPos.Y, 1);
    }

    [Fact]
    public void Current_sim_object_update_is_preferred_over_a_neighbor_coarse()
    {
        var ahernObj = S(Ahern, new Vector3(7f, 11f, 40.2f), T.AddSeconds(-0.5), Program.AvPosSource.ObjectUpdate);
        var doreCoarse = S(Dore, new Vector3(235.4f, -0.9f, 40f), T, Program.AvPosSource.Coarse);
        var pick = Program.PickAvatarPos(new[] { doreCoarse, ahernObj }, Ahern, T, Program.AvCoarseExpireS);
        Assert.NotNull(pick);
        Assert.Equal(Ahern, pick.Value.Handle);
        Assert.Equal(Program.AvPosSource.ObjectUpdate, pick.Value.Source);
        Assert.Equal(7f, pick.Value.LocalPos.X, 1);
    }

    [Fact]
    public void Fresh_current_sim_coarse_beats_stale_neighbor_object()
    {
        // He is not in Ahern's object cache yet, but the current region's coarse
        // is newer than the leftover Dore object — use the coarse, not the ghost.
        var staleDore = S(Dore, new Vector3(235.4f, -0.9f, 40f), T.AddSeconds(-20), Program.AvPosSource.ObjectUpdate);
        var ahernCoarse = S(Ahern, new Vector3(9f, 9f, 41f), T, Program.AvPosSource.Coarse);
        var pick = Program.PickAvatarPos(new[] { staleDore, ahernCoarse }, Ahern, T, Program.AvCoarseExpireS);
        Assert.NotNull(pick);
        Assert.Equal(Ahern, pick.Value.Handle);
        Assert.Equal(Program.AvPosSource.Coarse, pick.Value.Source);
        Assert.Equal(9f, pick.Value.LocalPos.X, 1);
    }

    [Fact]
    public void Expired_coarse_is_ignored()
    {
        var oldCoarse = S(Ahern, new Vector3(9f, 9f, 41f), T.AddSeconds(-10), Program.AvPosSource.Coarse);
        var doreObj = S(Dore, new Vector3(250f, 25f, 40f), T.AddSeconds(-2), Program.AvPosSource.ObjectUpdate);
        Assert.True(Program.AvCoarseExpired(oldCoarse, T, Program.AvCoarseExpireS));
        var pick = Program.PickAvatarPos(new[] { oldCoarse, doreObj }, Ahern, T, Program.AvCoarseExpireS);
        Assert.NotNull(pick);
        Assert.Equal(Dore, pick.Value.Handle);
        Assert.Equal(Program.AvPosSource.ObjectUpdate, pick.Value.Source);
        Assert.Null(Program.PickAvatarPos(new[] { oldCoarse }, Ahern, T, Program.AvCoarseExpireS));
    }

    [Fact]
    public void Newer_update_in_a_neighbor_drops_the_old_current_sim_entry()
    {
        // He just walked from Ahern into Dore: the newer Dore object must replace Ahern.
        var ahern = S(Ahern, new Vector3(2f, 24f, 40f), T.AddSeconds(-5), Program.AvPosSource.ObjectUpdate);
        var dore = S(Dore, new Vector3(250f, 24f, 40f), T, Program.AvPosSource.ObjectUpdate);
        var dropped = Program.DropOlderOtherSimEntries(new[] { ahern, dore }, Ahern, T, Program.AvCoarseExpireS);
        Assert.Single(dropped);
        Assert.Equal(Dore, dropped[0].Handle);
        var pick = Program.PickAvatarPos(new[] { ahern, dore }, Ahern, T, Program.AvCoarseExpireS);
        Assert.NotNull(pick);
        Assert.Equal(Dore, pick.Value.Handle);
    }
}
