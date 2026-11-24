using System;
using System.IO;
using GalatayText;
using LibreMetaverse;
using LibreMetaverse.Caching;
using Xunit;

public class ObjectCacheTests
{
    [Fact]
    public void ConfigureObjectCacheWiresTheProviderWithTheGivenDirectoryAndCap()
    {
        var dir = Path.Combine(Path.GetTempPath(), "galatea-objc-wire-" + Guid.NewGuid().ToString("N"));
        var client = new GridClient();
        ObjectDiskCache cache = null;
        try
        {
            cache = Program.ConfigureObjectCache(client, dir, Program.ObjectCacheMaxEntriesPerRegion);
            Assert.NotNull(cache);
            Assert.Same(cache, client.Objects.CacheProvider);
            Assert.Equal(dir, cache.Directory);
            Assert.Equal(Program.ObjectCacheMaxEntriesPerRegion, cache.MaxEntriesPerRegion);
            Assert.Equal(40000, cache.MaxEntriesPerRegion);
        }
        finally
        {
            try { cache?.Dispose(); } catch { }
            if (client?.Objects != null) client.Objects.CacheProvider = null;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void ConfigureObjectCacheLeavesTheProviderNullWhenDisabled()
    {
        var client = new GridClient();
        Assert.Null(Program.ConfigureObjectCache(client, "off"));
        Assert.Null(client.Objects.CacheProvider);
        Assert.Null(Program.ConfigureObjectCache(client, "OFF"));
        Assert.Null(client.Objects.CacheProvider);
        Assert.Null(Program.ConfigureObjectCache(client, ""));
        Assert.Null(client.Objects.CacheProvider);
    }

    [Fact]
    public void ObjectCacheSelfTestRoundTripsABlock()
    {
        Assert.True(Program.ObjectCacheSelfTest(out var why), why);
        Assert.Equal("ok", why);
    }
}
