using GalatayText;
using LibreMetaverse;
using Xunit;

public class BodyFamilyTests
{
    static UUID U(int i) => new UUID($"00000000-0000-0000-0000-{i:D12}");
    [Fact] public void Families()
    {
        Assert.Equal("kay", Program.BodyFamily("MISAKI -Kay Body Perky"));
        Assert.Equal("kay", Program.BodyFamily("MISAKI -Kay 3.0 Skeleton"));
        Assert.Equal("larax", Program.BodyFamily("Maitreya Mesh Body - LaraX Petite Add-on V1.1"));
        Assert.Equal("larax", Program.BodyFamily("The V - Bento for Maitreya LaraX - SCULPTED - V1.61"));
        Assert.Null(Program.BodyFamily("TETRA - Spicy Bikini Top - LaraX Petite"));
        Assert.Null(Program.BodyFamily("CALISTA hair long"));
    }
    [Fact] public void NakedToAndroidDropsLaraX()
    {
        var worn = new List<Program.WornAtt> { new(U(1), "Maitreya Mesh Body - LaraX V1.1", false), new(U(2), "Maitreya Mesh Body - LaraX Petite Add-on V1.1", false), new(U(9), "/ HEAD / lel evox / AVALON 4.0", false) };
        var target = new List<Program.TargetObj> { new(U(3), "MISAKI -Kay Body V-Tech"), new(U(4), "MISAKI -Kay 3.0 Skeleton"), new(U(9), "/ HEAD / lel evox / AVALON 4.0") };
        var (d, a, _) = Program.PlanAttachmentSwap(worn, target, new HashSet<UUID>());
        Assert.Contains(U(1), d); Assert.Contains(U(2), d); Assert.DoesNotContain(U(9), d); Assert.Contains(U(3), a);
    }
    [Fact] public void AndroidToHumanDropsKay()
    {
        var worn = new List<Program.WornAtt> { new(U(3), "MISAKI -Kay Body Perky", false), new(U(5), "MISAKI -Kay HandL", false) };
        var target = new List<Program.TargetObj> { new(U(1), "Maitreya Mesh Body - LaraX V1.1") };
        var (d, _, _) = Program.PlanAttachmentSwap(worn, target, new HashSet<UUID>());
        Assert.Contains(U(3), d); Assert.Contains(U(5), d);
    }
    [Fact] public void VTechToPerkySwapsCover()
    {
        var worn = new List<Program.WornAtt> { new(U(3), "MISAKI -Kay Body V-Tech", false), new(U(4), "MISAKI -Kay 3.0 Skeleton", false) };
        var target = new List<Program.TargetObj> { new(U(6), "MISAKI -Kay Body Perky"), new(U(4), "MISAKI -Kay 3.0 Skeleton") };
        var (d, a, _) = Program.PlanAttachmentSwap(worn, target, new HashSet<UUID>());
        Assert.Contains(U(3), d); Assert.DoesNotContain(U(4), d); Assert.Contains(U(6), a);
    }
}
