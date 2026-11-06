using System.Text.Json;
using System.Text.Json.Serialization;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Core.Tests;

/// <summary>
/// 分类内子分组（实验性，契约 #57）模型面：目录默认名与限长（文本元素口径）、
/// 三态偏好默认/往返/免迁移回落、名清单逐槽回落与单槽原子更新、非法条目回落默认。
/// </summary>
[TestClass]
public sealed class SubgroupModelTests
{
    private static readonly JsonSerializerOptions StoreOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    [TestMethod]
    public void SubgroupCatalog_DefaultNamesAndSlotBounds()
    {
        Assert.AreEqual("待分类", SubgroupCatalog.DefaultName(0));
        Assert.AreEqual("重要", SubgroupCatalog.DefaultName(1));
        Assert.AreEqual("不重要", SubgroupCatalog.DefaultName(2));
        Assert.AreEqual("待办", SubgroupCatalog.DefaultName(3));
        Assert.IsTrue(SubgroupCatalog.IsValidSlot(0));
        Assert.IsTrue(SubgroupCatalog.IsValidSlot(3));
        Assert.IsFalse(SubgroupCatalog.IsValidSlot(4), "越界槽号非法。");
        Assert.IsFalse(SubgroupCatalog.IsValidSlot(null), "null＝未指定，非合法槽值。");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SubgroupCatalog.DefaultName(4));
    }

    [TestMethod]
    public void SubgroupCatalog_NameValidationUsesTextElements()
    {
        Assert.IsTrue(SubgroupCatalog.IsValidName("急"));
        Assert.IsTrue(SubgroupCatalog.IsValidName("三字名"));
        Assert.IsFalse(SubgroupCatalog.IsValidName("四字名称"), "超 3 文本元素非法。");
        Assert.IsFalse(SubgroupCatalog.IsValidName("  "), "纯空白非法。");
        Assert.IsFalse(SubgroupCatalog.IsValidName(null));
        Assert.IsFalse(SubgroupCatalog.IsValidName(string.Empty));
        // 代理对按文本元素口径：一个 emoji＝1 元素、限长不截断在代理对中间。
        Assert.IsTrue(SubgroupCatalog.IsValidName("😀😀😀"));
        Assert.AreEqual(
            "😀😀😀",
            SubgroupCatalog.LimitName("😀😀😀😀"),
            "限长按文本元素截断（3 元素上限，不产生断代理对）。");
    }

    [TestMethod]
    public void SubgroupDisplay_DefaultsToOff()
    {
        Assert.AreEqual(SubgroupDisplayMode.Off, AppPreferences.Default.SubgroupDisplay);
    }

    [TestMethod]
    public void SubgroupDisplay_RoundTripsCamelCaseJson()
    {
        // LocalStore 以 camelCase + 字符串枚举持久化，三值走同一份选项验证往返。
        foreach (var mode in new[]
                 {
                     SubgroupDisplayMode.Off,
                     SubgroupDisplayMode.ChipRail,
                     SubgroupDisplayMode.GroupedView
                 })
        {
            var preferences = AppPreferences.Default with { SubgroupDisplay = mode };

            var restored = JsonSerializer.Deserialize<AppPreferences>(
                JsonSerializer.Serialize(preferences, StoreOptions), StoreOptions);

            Assert.AreEqual(preferences, restored);
            Assert.AreEqual(mode, restored!.SubgroupDisplay);
        }
    }

    [TestMethod]
    public void SubgroupDisplay_LegacyJsonMissingFieldFallsBackToOff()
    {
        var restored = JsonSerializer.Deserialize<AppPreferences>(
            """{"themeMode":"Light","autoUpdateCheckEnabled":false}""",
            StoreOptions);

        Assert.IsNotNull(restored);
        Assert.AreEqual(
            SubgroupDisplayMode.Off,
            restored.SubgroupDisplay,
            "旧 preferences.json 缺字段必须回落 Off（实验性默认零打扰）。");
    }

    [TestMethod]
    public void SubgroupNames_DefaultToNullAndResolvePerSlotFallback()
    {
        var settings = WindowSettings.Default;
        Assert.IsNull(settings.SubgroupNames, "未定制时名清单为 null（全默认）。");
        Assert.AreEqual("待分类", settings.SubgroupName(BoardCategory.Inbox, 0));
        Assert.AreEqual("重要", settings.SubgroupName(BoardCategory.Inbox, 1));
        Assert.AreEqual("不重要", settings.SubgroupName(BoardCategory.CustomerOriginal, 2));
        Assert.AreEqual("待办", settings.SubgroupName(BoardCategory.CustomerOriginal, 3));
    }

    [TestMethod]
    public void SubgroupNames_WithSubgroupName_UpdatesSingleSlotAtomically()
    {
        var settings = WindowSettings.Default
            .WithSubgroupName(BoardCategory.Inbox, 1, "急件")
            .WithSubgroupName(BoardCategory.Inbox, 1, "要事");

        Assert.AreEqual("要事", settings.SubgroupName(BoardCategory.Inbox, 1));
        Assert.AreEqual("待分类", settings.SubgroupName(BoardCategory.Inbox, 0), "未涉及槽保持默认。");
        Assert.AreEqual(
            "重要",
            settings.SubgroupName(BoardCategory.CustomerOriginal, 1),
            "其他分类同槽不受影响。");

        var withOther = settings.WithSubgroupName(BoardCategory.CustomerOriginal, 3, "催办");
        Assert.AreEqual("要事", withOther.SubgroupName(BoardCategory.Inbox, 1), "跨分类更新不动 Inbox。");
    }

    [TestMethod]
    public void SubgroupNames_InvalidEntriesFallBackToSlotDefault()
    {
        // 清单短（缺项）、空白名、超 3 字名一律逐槽回落默认，不整体弃用。
        var settings = WindowSettings.Default with
        {
            SubgroupNames = new Dictionary<BoardCategory, IReadOnlyList<string>>
            {
                [BoardCategory.Inbox] = ["", "   ", "四个字的名称", "合法"]
            }
        };

        Assert.AreEqual("待分类", settings.SubgroupName(BoardCategory.Inbox, 0), "空串回落默认。");
        Assert.AreEqual("重要", settings.SubgroupName(BoardCategory.Inbox, 1), "空白回落默认。");
        Assert.AreEqual("不重要", settings.SubgroupName(BoardCategory.Inbox, 2), "超长回落默认。");
        Assert.AreEqual("合法", settings.SubgroupName(BoardCategory.Inbox, 3), "合法自定义保留。");
        Assert.AreEqual(
            "待分类",
            settings.SubgroupName(BoardCategory.CustomerOriginal, 0),
            "清单短于槽号的缺项回落默认。");
    }
}
