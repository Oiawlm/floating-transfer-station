using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 一次性偏好迁移（契约 59，2026-11-07）锁定：实验开关默认开启的存量收敛五态
/// ——旧 JSON 显式 false＋无戳（置开＋落戳＋落盘）、戳 ≥1＋显式 false（不动＋
/// 零重写）、缺字段回落开启（新默认）、新装默认（置开落盘后幂等零写）、保存
/// 失败（采纳带戳内存基准，下次启动重放收敛）。持久化经 IPreferencesStore 唯一
/// 原子管线（契约 #5 同路）。
/// </summary>
[TestClass]
public sealed class ExperimentalDefaultsMigrationTests
{
    private static LocalStore CreateStore(TestDirectory directory, IAtomicTextWriter? writer = null) =>
        new(AppPaths.ForTests(directory.Root), writer ?? new AtomicTextWriter());

    [TestMethod]
    public async Task EnsureAsync_LegacyExplicitFalseWithoutStamp_EnablesBothAndPersistsStamp()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = CreateStore(directory);
        // 存量用户普遍形态：自动落盘的显式 false（QuickLaunchMigration 全量快照写入）＋无新戳字段。
        await File.WriteAllTextAsync(
            paths.PreferencesFile,
            """{"themeMode":"Light","quickLaunchMigrationVersion":1,"reviewMarkdownViewEnabled":false,"dragOutAsFileEnabled":false}""");
        var preferences = await store.LoadPreferencesAsync();
        Assert.IsFalse(preferences.ReviewMarkdownViewEnabled);
        Assert.IsFalse(preferences.DragOutAsFileEnabled);
        Assert.AreEqual(0, preferences.ExperimentalDefaultsMigrationVersion);

        var migrated = await new ExperimentalDefaultsMigration(store).EnsureAsync(preferences);

        Assert.IsTrue(migrated.ReviewMarkdownViewEnabled, "存量显式 false 由一次性迁移统一置开。");
        Assert.IsTrue(migrated.DragOutAsFileEnabled, "存量显式 false 由一次性迁移统一置开。");
        Assert.AreEqual(ExperimentalDefaultsMigration.CurrentVersion, migrated.ExperimentalDefaultsMigrationVersion);
        var persisted = await store.LoadPreferencesAsync();
        Assert.IsTrue(persisted.ReviewMarkdownViewEnabled, "置开结果落盘（重读断言）。");
        Assert.IsTrue(persisted.DragOutAsFileEnabled, "置开结果落盘（重读断言）。");
        Assert.AreEqual(ExperimentalDefaultsMigration.CurrentVersion, persisted.ExperimentalDefaultsMigrationVersion);
    }

    [TestMethod]
    public async Task EnsureAsync_StampedPreferencesExplicitFalse_NoOpAndDoesNotRewriteFile()
    {
        using var directory = new TestDirectory();
        var store = CreateStore(directory);
        // 迁移后用户显式关闭：戳 ≥1 是明确选择，迁移永不复翻。
        var stamped = AppPreferences.Default with
        {
            ReviewMarkdownViewEnabled = false,
            ExperimentalDefaultsMigrationVersion = ExperimentalDefaultsMigration.CurrentVersion,
        };
        await store.SavePreferencesAsync(stamped);
        var preferences = await store.LoadPreferencesAsync();
        var before = await File.ReadAllTextAsync(AppPaths.ForTests(directory.Root).PreferencesFile);

        var result = await new ExperimentalDefaultsMigration(store).EnsureAsync(preferences);

        Assert.IsTrue(ReferenceEquals(preferences, result), "戳 ≥1 直接返回入参，不动偏好。");
        Assert.IsFalse(result.ReviewMarkdownViewEnabled, "迁移后用户显式关闭永不复翻。");
        var after = await File.ReadAllTextAsync(AppPaths.ForTests(directory.Root).PreferencesFile);
        Assert.AreEqual(before, after, "幂等零写：盘上文件内容不重写。");
    }

    [TestMethod]
    public async Task EnsureAsync_JsonWithoutBothFields_FallsBackToEnabledAndStamps()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = CreateStore(directory);
        await File.WriteAllTextAsync(paths.PreferencesFile, """{"themeMode":"Light"}""");
        var preferences = await store.LoadPreferencesAsync();
        Assert.IsTrue(preferences.ReviewMarkdownViewEnabled, "缺字段回落构造默认＝新默认开启。");
        Assert.IsTrue(preferences.DragOutAsFileEnabled, "缺字段回落构造默认＝新默认开启。");

        var migrated = await new ExperimentalDefaultsMigration(store).EnsureAsync(preferences);

        Assert.IsTrue(migrated.ReviewMarkdownViewEnabled);
        Assert.IsTrue(migrated.DragOutAsFileEnabled);
        Assert.AreEqual(ExperimentalDefaultsMigration.CurrentVersion, migrated.ExperimentalDefaultsMigrationVersion);
        var persisted = await store.LoadPreferencesAsync();
        Assert.AreEqual(ExperimentalDefaultsMigration.CurrentVersion, persisted.ExperimentalDefaultsMigrationVersion);
    }

    [TestMethod]
    public async Task EnsureAsync_FreshInstallDefault_EnablesPersistsAndIdempotentSecondRunWritesNothing()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = CreateStore(directory);

        var first = await new ExperimentalDefaultsMigration(store).EnsureAsync(AppPreferences.Default);

        Assert.IsTrue(first.ReviewMarkdownViewEnabled);
        Assert.IsTrue(first.DragOutAsFileEnabled);
        Assert.AreEqual(ExperimentalDefaultsMigration.CurrentVersion, first.ExperimentalDefaultsMigrationVersion);
        var persisted = await store.LoadPreferencesAsync();
        Assert.AreEqual(ExperimentalDefaultsMigration.CurrentVersion, persisted.ExperimentalDefaultsMigrationVersion);
        var before = await File.ReadAllTextAsync(paths.PreferencesFile);

        var second = await new ExperimentalDefaultsMigration(store).EnsureAsync(first);

        Assert.IsTrue(ReferenceEquals(first, second), "重跑幂等：戳 ≥1 零写。");
        var after = await File.ReadAllTextAsync(paths.PreferencesFile);
        Assert.AreEqual(before, after, "重跑幂等：盘上文件内容不重写。");
    }

    [TestMethod]
    public async Task EnsureAsync_PreferencesSaveFailure_AdoptsStampedBaselineAndConvergesOnRetry()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        // 存量用户：盘上已有自动落盘的显式 false＋无戳（迁移只失败在自己的保存步）。
        await File.WriteAllTextAsync(
            paths.PreferencesFile,
            """{"themeMode":"Light","quickLaunchMigrationVersion":1,"reviewMarkdownViewEnabled":false,"dragOutAsFileEnabled":false}""");
        var failing = CreateStore(directory, new FailingFileWriter("preferences.json"));
        var legacy = await failing.LoadPreferencesAsync();

        var outcome = await new ExperimentalDefaultsMigration(failing).EnsureAsync(legacy);

        Assert.IsTrue(outcome.ReviewMarkdownViewEnabled, "保存失败非致命：采纳带戳内存基准继续启动。");
        Assert.IsTrue(outcome.DragOutAsFileEnabled, "保存失败非致命：采纳带戳内存基准继续启动。");
        Assert.AreEqual(ExperimentalDefaultsMigration.CurrentVersion, outcome.ExperimentalDefaultsMigrationVersion);
        var onDisk = await File.ReadAllTextAsync(paths.PreferencesFile);
        Assert.IsFalse(onDisk.Contains("experimentalDefaultsMigrationVersion"), "戳本体本次未落盘。");

        // 下次启动重放（盘上无戳 → 迁移再跑，保存成功收敛）。
        var retrying = CreateStore(directory);
        var reloaded = await retrying.LoadPreferencesAsync();
        Assert.IsFalse(reloaded.ReviewMarkdownViewEnabled);
        Assert.AreEqual(0, reloaded.ExperimentalDefaultsMigrationVersion);
        var retried = await new ExperimentalDefaultsMigration(retrying).EnsureAsync(reloaded);
        Assert.AreEqual(ExperimentalDefaultsMigration.CurrentVersion, retried.ExperimentalDefaultsMigrationVersion);
        var converged = await retrying.LoadPreferencesAsync();
        Assert.IsTrue(converged.ReviewMarkdownViewEnabled, "重放收敛：盘上置开＋落戳。");
        Assert.IsTrue(converged.DragOutAsFileEnabled, "重放收敛：盘上置开＋落戳。");
        Assert.AreEqual(ExperimentalDefaultsMigration.CurrentVersion, converged.ExperimentalDefaultsMigrationVersion);
    }

    private sealed class FailingFileWriter(string fileName) : IAtomicTextWriter
    {
        public Task WriteAsync(string path, string content, CancellationToken cancellationToken = default) =>
            Path.GetFileName(path) == fileName
                ? throw new IOException($"Injected {fileName} failure.")
                : Task.CompletedTask;
    }
}
