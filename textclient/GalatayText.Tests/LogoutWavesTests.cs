using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-10-30 14:56:49 / 14:59:14 logouts (textclient.log "stash: 9 attachment(s) taken off"): all 9 detached in one burst, head before hair at 14:56:49,
// logout the same second; David saw hair and teeth floating after everything else vanished.
public class LogoutWavesTests
{
    static LoginVeil.Stashed S(string n, int p) => new(UUID.Random(), p, n);
    static List<LoginVeil.Stashed> Live1456() => new() {
        S("/ HEAD / lel evox / AVALON 4.0", 12), S("TETRA - Moca Ankle Boots (Suede) - Maitreya Mid", 7), S("TETRA - Chill T-Shirt - LaraX Petite", 55),
        S("TETRA - Geena ripped shorts - LaraX (low waist)", 10), S(".Heol Star Bracelet Gold", 6), S("DOUX - Oasis Hairstyle [M/Long]", 2),
        S("/ EYES / lel evox 4.0", 15), S("/ GUMS / TEETH / lel evox 4.0", 47), S(".Heol Star Earrings Gold", 13) };

    [Fact] public void HairTeethEyesEarrings_GoInTheFirstWave_HeadAndClothesAfter()
    {
        var w = LoginVeil.LogoutWaves(Live1456());
        Assert.Equal(2, w.Count);
        Assert.Equal(new[] { "DOUX - Oasis Hairstyle [M/Long]", "/ EYES / lel evox 4.0", "/ GUMS / TEETH / lel evox 4.0", ".Heol Star Earrings Gold" }, w[0].Select(x => x.Name));
        Assert.Contains(w[1], x => x.Name.Contains("HEAD")); Assert.Contains(w[1], x => x.Name.Contains("T-Shirt"));
        Assert.Equal(9, w.Sum(x => x.Count));
    }
    [Fact] public void NoHeadParts_OneWave() => Assert.Single(LoginVeil.LogoutWaves(new[] { S("TETRA - Chill T-Shirt - LaraX Petite", 55) }));

    [Fact] public async Task SecondWave_IsSentOnlyAfterTheFirstIsGone_AndTheLastSettles()
    {
        var waves = LoginVeil.LogoutWaves(Live1456()); var worn = new HashSet<UUID>(waves.SelectMany(w => w.Select(x => x.Item)));
        var order = new List<string>(); var t0 = DateTime.Now; DateTime firstGone = default, secondSent = default;
        var log = await LoginVeil.DetachInWaves(waves, x =>
        {
            order.Add(x.Name); if (x == waves[1][0]) secondSent = DateTime.Now;
            _ = Task.Run(async () => { await Task.Delay(200); lock (worn) { worn.Remove(x.Item); if (waves[0].All(y => !worn.Contains(y.Item)) && firstGone == default) firstGone = DateTime.Now; } });
        }, () => { lock (worn) return new HashSet<UUID>(worn); }, confirmMs: 2000, settleMs: 100, finalSettleMs: 300);
        Assert.True(secondSent > firstGone && firstGone != default);
        Assert.True(order.IndexOf("/ GUMS / TEETH / lel evox 4.0") < order.IndexOf("/ HEAD / lel evox / AVALON 4.0"));
        Assert.True((DateTime.Now - firstGone).TotalMilliseconds >= 300);
        Assert.All(log, l => Assert.Contains("confirmed gone", l));
    }
}
