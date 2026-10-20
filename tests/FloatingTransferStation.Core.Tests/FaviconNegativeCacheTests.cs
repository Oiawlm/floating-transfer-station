using FloatingTransferStation.Services;
namespace FloatingTransferStation.Core.Tests;

/// <summary>
/// favicon 负缓存语义锁定（#30 修订，Issue #206）：TTL 只抑制会话内重复触发；命中
/// 不刷新失败时间（防永久延期）；容量有界淘汰最旧；显式失效（编辑目标）。
/// </summary>
[TestClass]
public sealed class FaviconNegativeCacheTests
{
    [TestMethod]
    public void Record_ThenHitWithinTtl()
    {
        var cache = new FaviconNegativeCache(TimeSpan.FromMinutes(10));
        var now = DateTimeOffset.UtcNow;
        cache.Record("key", now);

        Assert.IsTrue(cache.IsNegative("key", now.AddMinutes(9)), "TTL 内命中＝抑制重复触发。");
    }

    [TestMethod]
    public void Hit_DoesNotExtendFailureTime()
    {
        var cache = new FaviconNegativeCache(TimeSpan.FromMinutes(10));
        var recorded = DateTimeOffset.UtcNow;
        cache.Record("key", recorded);

        _ = cache.IsNegative("key", recorded.AddMinutes(9));
        _ = cache.IsNegative("key", recorded.AddMinutes(9).AddSeconds(30));

        Assert.IsFalse(cache.IsNegative("key", recorded.AddMinutes(10)), "命中不刷新失败时间——TTL 过后照常过期（防永久延期）。");
    }

    [TestMethod]
    public void Expired_BeyondTtl_NoLongerNegative()
    {
        var cache = new FaviconNegativeCache(TimeSpan.FromMinutes(10));
        var now = DateTimeOffset.UtcNow;
        cache.Record("key", now);

        Assert.IsFalse(cache.IsNegative("key", now.AddMinutes(10)), "TTL 过后不再抑制。");
    }

    [TestMethod]
    public void Record_Twice_KeepsEarliestFailureTime()
    {
        var cache = new FaviconNegativeCache(TimeSpan.FromMinutes(10));
        var first = DateTimeOffset.UtcNow;
        cache.Record("key", first);
        cache.Record("key", first.AddMinutes(5));

        Assert.IsFalse(cache.IsNegative("key", first.AddMinutes(10)), "重复记录不得顺延首次失败时间。");
    }

    [TestMethod]
    public void Remove_ClearsEntry()
    {
        var cache = new FaviconNegativeCache();
        var now = DateTimeOffset.UtcNow;
        cache.Record("key", now);
        cache.Remove("key");

        Assert.IsFalse(cache.IsNegative("key", now.AddSeconds(1)), "显式失效（编辑目标通道）。");
    }

    [TestMethod]
    public void Capacity_ExceedingEvictsOldest()
    {
        // TTL 远长于时标间隔：仅容量淘汰生效（排除 TTL 过期干扰）。
        var cache = new FaviconNegativeCache(TimeSpan.FromHours(2));
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < FaviconNegativeCache.Capacity; i++)
        {
            cache.Record($"key{i}", now.AddSeconds(i));
        }

        cache.Record("overflow", now.AddHours(1));

        Assert.IsFalse(cache.IsNegative("key0", now.AddHours(1)), "最旧记录被淘汰。");
        Assert.IsTrue(cache.IsNegative("overflow", now.AddHours(1)), "新记录在场。");
        Assert.IsTrue(cache.IsNegative($"key{FaviconNegativeCache.Capacity - 1}", now.AddHours(1)), "未越界记录保留。");
    }

    [TestMethod]
    public void Keys_IndependentPerIconKey()
    {
        var cache = new FaviconNegativeCache();
        var now = DateTimeOffset.UtcNow;
        cache.Record("site-a", now);

        Assert.IsFalse(cache.IsNegative("site-b", now), "同站不同目标（IconKey）互不牵连（负缓存键非 origin）。");
    }
}
