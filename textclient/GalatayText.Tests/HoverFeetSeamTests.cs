using GalatayText;
using LibreMetaverse;
using Xunit;

// The hover and feet code reach the rest of the client only through IHoverHost / IFeetHost (HoverHost.cs): a fake host is enough to run them offline.
public class HoverFeetSeamTests
{
    sealed class FakeFeetHost : IFeetHost
    {
        public string Dir;
        public GridClient Client => null;
        public Simulator Sim => null;
        public void Log(string kind, string msg) { }
        public string RouteDir => Dir;
        public List<Primitive> WornPrims() => new();
        public UUID AttachItemId(Primitive p) => UUID.Zero;
        public Task<string> AttachItem(UUID item, string pointArg) => throw new NotImplementedException();
        public Task<string> DetachItemAsync(UUID item, string why) => throw new NotImplementedException();
        public List<Primitive> LinkPrims(Primitive root) => new();
        public Task EnsureProperties(Simulator sim, List<Primitive> prims) => Task.CompletedTask;
        public Task<string> TouchAttachment(string rest) => throw new NotImplementedException();
        public List<UUID> SelfAnims() => new();
        public bool PoseBusy => false; public double PoseSettledSec => 99; public bool Sitting => false;
        public Task<string> FeetCloseupJson(double exposure) => Task.FromResult<string>(null);
    }

    [Fact] public async Task FeetCmd_ReadsItsRulesThroughTheHost_AndReportsWithoutTouchingProgram()
    {
        var dir = Path.Combine(Path.GetTempPath(), "feet-seam-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "_feet-rules.json"), "{\"default\":\"f2\",\"footwear\":[{\"item\":\"" + Guid.NewGuid() + "\",\"name\":\"Boots\",\"feet\":\"f3\"}]}");
            var r = await new FeetAuto(new FakeFeetHost { Dir = dir }).FeetCmd(new[] { "feet" });
            Assert.StartsWith("feet: wanted f2 (no listed heeled footwear); last pressed ", r);
            Assert.Contains("; 1 heeled item(s) listed", r);
        }
        finally { Directory.Delete(dir, true); }
    }
}
