using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 子分组槽序持久化（契约 #57 修订，照 <see cref="CategoryDisplayOrderTests"/> 镜像）：
/// 每分类独立槽序清单（与 <see cref="WindowSettings.SubgroupNames"/> 同构、槽号即身份
/// 零迁移）；读取端整单校验非法回落恒等序；ResetToDefault 保留；单分类原子更新。
/// </summary>
[TestClass]
public sealed class SubgroupOrderTests
{
    [TestMethod]
    public void SubgroupOrder_WithoutCustomization_FallsBackToIdentityOrder()
    {
        CollectionAssert.AreEqual(
            Enumerable.Range(0, SubgroupCatalog.SlotCount).ToArray(),
            WindowSettings.Default.SubgroupOrder(BoardCategory.Inbox).ToArray());
        CollectionAssert.AreEqual(
            Enumerable.Range(0, SubgroupCatalog.SlotCount).ToArray(),
            WindowSettings.Default.SubgroupOrder(BoardCategory.Reference).ToArray());
    }

    [TestMethod]
    public void SubgroupOrder_InvalidCustomOrder_FallsBackToIdentityPerCategory()
    {
        // 非法（长度≠4/槽号重复/越界/负值）一律该分类回落恒等序，校验只在读取端
        // 一次做齐；另一分类不受连坐。
        var invalidOrders = new[]
        {
            new[] { 0, 1, 2 },
            new[] { 0, 1, 2, 3, 3 },
            new[] { 0, 0, 2, 3 },
            new[] { 0, 1, 2, 4 },
            new[] { -1, 1, 2, 3 }
        };

        foreach (var order in invalidOrders)
        {
            var settings = new WindowSettings(360, 640, 80)
            {
                SubgroupOrders = new Dictionary<BoardCategory, IReadOnlyList<int>>
                {
                    [BoardCategory.Inbox] = order,
                    [BoardCategory.Reference] = [3, 2, 1, 0]
                }
            };

            CollectionAssert.AreEqual(
                Enumerable.Range(0, SubgroupCatalog.SlotCount).ToArray(),
                settings.SubgroupOrder(BoardCategory.Inbox).ToArray(),
                $"非法槽序 {string.Join(",", order)} 必须该分类回落恒等序。");
            CollectionAssert.AreEqual(
                new[] { 3, 2, 1, 0 },
                settings.SubgroupOrder(BoardCategory.Reference).ToArray(),
                "另一分类的合法槽序不受非法清单连坐。");
        }
    }

    [TestMethod]
    public void SubgroupOrder_ValidCustomOrder_IsReturnedPerCategory()
    {
        var settings = WindowSettings.Default
            .WithSubgroupOrder(BoardCategory.Inbox, [2, 0, 3, 1])
            .WithSubgroupOrder(BoardCategory.Reference, [1, 3, 0, 2]);

        CollectionAssert.AreEqual(new[] { 2, 0, 3, 1 }, settings.SubgroupOrder(BoardCategory.Inbox).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 3, 0, 2 }, settings.SubgroupOrder(BoardCategory.Reference).ToArray());
    }

    [TestMethod]
    public void JsonRoundTrip_PreservesSubgroupOrders()
    {
        var storeOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var settings = WindowSettings.Default
            .WithSubgroupOrder(BoardCategory.Inbox, [3, 1, 0, 2])
            .WithSubgroupOrder(BoardCategory.Reference, [2, 3, 1, 0]);

        var restored = System.Text.Json.JsonSerializer.Deserialize<WindowSettings>(
            System.Text.Json.JsonSerializer.Serialize(settings, storeOptions),
            storeOptions);

        CollectionAssert.AreEqual(
            settings.SubgroupOrder(BoardCategory.Inbox).ToArray(),
            restored!.SubgroupOrder(BoardCategory.Inbox).ToArray());
        CollectionAssert.AreEqual(
            settings.SubgroupOrder(BoardCategory.Reference).ToArray(),
            restored.SubgroupOrder(BoardCategory.Reference).ToArray());
        // 名单与槽序零耦合：槽序定制不产生名单快照。
        Assert.IsNull(restored.SubgroupNames);
    }

    [TestMethod]
    public void ResetToDefault_PreservesCustomSubgroupOrders()
    {
        var settings = WindowSettings.Default
            .WithSubgroupOrder(BoardCategory.Inbox, [3, 1, 0, 2]);

        var reset = settings.ResetToDefault(1600, 900);

        CollectionAssert.AreEqual(
            new[] { 3, 1, 0, 2 },
            reset.SubgroupOrder(BoardCategory.Inbox).ToArray(),
            "子分组槽序是用户定制，与窗口几何一样不随恢复默认重置。");
    }

    [TestMethod]
    public void WithSubgroupOrder_UpdatesSingleCategoryAtomically()
    {
        var settings = WindowSettings.Default
            .WithSubgroupOrder(BoardCategory.Inbox, [1, 0, 3, 2]);

        var updated = settings.WithSubgroupOrder(BoardCategory.Reference, [3, 2, 1, 0]);

        CollectionAssert.AreEqual(
            new[] { 1, 0, 3, 2 },
            updated.SubgroupOrder(BoardCategory.Inbox).ToArray(),
            "其他分类槽序原值不动（单分类原子更新）。");
        CollectionAssert.AreEqual(
            new[] { 3, 2, 1, 0 },
            updated.SubgroupOrder(BoardCategory.Reference).ToArray());
        CollectionAssert.AreEqual(
            new[] { 1, 0, 3, 2 },
            settings.SubgroupOrder(BoardCategory.Inbox).ToArray(),
            "原实例不可变（record 原子更新）。");

        // 非法序抛参数异常（调用方先归一，镜像 WithSubgroupName）。
        Assert.ThrowsExactly<ArgumentException>(
            () => updated.WithSubgroupOrder(BoardCategory.Inbox, [0, 0, 2, 3]));
        Assert.ThrowsExactly<ArgumentException>(
            () => updated.WithSubgroupOrder(BoardCategory.Inbox, [0, 1, 2]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => updated.WithSubgroupOrder((BoardCategory)99, [0, 1, 2, 3]));
    }
}
