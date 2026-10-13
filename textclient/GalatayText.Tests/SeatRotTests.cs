using System; using LibreMetaverse; using Xunit; using GalatayText;
public class SeatRotTests
{
    static readonly Quaternion Couch = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2);
    [Fact] public void UnseatedKeepsRotation() => Assert.Equal(Quaternion.Identity, Program.SeatWorldRot(_ => null, 0, Quaternion.Identity));
    [Fact] public void SeatedComposesSeatRotation()
    {
        var r = Program.SeatWorldRot(id => id == 7 ? (Couch, 0u) : null, 7, Quaternion.Identity);
        Assert.True(Math.Abs(Quaternion.Dot(r, Couch)) > 0.9999f);
    }
    [Fact] public void ChildSeatComposesUpToRoot()
    {
        var half = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 4);
        var r = Program.SeatWorldRot(id => id == 8 ? (half, 7u) : id == 7 ? (half, 0u) : null, 8, Quaternion.Identity);
        Assert.True(Math.Abs(Quaternion.Dot(r, Couch)) > 0.999f);
    }
}
