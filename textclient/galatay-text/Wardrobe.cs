// Wardrobe.cs (2026-10-14, David): undress never clashes with outfit changes, the login one included. At 15:07 an undress
// raced the login outfit: only the COF links of the Bimbette alphas went, the wearables stayed. One wardrobe lock
// serializes undress and every outfit wear (undress waits for a running change, then verifies the wearables); automatic /
// random changes (login daily pick, beach-return restore) never run while she is undressed or within UndressQuietS of an
// undress, until she deliberately dresses (any outfit wear through the lock clears it).
using System;
using System.Threading;
using System.Threading.Tasks;

namespace GalatayText;

public static partial class Program
{
    internal static readonly SemaphoreSlim wardrobeLock = new(1, 1);
    internal static volatile bool wardrobeUndressed;
    internal static DateTime wardrobeUndressedAt = DateTime.MinValue;
    internal const int UndressQuietS = 180, WardrobeWaitS = 120;
    static string wardrobeHolder = "";

    internal static bool AutoOutfitAllowed(bool undressed, DateTime undressedAt, DateTime now)
        => !undressed && (now - undressedAt).TotalSeconds >= UndressQuietS;

    // (2026-11-09) true while an outfit change holds the wardrobe lock. The library's add-missing login outfit send waits on it
    // (Settings.Agent.OutfitSendBusy) and then re-reads the COF: at 14:15 the daily outfit took the bikini off while that send
    // held an older COF read, and the send put the bikini back on under the new outfit.
    internal static bool OutfitChanging() => LockHeld(wardrobeLock);
    internal static bool LockHeld(SemaphoreSlim s) => s.CurrentCount == 0;   // pure (tested)

    internal static void WardrobeUndressedNow() { wardrobeUndressed = true; wardrobeUndressedAt = DateTime.Now; }
    internal static void WardrobeDressed(string outfit) { if (wardrobeUndressed) Log("wardrobe", $"dressed deliberately ('{outfit}'): random changes allowed again"); wardrobeUndressed = false; }

    internal static async Task<T> WithWardrobe<T>(string who, Func<Task<T>> body, CancellationToken ct, int waitS = WardrobeWaitS)
    {
        if (!await wardrobeLock.WaitAsync(0, ct))
        {
            Log("wardrobe", $"{who}: waiting for '{wardrobeHolder}' to finish");
            if (!await wardrobeLock.WaitAsync(TimeSpan.FromSeconds(waitS), ct)) Log("wardrobe", $"{who}: '{wardrobeHolder}' still running after {waitS} s; going ahead");
            else { wardrobeHolder = who; try { return await body(); } finally { wardrobeHolder = ""; wardrobeLock.Release(); } }
            return await body();
        }
        wardrobeHolder = who;
        try { return await body(); } finally { wardrobeHolder = ""; wardrobeLock.Release(); }
    }
}
