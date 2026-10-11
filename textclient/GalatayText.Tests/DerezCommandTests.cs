using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-11: take refused her own group-set TETRA box; add return/delete.</summary>
public class DerezCommandTests
{
    static readonly UUID Me = new("11111111-1111-1111-1111-111111111111");
    static readonly UUID Grp = new("22222222-2222-2222-2222-222222222222");
    static readonly UUID Other = new("33333333-3333-3333-3333-333333333333");

    [Fact] public void Zero_update_owner_but_properties_owner_me_is_ok() => Assert.Null(Program.DerezOwnerCheck(Me, UUID.Zero, Me, Grp, Me, Other));
    [Fact] public void Group_set_object_owned_by_me_is_ok() => Assert.Null(Program.DerezOwnerCheck(Me, Me, Me, Grp, null, null));
    [Fact] public void Group_deeded_object_I_rezzed_is_ok() => Assert.Null(Program.DerezOwnerCheck(Me, UUID.Zero, Grp, Grp, Me, Other));
    [Fact] public void Someone_elses_object_is_refused() => Assert.NotNull(Program.DerezOwnerCheck(Me, UUID.Zero, Other, Grp, Other, Other));
    [Fact] public void Group_deeded_by_someone_else_is_refused() => Assert.NotNull(Program.DerezOwnerCheck(Me, UUID.Zero, Grp, Grp, Other, Other));
    [Fact] public void Unknown_owner_is_refused() => Assert.NotNull(Program.DerezOwnerCheck(Me, UUID.Zero, null, null, null, null));
    [Theory]
    [InlineData("take", DeRezDestination.AgentInventoryTake)]
    [InlineData("return", DeRezDestination.ReturnToOwner)]
    [InlineData("delete", DeRezDestination.TrashFolder)]
    public void Destinations(string cmd, DeRezDestination d) => Assert.Equal(d, Program.DerezDest(cmd));
}
