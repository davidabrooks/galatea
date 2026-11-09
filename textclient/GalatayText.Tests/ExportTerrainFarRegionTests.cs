using LibreMetaverse;
using Xunit;
using GalatayText;

// 2026-11-09 14:21 PT: a look 12 s after a teleport Peronaut -> Ahern -> Morris merged the terrain of the old Peronaut-area regions
// (still connected, ~136 regions away) with Morris: an 8897 x 2753 grid, a 464 MB scene.json and the client at 8.9 GB RSS.
public class ExportTerrainFarRegionTests
{
    const int N = 65; // 256 / 4 + 1

    static float?[,] Flat(float z) { var h = new float?[N, N]; for (int j = 0; j < N; j++) for (int i = 0; i < N; i++) h[j, i] = z; return h; }

    [Theory]
    [InlineData(256, 0, true)] [InlineData(-256, 0, true)] [InlineData(0, 256, true)] [InlineData(256, -256, true)]
    [InlineData(0, 0, false)] [InlineData(512, 0, false)] [InlineData(34816, 10752, false)] [InlineData(-34816, -10496, false)]
    public void OnlyNeighboursAreAdjacent(float x, float y, bool adjacent) =>
        Assert.Equal(adjacent, Program.IsAdjacentOffset(new Vector3(x, y, 0)));

    [Fact]
    public void AStaleFarRegionDoesNotBlowUpTheTerrainGrid()
    {
        // Morris (her region), Ahern north of it, and the old Peronaut 136 x 42 regions away (cell offsets = metres / 4)
        var grids = new List<(int, int, float?[,])> { (0, 0, Flat(20)), (0, 64, Flat(21)), (34816 / 4, 10752 / 4, Flat(29)) };
        var (x0, y0, m) = Program.MergeTerrain(grids, N);
        Assert.Equal((0, 0), (x0, y0));
        Assert.Equal(N + 64, m.GetLength(0));   // Morris + Ahern only
        Assert.Equal(N, m.GetLength(1));
        Assert.True(m.Length < 3 * N * 3 * N);
    }

    [Fact]
    public void AFullRingOfNeighboursIsStillMerged()
    {
        var grids = new List<(int, int, float?[,])> { (0, 0, Flat(20)) };
        foreach (var dx in new[] { -64, 0, 64 }) foreach (var dy in new[] { -64, 0, 64 }) if (dx != 0 || dy != 0) grids.Add((dx, dy, Flat(10)));
        var (x0, y0, m) = Program.MergeTerrain(grids, N);
        Assert.Equal((-64, -64), (x0, y0));
        Assert.Equal(64 * 2 + N, m.GetLength(0));
        Assert.Equal(20f, m[64, 64]);           // her own region wins where they overlap
    }
}
