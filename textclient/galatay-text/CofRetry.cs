namespace GalatayText;

// 2026-10-31 16:40:18 relog (textclient.log): the login outfit change ran while the region still had no FetchInventory2 /
// FetchInventoryDescendents2 capability, so CofFolder() returned null and both the drop ("Maitreya LaraX Vintage Jeans", "PCP Beth Top")
// and the add (ARTi'S top, shorts, boots) logged "usual outfit: Current Outfit folder not found" and gave up. The old clothes stayed
// linked and worn under the new ones. A missing Current Outfit folder this early is transient: retry until it shows up.
public static class CofRetry
{
    public const string Missing = "Current Outfit folder not found";
    public static bool IsMissing(string r) => r != null && r.EndsWith(Missing, StringComparison.Ordinal);

    /// <summary>Runs op until its result is not "Current Outfit folder not found" or the tries run out. Returns the last result,
    /// with the try count when it needed more than one.</summary>
    public static async Task<string> Run(Func<CancellationToken, Task<string>> op, CancellationToken ct, int tries = 20, int delayMs = 3000,
        Func<int, CancellationToken, Task> delay = null)
    {
        delay ??= (ms, c) => Task.Delay(ms, c);
        string r = null;
        for (int i = 1; i <= Math.Max(1, tries); i++)
        {
            r = await op(ct);
            if (!IsMissing(r)) return i == 1 ? r : $"{r} (after {i} tries: the Current Outfit folder was not readable yet)";
            if (i < tries) { try { await delay(delayMs, ct); } catch (OperationCanceledException) { break; } }
        }
        return $"{r} (gave up after {tries} tries)";
    }
}
