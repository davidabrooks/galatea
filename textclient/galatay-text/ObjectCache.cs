// Object cache wiring (2026-11-23): Cinder closed our LibreMetaverse #219 and moved the on-disk cache out of
// core into LibreMetaverse.Caching. We opt in with Objects.CacheProvider = new ObjectDiskCache(dir, cap).
// GT_OBJECT_CACHE=off leaves the provider null (handshake always says the cache is empty, as before).
using System.Globalization;
using LibreMetaverse;
using LibreMetaverse.Caching;
using LibreMetaverse.Packets;

namespace GalatayText;

public static partial class Program
{
    internal const string ObjectCacheDefaultDir = "/home/box/viewers/textclient/objcache";
    internal const int ObjectCacheMaxEntriesPerRegion = 40000;

    internal static ObjectDiskCache LiveObjectCache;

    /// <summary>
    /// Wire <paramref name="c"/>'s object-update cache. Returns the provider, or null when
    /// <paramref name="dirOrOff"/> is empty or "off". The caller owns the returned instance (dispose
    /// it on logout / reconfigure); this method does not dispose a previous provider.
    /// </summary>
    internal static ObjectDiskCache ConfigureObjectCache(GridClient c, string dirOrOff, int maxEntries = ObjectCacheMaxEntriesPerRegion)
    {
        if (c == null) throw new ArgumentNullException(nameof(c));
        if (string.IsNullOrEmpty(dirOrOff) || dirOrOff.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            c.Objects.CacheProvider = null;
            return null;
        }
        var cache = new ObjectDiskCache(dirOrOff, maxEntries);
        c.Objects.CacheProvider = cache;
        return cache;
    }

    internal static string ObjectCacheStatus()
    {
        var cache = LiveObjectCache;
        if (cache == null) return "object cache: off";
        int files = 0; long bytes = 0;
        try
        {
            if (Directory.Exists(cache.Directory))
            {
                foreach (var f in Directory.EnumerateFiles(cache.Directory, "*.objcache"))
                {
                    files++;
                    try { bytes += new FileInfo(f).Length; } catch { }
                }
            }
        }
        catch { }
        var countPart = "";
        var sim = client?.Network?.CurrentSim;
        if (sim != null && sim.ID != UUID.Zero)
            countPart = $"; this region {cache.Count(sim.Handle, sim.ID)} entries";
        return string.Format(CultureInfo.InvariantCulture,
            "object cache: {0} cap {1}; {2} file(s) {3} bytes{4}",
            cache.Directory, cache.MaxEntriesPerRegion, files, bytes, countPart);
    }

    /// <summary>Offline round-trip of Store / Flush / reload / Lookup, used by crowd's self-test.</summary>
    internal static bool ObjectCacheSelfTest(out string why)
    {
        why = "";
        var dir = Path.Combine(Path.GetTempPath(), "galatea-objc-selftest-" + Guid.NewGuid().ToString("N"));
        const ulong handle = 0x123UL;
        var cacheId = new UUID("0c0c0c0c-0000-0000-0000-000000000001");
        try
        {
            Directory.CreateDirectory(dir);
            var data = new byte[40];
            Utils.UIntToBytes(1234, data, 16);
            data[20] = (byte)PCode.Prim;
            Utils.UIntToBytes(77, data, 22);
            var block = new ObjectUpdateCompressedPacket.ObjectDataBlock { UpdateFlags = 5, Data = data };
            using (var cache = new ObjectDiskCache(dir, 100))
            {
                cache.Store(handle, cacheId, block);
                if (!cache.HasEntries(handle, cacheId) || cache.Count(handle, cacheId) != 1)
                { why = "HasEntries after Store"; return false; }
                cache.Flush();
            }
            using (var cache = new ObjectDiskCache(dir, 100))
            {
                if (cache.Count(handle, cacheId) != 1) { why = "reload count"; return false; }
                var hits = new List<ObjectUpdateCompressedPacket.ObjectDataBlock>();
                var misses = cache.Lookup(handle, cacheId, new (uint, uint)[] { (1234, 77), (99, 1) }, hits);
                if (hits.Count != 1 || hits[0].UpdateFlags != 5 || hits[0].Data is not { Length: 40 }
                    || Utils.BytesToUInt(hits[0].Data, 16) != 1234 || misses.Count != 1 || misses[0] != 99)
                { why = "lookup round trip"; return false; }
            }
            why = "ok";
            return true;
        }
        catch (Exception ex) { why = ex.Message; return false; }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
