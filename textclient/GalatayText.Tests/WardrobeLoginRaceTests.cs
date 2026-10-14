using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-14 15:07 login race: undress ran while the login outfit was changing; only COF links went.</summary>
public class WardrobeLoginRaceTests
{
    [Fact]
    public async Task Undress_waits_for_the_running_login_outfit_change()
    {
        var order = new List<string>();
        var login = Program.WithWardrobe("login outfit", async () => { order.Add("login start"); await Task.Delay(400); order.Add("login end"); return 0; }, CancellationToken.None);
        await Task.Delay(50);
        var undress = Program.WithWardrobe("undress", async () => { order.Add("undress"); await Task.Yield(); return 0; }, CancellationToken.None);
        await Task.WhenAll(login, undress);
        Assert.Equal(new[] { "login start", "login end", "undress" }, order);
    }

    [Fact]
    public void No_random_change_while_undressed_or_just_after()
    {
        var now = new DateTime(2026, 10, 14, 15, 7, 30);
        Assert.False(Program.AutoOutfitAllowed(true, now.AddSeconds(-20), now));
        Assert.False(Program.AutoOutfitAllowed(true, now.AddHours(-2), now));
        Assert.False(Program.AutoOutfitAllowed(false, now.AddSeconds(-60), now));
        Assert.True(Program.AutoOutfitAllowed(false, now.AddMinutes(-10), now));
        Assert.True(Program.AutoOutfitAllowed(false, DateTime.MinValue, now));
    }
}
