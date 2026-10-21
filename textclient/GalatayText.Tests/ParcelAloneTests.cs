using System;
using System.Collections.Generic;
using System.Linq;
using GalatayText;
using LibreMetaverse;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-16 David: "no one else in our parcel.. if neighbors want to spy on us we will let them". The alone check for
/// adult poses counts only avatars on our own parcel (a 64x64 bitmap of 4 m cells), not the region and not neighbour regions.</summary>
public class ParcelAloneTests
{
    static readonly UUID David = new("44ce5a36-c1c7-4a68-ac9a-635ddfff6233");

    // bitmap with cells x0..x1, y0..y1 (inclusive, 4 m cells) set, layout index = y*64+x, bit = index%8 of byte index/8
    static byte[] Parcel(int x0, int y0, int x1, int y1)
    {
        var bm = new byte[512];
        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) { int i = y * 64 + x; bm[i >> 3] |= (byte)(1 << (i % 8)); }
        return bm;
    }
    // our parcel: x 128..191 m, y 0..99 m (cells 32..47, 0..24)
    static readonly byte[] Ours = Parcel(32, 0, 47, 24);
    static ParcelAvatar At(UUID id, float x, float y, float slack = 0) => new(id, new Vector3(x, y, 25), slack);
    static AloneVerdict Check(IEnumerable<ParcelAvatar> others, byte[] bm = null, bool android = false, string region = "Peronaut", string why = "no data") =>
        Program.AloneCheck(android, region, others, David, bm, "Peronaut Home", 7, why);

    [Fact]
    public void Bitmap_cells_use_the_viewer_layout()
    {
        Assert.True(Program.ParcelOn(Ours, 128f, 0f));
        Assert.True(Program.ParcelOn(Ours, 191.9f, 99.9f));
        Assert.False(Program.ParcelOn(Ours, 127.9f, 10f));     // cell 31
        Assert.False(Program.ParcelOn(Ours, 192f, 10f));       // cell 48
        Assert.False(Program.ParcelOn(Ours, 150f, 100f));      // cell row 25
        Assert.False(Program.ParcelOn(Ours, -1f, 10f));        // west neighbour region
        Assert.False(Program.ParcelOn(Ours, 300f, 10f));       // east neighbour region
        Assert.False(Program.ParcelOn(new byte[512], 150f, 10f));
        Assert.False(Program.ParcelOn(null, 150f, 10f));
        // the bit order is the one LibreMetaverse reads: cell (x=9,y=1) = index 73 = byte 9 bit 1
        var one = new byte[512]; one[9] = 0b10;
        Assert.True(Program.ParcelCellOn(one, 9, 1)); Assert.False(Program.ParcelCellOn(one, 8, 1)); Assert.False(Program.ParcelCellOn(one, 9, 0));
    }

    [Fact]
    public void Map_positions_are_checked_over_the_metre_they_can_be_in()
    {
        // region map x 127 means 127.0 .. 128.0: the far edge is on our parcel (cell 32 starts at 128)
        Assert.False(Program.ParcelOn(Ours, 127f, 10f, 0f));
        Assert.True(Program.ParcelOn(Ours, 127f, 10f, 1f));
        Assert.False(Program.ParcelOn(Ours, 126f, 10f, 1f));
        Assert.True(Program.ParcelOn(Ours, 191f, 10f, 1f));
        Assert.False(Program.ParcelOn(Ours, 255f, 10f, 1f)); // region edge does not wrap
    }

    [Fact]
    public void Only_David_on_the_parcel_is_alone_even_with_strangers_elsewhere_in_the_region()
    {
        var a = UUID.Random(); var b = UUID.Random(); var c = UUID.Random();
        var v = Check(new[] { At(David, 150, 20), At(a, 40, 200), At(b, 210, 30), At(c, 150, 180, 1) }, Ours);   // the 4 others from 2026-10-16: far away / off the parcel
        Assert.True(v.Allowed); Assert.False(v.Fallback);
        Assert.Equal(new[] { David }, v.OnParcel); Assert.Equal(3, v.Elsewhere.Count);
        Assert.Contains("parcel 'Peronaut Home' (local id 7)", v.Line);
        Assert.Contains("others on parcel: none (3 elsewhere in region ignored); David on our parcel", v.Line);
        Assert.Contains("alone with David", v.Line);
    }

    [Fact]
    public void A_stranger_on_the_parcel_blocks()
    {
        var a = UUID.Random();
        var v = Check(new[] { At(David, 150, 20), At(a, 160, 50) }, Ours);
        Assert.False(v.Allowed); Assert.Contains(a, v.OnParcel); Assert.Contains("not alone", v.Line);
        // one metre inside the edge from the region map still counts
        Assert.False(Check(new[] { At(David, 150, 20), At(a, 127, 50, 1) }, Ours).Allowed);
    }

    [Fact]
    public void David_must_be_on_the_parcel_and_an_unknown_position_counts_as_on_it()
    {
        var a = UUID.Random();
        Assert.False(Check(new[] { At(David, 40, 200) }, Ours).Allowed);                 // David elsewhere in the region: not "with David"
        Assert.False(Check(new ParcelAvatar[0], Ours).Allowed);                          // nobody at all
        Assert.False(Check(new[] { At(David, 150, 20), new ParcelAvatar(a, null, 0) }, Ours).Allowed);   // position unknown: strict
        Assert.True(Check(new[] { new ParcelAvatar(David, null, 0) }, Ours).Allowed);
    }

    [Fact]
    public void Android_outfit_and_other_regions_never_allow_it()
    {
        Assert.False(Check(new[] { At(David, 150, 20) }, Ours, android: true).Allowed);
        Assert.False(Check(new[] { At(David, 150, 20) }, Ours, region: "Terric").Allowed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Missing_or_empty_parcel_data_falls_back_to_the_strict_region_wide_count(bool emptyBitmap)
    {
        var far = UUID.Random();
        var bm = emptyBitmap ? new byte[512] : null;
        var v = Check(new[] { At(David, 150, 20), At(far, 40, 200) }, bm, why: "no parcel data covers my position yet (requested)");
        Assert.False(v.Allowed); Assert.True(v.Fallback);                                  // the stranger is far away, but with no parcel data everyone counts
        Assert.Contains("no parcel data (no parcel data covers my position yet (requested))", v.Line);
        Assert.Contains("strict region-wide", v.Line);
        // old behaviour otherwise: only David in the region is fine, nobody is not
        Assert.True(Check(new[] { At(David, 150, 20) }, bm).Allowed);
        Assert.False(Check(new ParcelAvatar[0], bm).Allowed);
        Assert.True(Check(new[] { At(David, 150, 20) }, bm).Fallback);
    }

    [Fact]
    public void Neighbour_region_avatars_are_not_in_the_current_region_list_and_positions_outside_it_are_off_the_parcel()
    {
        var n = UUID.Random();
        // coordinates in the current region's frame beyond 0..256 (a neighbour's avatar) are never on the parcel
        var v = Check(new[] { At(David, 150, 20), At(n, 300, 20), At(UUID.Random(), -20, 20) }, Ours);
        Assert.True(v.Allowed); Assert.Equal(2, v.Elsewhere.Count);
    }

    [Fact]
    public void Presence_baseline_then_enter_then_leave_after_the_grace()
    {
        var a = UUID.Random(); var b = UUID.Random(); var t = new DateTime(2026, 10, 16, 15, 0, 0);
        var p = new ParcelPresence { GraceS = 4 };
        var r0 = p.Update(t, new HashSet<UUID> { a });
        Assert.True(r0.baseline); Assert.Empty(r0.entered);                              // already there when the data arrived: no "enter"
        var r1 = p.Update(t.AddSeconds(1), new HashSet<UUID> { a, b });
        Assert.Equal(new[] { b }, r1.entered); Assert.Empty(r1.left);
        var r2 = p.Update(t.AddSeconds(2), new HashSet<UUID> { a, b });
        Assert.Empty(r2.entered);                                                        // no repeat
        var r3 = p.Update(t.AddSeconds(3), new HashSet<UUID> { a });                     // b flickers off the parcel edge
        Assert.Empty(r3.left);
        var r4 = p.Update(t.AddSeconds(4), new HashSet<UUID> { a, b });
        Assert.Empty(r4.entered); Assert.Empty(r4.left);                                 // back before the grace: not a leave + enter
        p.Update(t.AddSeconds(5), new HashSet<UUID> { a });
        var r6 = p.Update(t.AddSeconds(9.5), new HashSet<UUID> { a });
        Assert.Equal(new[] { b }, r6.left);
        var r7 = p.Update(t.AddSeconds(10), new HashSet<UUID> { a, b });
        Assert.Equal(new[] { b }, r7.entered);                                           // enters again later
        p.Reset();
        Assert.True(p.Update(t.AddSeconds(11), new HashSet<UUID> { a, b }).baseline);
    }

    [Fact]
    public void Entering_while_a_sexual_pose_runs_switches_the_pose()
    {
        Assert.Equal("switch", Program.PoseIntruderDecision(true, true, false, false, 999).action);
        Assert.Equal("none", Program.PoseIntruderDecision(false, true, false, false, 999).action);   // standing
        Assert.Equal("none", Program.PoseIntruderDecision(true, false, false, false, 999).action);   // PG pose
        Assert.Equal("none", Program.PoseIntruderDecision(true, true, true, false, 999).action);     // David on the seat chooses
        Assert.Equal("none", Program.PoseIntruderDecision(true, true, false, true, 999).action);     // still alone
        Assert.Equal("none", Program.PoseIntruderDecision(true, true, false, false, 3).action);      // debounce
        Assert.True(Program.PosePathIsSexual(new[] { "Fist*", "fist & balls" }));                    // what the reaction looks at
        Assert.False(Program.PosePathIsSexual(new[] { "SOLO F*", "laptop" }));
    }
}
