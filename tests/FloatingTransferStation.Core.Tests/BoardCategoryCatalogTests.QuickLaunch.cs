using System.Text.Json;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 快捷启动角色机制（#30，Issue #162）：CategoryRole 穷尽映射、枚举身份冻结
/// （Prompt 保名保值）、默认名「启动」、序列化身份兼容（历史 JSON 样本读取
/// /写回形态锁定）。SweepCategories 的角色化断言见 AutoCleanupScheduleTests。
/// </summary>
[TestClass]
public sealed partial class BoardCategoryCatalogTests
{
    [TestMethod]
    public void RoleOf_MapsEveryCategoryToItsQuickLaunchRole()
    {
        var expected = new Dictionary<BoardCategory, CategoryRole>
        {
            [BoardCategory.CustomerOriginal] = CategoryRole.ContentBucket,
            [BoardCategory.Reference] = CategoryRole.Review,
            [BoardCategory.Prompt] = CategoryRole.Launcher,
            [BoardCategory.Inbox] = CategoryRole.ContentBucket
        };
        CollectionAssert.AreEquivalent(
            expected.Keys.ToList(),
            Enum.GetValues<BoardCategory>().ToList(),
            "期望表必须覆盖全部命名成员（新增成员漏角色映射另有编译期 CS8509 防线）。");
        foreach (var (category, role) in expected)
        {
            Assert.AreEqual(role, BoardCategoryCatalog.RoleOf(category), $"{category} 的角色。");
        }
    }

    [TestMethod]
    public void PromptMember_IdentityIsFrozenNameAndValueUnchanged()
    {
        // 枚举身份冻结（#30）：成员名与数值不改——序列化身份兼容的唯一地基
        // （仓库先例：复盘复用成员名 Reference，不靠改名+转换器钉线名）。
        // 经反射按名取成员：改名即取不到成员、改值即原始常量不符（nameof/强类型
        // 常量在编译期折叠为恒真断言，分析器 MSTEST0032 会正确拦截）。
        var member = typeof(BoardCategory).GetField("Prompt");
        Assert.IsNotNull(member, "成员名 Prompt 冻结：改名即破坏历史 JSON 兼容。");
        Assert.AreEqual(2, Convert.ToInt32(member.GetRawConstantValue()), "成员数值 2 冻结。");
    }

    [TestMethod]
    public void DisplayName_PromptDefaultsToQuickLaunch()
    {
        Assert.AreEqual("启动", BoardCategoryCatalog.DisplayName(BoardCategory.Prompt));
    }

    [TestMethod]
    public void IsContentBucket_AgreesWithRoleOfForEveryCategory()
    {
        foreach (var category in BoardCategoryCatalog.Ordered)
        {
            Assert.AreEqual(
                BoardCategoryCatalog.RoleOf(category) == CategoryRole.ContentBucket,
                BoardCategoryCatalog.IsContentBucket(category),
                $"{category} 的内容桶判定必须由角色唯一派生。");
        }
    }

    [TestMethod]
    public async Task HistoricalSettingsJson_PromptKeyRoundTripsUnchanged()
    {
        // 历史 JSON 样本读取/写回形态锁定：settings.json 的 CategoryNames 以成员名
        // （"Prompt"）为键，角色让位不改变序列化形态。
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        await File.WriteAllTextAsync(paths.SettingsFile, """
            {
              "panelWidth": 420,
              "windowHeight": 700,
              "top": 120,
              "categoryNames": {
                "CustomerOriginal": "客户原图",
                "Reference": "复盘",
                "Prompt": "文本",
                "Inbox": "待处理"
              },
              "reviewMigrationVersion": 1,
              "categoryNameMigrationVersion": 1,
              "categoryOrder": [ "CustomerOriginal", "Reference", "Prompt", "Inbox" ]
            }
            """);
        var store = new LocalStore(paths, new AtomicTextWriter());

        var loaded = await store.LoadSettingsAsync();

        Assert.AreEqual("文本", loaded.CategoryName(BoardCategory.Prompt), "旧名在版本戳归位前照常显示。");
        Assert.AreEqual("复盘", loaded.CategoryName(BoardCategory.Reference));

        await store.SaveSettingsAsync(loaded);
        var json = await File.ReadAllTextAsync(paths.SettingsFile);
        using var document = JsonDocument.Parse(json);
        var names = document.RootElement.GetProperty("categoryNames");
        Assert.IsTrue(names.TryGetProperty("Prompt", out var promptName));
        Assert.AreEqual("文本", promptName.GetString());
        Assert.IsFalse(
            names.EnumerateObject().Any(property => property.Name is "文本桶" or "QuickLaunch" or "Launcher"),
            "枚举键不得出现改名后的形态。");
    }

    [TestMethod]
    public async Task HistoricalBoardJson_PromptCategoryItemsRoundTrip()
    {
        // board.json 条目的 category: "Prompt"（存量文本桶卡片）在角色让位后仍按
        // 原形态读取与写回——迁移负责把它们收编到 Inbox，存储层不丢不删。
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        await File.WriteAllTextAsync(paths.BoardFile, """
            {
              "schemaVersion": 1,
              "items": [
                {
                  "id": "0f2b7d1e-1111-2222-3333-444455556666",
                  "kind": "Text",
                  "category": "Prompt",
                  "order": 0,
                  "createdAt": "2026-10-01T08:00:00+00:00",
                  "text": "存量提示词",
                  "isPinned": false
                }
              ]
            }
            """);
        var store = new LocalStore(paths, new AtomicTextWriter());

        var loaded = await store.LoadBoardAsync();

        var item = loaded.Items.Single();
        Assert.AreEqual(BoardCategory.Prompt, item.Category);
        Assert.AreEqual("存量提示词", item.Text);

        await store.SaveBoardAsync(loaded);
        var json = await File.ReadAllTextAsync(paths.BoardFile);
        StringAssert.Contains(json, "\"category\": \"Prompt\"");
    }
}
