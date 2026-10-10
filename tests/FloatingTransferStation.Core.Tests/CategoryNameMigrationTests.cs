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
        // 旧版本任一次改名都会把当时的全部默认名（含「文本2」）固化成显式快照。
        var settings = WindowSettings.Default
            .WithCategoryName(BoardCategory.CustomerOriginal, "截图")
            .WithCategoryName(BoardCategory.Prompt, CategoryNameMigration.LegacyPromptDisplayName);

        var migrated = await new CategoryNameMigration(store).EnsureAsync(settings);

        Assert.AreEqual("文本", migrated.CategoryName(BoardCategory.Prompt));
        Assert.AreEqual("截图", migrated.CategoryName(BoardCategory.CustomerOriginal));
        Assert.AreEqual("文本1", migrated.CategoryName(BoardCategory.Reference));
        Assert.AreEqual("待分类", migrated.CategoryName(BoardCategory.Inbox));
        Assert.AreEqual(CategoryNameMigration.CurrentVersion, migrated.CategoryNameMigrationVersion);

        var persisted = await store.LoadSettingsAsync();
        Assert.AreEqual("文本", persisted.CategoryName(BoardCategory.Prompt));
        Assert.AreEqual(CategoryNameMigration.CurrentVersion, persisted.CategoryNameMigrationVersion);
    }

    [TestMethod]
    public async Task EnsureAsync_KeepsCustomPromptName()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var settings = WindowSettings.Default.WithCategoryName(BoardCategory.Prompt, "我的提示");

        var migrated = await new CategoryNameMigration(store).EnsureAsync(settings);

        Assert.AreEqual("我的提示", migrated.CategoryName(BoardCategory.Prompt));
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
        Assert.AreEqual("文本", migrated.CategoryName(BoardCategory.Prompt));
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
            .WithCategoryName(BoardCategory.Prompt, CategoryNameMigration.LegacyPromptDisplayName);

        var failed = await new CategoryNameMigration(new LocalStore(paths, new FailingSettingsWriter()))
            .EnsureAsync(settings);

        Assert.IsTrue(ReferenceEquals(settings, failed));
        Assert.AreEqual(0, failed.CategoryNameMigrationVersion);
        Assert.IsFalse(File.Exists(paths.SettingsFile));

        var retried = await new CategoryNameMigration(new LocalStore(paths, new AtomicTextWriter()))
            .EnsureAsync(failed);

        Assert.AreEqual("文本", retried.CategoryName(BoardCategory.Prompt));
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
