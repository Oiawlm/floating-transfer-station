using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class CategoryNameMigrationTests
{
    [TestMethod]
    public async Task EnsureAsync_RewritesLegacyPromptDefaultSnapshotKeepsOtherNames()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        // 旧版本任一次改名都会把当时的全部默认名（含「文本2」）固化成显式快照；
        // v2 一次到位：v1 遗留名「文本2」直接映射到新默认名「快捷启动」。
        var settings = WindowSettings.Default
            .WithCategoryName(BoardCategory.CustomerOriginal, "截图")
            .WithCategoryName(BoardCategory.Prompt, CategoryNameMigration.LegacyPromptDisplayName);

        var migrated = await new CategoryNameMigration(store).EnsureAsync(settings);

        Assert.AreEqual("快捷启动", migrated.CategoryName(BoardCategory.Prompt));
        Assert.AreEqual("截图", migrated.CategoryName(BoardCategory.CustomerOriginal));
        Assert.AreEqual("记录", migrated.CategoryName(BoardCategory.Reference));
        Assert.AreEqual("待分类", migrated.CategoryName(BoardCategory.Inbox));
        Assert.AreEqual(CategoryNameMigration.CurrentVersion, migrated.CategoryNameMigrationVersion);

        var persisted = await store.LoadSettingsAsync();
        Assert.AreEqual("快捷启动", persisted.CategoryName(BoardCategory.Prompt));
        Assert.AreEqual(CategoryNameMigration.CurrentVersion, persisted.CategoryNameMigrationVersion);
    }

    [TestMethod]
    public async Task EnsureAsync_V2MapsLegacyDefaultNamesToNewDefaults()
    {
        // v2 核心：存量快照中恰为旧默认名「图片」「文本1」「启动」时映射到
        // 「重要」「记录」「快捷启动」；待分类不变。
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var settings = WindowSettings.Default
            .WithCategoryName(BoardCategory.CustomerOriginal, "图片")
            .WithCategoryName(BoardCategory.Reference, "文本1")
            .WithCategoryName(BoardCategory.Prompt, "启动");

        var migrated = await new CategoryNameMigration(store).EnsureAsync(settings);

        Assert.AreEqual("重要", migrated.CategoryName(BoardCategory.CustomerOriginal));
        Assert.AreEqual("记录", migrated.CategoryName(BoardCategory.Reference));
        Assert.AreEqual("快捷启动", migrated.CategoryName(BoardCategory.Prompt));
        Assert.AreEqual("待分类", migrated.CategoryName(BoardCategory.Inbox));
        Assert.AreEqual(CategoryNameMigration.CurrentVersion, migrated.CategoryNameMigrationVersion);

        var persisted = await store.LoadSettingsAsync();
        Assert.AreEqual("重要", persisted.CategoryName(BoardCategory.CustomerOriginal));
        Assert.AreEqual("记录", persisted.CategoryName(BoardCategory.Reference));
        Assert.AreEqual("快捷启动", persisted.CategoryName(BoardCategory.Prompt));
    }

    [TestMethod]
    public async Task EnsureAsync_V2MapsMaintainerHistoricalReviewNameToRecord()
    {
        // 「复盘」是维护者本机对 Reference 的历史自定义名，用户 2026-11-03 明确
        // 要求随默认名通用化一并更新为「记录」。
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var settings = WindowSettings.Default
            .WithCategoryName(BoardCategory.Reference, "复盘");

        var migrated = await new CategoryNameMigration(store).EnsureAsync(settings);

        Assert.AreEqual("记录", migrated.CategoryName(BoardCategory.Reference));
        Assert.AreEqual(CategoryNameMigration.CurrentVersion, migrated.CategoryNameMigrationVersion);
    }

    [TestMethod]
    public async Task EnsureAsync_V2KeepsCustomNamesUnmapped()
    {
        // 其他自定义名一律保留不动——只有恰为旧默认名/「复盘」的值才映射。
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var settings = WindowSettings.Default
            .WithCategoryName(BoardCategory.CustomerOriginal, "我的截图")
            .WithCategoryName(BoardCategory.Reference, "笔记")
            .WithCategoryName(BoardCategory.Prompt, "软件");

        var migrated = await new CategoryNameMigration(store).EnsureAsync(settings);

        Assert.AreEqual("我的截图", migrated.CategoryName(BoardCategory.CustomerOriginal));
        Assert.AreEqual("笔记", migrated.CategoryName(BoardCategory.Reference));
        Assert.AreEqual("软件", migrated.CategoryName(BoardCategory.Prompt));
        Assert.AreEqual("待分类", migrated.CategoryName(BoardCategory.Inbox));
        Assert.AreEqual(CategoryNameMigration.CurrentVersion, migrated.CategoryNameMigrationVersion);
    }

    [TestMethod]
    public async Task EnsureAsync_V1ToV2TransitionalStateMapsOldQuickLaunchDefault()
    {
        // v1→v2 途经态：版本 1 的存量快照（v1 时代默认名/快捷启动迁移归位写的
        // 「启动」）在 v2 映射为「快捷启动」。
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var settings = WindowSettings.Default
            .WithCategoryName(BoardCategory.Prompt, "启动") with
        {
            CategoryNameMigrationVersion = 1
        };

        var migrated = await new CategoryNameMigration(store).EnsureAsync(settings);

        Assert.AreEqual("快捷启动", migrated.CategoryName(BoardCategory.Prompt));
        Assert.AreEqual(CategoryNameMigration.CurrentVersion, migrated.CategoryNameMigrationVersion);
    }

    [TestMethod]
    public async Task EnsureAsync_KeepsCustomPromptName()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var settings = WindowSettings.Default.WithCategoryName(BoardCategory.Prompt, "我的软件");

        var migrated = await new CategoryNameMigration(store).EnsureAsync(settings);

        Assert.AreEqual("我的软件", migrated.CategoryName(BoardCategory.Prompt));
        Assert.AreEqual(CategoryNameMigration.CurrentVersion, migrated.CategoryNameMigrationVersion);
    }

    [TestMethod]
    public async Task EnsureAsync_FreshSettingsKeepNullNamesAndOnlyBumpVersion()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());

        var migrated = await new CategoryNameMigration(store).EnsureAsync(WindowSettings.Default);

        Assert.IsNull(migrated.CategoryNames);
        Assert.AreEqual(CategoryNameMigration.CurrentVersion, migrated.CategoryNameMigrationVersion);
        Assert.AreEqual("快捷启动", migrated.CategoryName(BoardCategory.Prompt));
    }

    [TestMethod]
    public async Task EnsureAsync_IsNoOpOnceMigrated()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var settings = WindowSettings.Default with
        {
            CategoryNameMigrationVersion = CategoryNameMigration.CurrentVersion
        };

        var migrated = await new CategoryNameMigration(store).EnsureAsync(settings);

        Assert.IsTrue(ReferenceEquals(settings, migrated));
        Assert.IsFalse(File.Exists(paths.SettingsFile));
    }

    [TestMethod]
    public async Task EnsureAsync_WhenSettingsSaveFails_ReturnsOriginalAndRetriesNextRun()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var settings = WindowSettings.Default
            .WithCategoryName(BoardCategory.Prompt, "启动");

        var failed = await new CategoryNameMigration(new LocalStore(paths, new FailingSettingsWriter()))
            .EnsureAsync(settings);

        Assert.IsTrue(ReferenceEquals(settings, failed));
        Assert.AreEqual(0, failed.CategoryNameMigrationVersion);
        Assert.IsFalse(File.Exists(paths.SettingsFile));

        var retried = await new CategoryNameMigration(new LocalStore(paths, new AtomicTextWriter()))
            .EnsureAsync(failed);

        Assert.AreEqual("快捷启动", retried.CategoryName(BoardCategory.Prompt));
        Assert.AreEqual(CategoryNameMigration.CurrentVersion, retried.CategoryNameMigrationVersion);
    }

    private sealed class FailingSettingsWriter : IAtomicTextWriter
    {
        public Task WriteAsync(string path, string content, CancellationToken cancellationToken = default) =>
            Path.GetFileName(path) == "settings.json"
                ? throw new IOException("Injected settings failure.")
                : Task.CompletedTask;
    }
}
