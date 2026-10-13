using System; using System.Collections.Generic; using LibreMetaverse; using Xunit; using GalatayText;
public class AnimOrderTests
{
    [Fact] public void TiesBrokenBySequenceNotUuid()
    {
        var t = new DateTime(2026, 10, 11, 19, 0, 0);
        var a = new UUID("ffffffff-0000-0000-0000-000000000001"); var b = new UUID("00000000-0000-0000-0000-000000000002");
        var m = new Dictionary<UUID, (int, DateTime)> { [a] = (5, t), [b] = (9, t) };
        var s = Program.AnimsOrdered(m, t.AddSeconds(3));
        Assert.StartsWith(a.ToString(), s);
        Assert.EndsWith(b + "@3.0", s);
    }
    [Fact] public void EarlierStartFirst()
    {
        var t = new DateTime(2026, 10, 11, 19, 0, 0);
        var a = new UUID("00000000-0000-0000-0000-000000000001"); var b = new UUID("00000000-0000-0000-0000-000000000002");
        var m = new Dictionary<UUID, (int, DateTime)> { [a] = (1, t.AddSeconds(5)), [b] = (99, t) };
        Assert.StartsWith(b.ToString(), Program.AnimsOrdered(m, t.AddSeconds(10)));
    }
}
