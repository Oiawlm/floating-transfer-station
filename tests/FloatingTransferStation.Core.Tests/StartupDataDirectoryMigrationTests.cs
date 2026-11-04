using System.Diagnostics;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 启动数据根归一的契约测试：旧形状登记（直下/嵌套）自动迁到同父「悬浮中转站」
/// 文件夹（唯一受管形状）、失败保旧运行＋告警、旧插件目录覆盖偏好并入数据根
/// plugins 后随迁移整体移动。全部走 <see cref="DataDirectoryChangeService"/>
/// 既有原子管线（登记替身注入失败路径）。
/// </summary>
[TestClass]
public sealed class StartupDataDirectoryMigrationTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(TestTempRoot.Root, "fts-startup-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FakeRegistration(string registered, bool commitSucceeds = true) : IDataDirectoryRegistration
    {
        public string? Current { get; set; } = registered;

        public List<(string DataDirectory, string DataParent)> Commits { get; } = [];

        public string? ReadDataDirectory() => Current;

        public bool TryCommit(string dataDirectory, string dataParent)
        {
            if (!commitSucceeds)
            {
                return false;
            }

            Commits.Add((dataDirectory, dataParent));
            Current = dataDirectory;
            return true;
        }
    }

    private static void CreateFiles(string directory, params (string RelativePath, string Content)[] files)
    {
        Directory.CreateDirectory(directory);
        foreach (var (relativePath, content) in files)
        {
            var path = Path.Combine(directory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }

    [TestMethod]
    public async Task MigratesLegacyDirectShapeToSameParentManagedRoot()
    {
        // 旧形状 A（<父>/Data）：启动归一到 <父>/悬浮中转站，登记双值齐更。
        var userParent = Path.Combine(_root, "store");
        var legacy = Path.Combine(userParent, "Data");
        CreateFiles(legacy, ("board.json", "{}"), ("plugins/sample/plugin.json", "{}"));
        var registration = new FakeRegistration(legacy);

        var result = await StartupDataDirectoryMigration.TryMigrateAsync(
            registration,
            static () => @"C:\fts-test-app.exe",
            static _ => true);

        Assert.IsTrue(result.ExitApplication);
        Assert.IsNull(result.Warning);
        Assert.AreEqual(1, registration.Commits.Count);
        Assert.AreEqual((Path.Combine(userParent, "悬浮中转站"), userParent), registration.Commits[0]);
        Assert.IsTrue(File.Exists(Path.Combine(userParent, "悬浮中转站", "board.json")), "数据直下新根。");
        Assert.IsTrue(File.Exists(Path.Combine(userParent, "悬浮中转站", "plugins", "sample", "plugin.json")),
            "插件目录随迁移整体移动。");
        Assert.IsTrue(File.Exists(Path.Combine(userParent, "悬浮中转站", RelocationCleanupMarker.FileName)),
            "迁移标记必须写入新根。");
        Assert.IsTrue(File.Exists(Path.Combine(legacy, "board.json")), "旧目录保留给延迟清理。");
    }

    [TestMethod]
    public async Task MigratesLegacyNestedShapeByHoistIntoMiddleLayerPath()
    {
        // 旧形状 B（<父>/悬浮中转站/Data）：就位迁移——中间层路径成为新数据根，
        // Data 内容上提一层；旧根整体让位到 .fts-superseded-*，标记记录其内 Data。
        var userParent = Path.Combine(_root, "store");
        var legacy = Path.Combine(userParent, "悬浮中转站", "Data");
        CreateFiles(legacy, ("board.json", "{}"), ("plugins/sample/plugin.json", "{}"));
        var registration = new FakeRegistration(legacy);

        var result = await StartupDataDirectoryMigration.TryMigrateAsync(
            registration,
            static () => @"C:\fts-test-app.exe",
            static _ => true);

        Assert.IsTrue(result.ExitApplication);
        Assert.IsNull(result.Warning);
        var managedRoot = Path.Combine(userParent, "悬浮中转站");
        Assert.AreEqual((managedRoot, userParent), registration.Commits[0]);
        Assert.IsTrue(File.Exists(Path.Combine(managedRoot, "board.json")), "数据直下新根。");
        Assert.IsTrue(File.Exists(Path.Combine(managedRoot, "plugins", "sample", "plugin.json")),
            "插件目录随迁移整体移动。");
        Assert.IsFalse(Directory.Exists(Path.Combine(managedRoot, "Data")), "新根内不得残留 Data 层。");

        // 延迟清理闭环：新根里的标记指向让位副本内的 Data，清理后旧数据删除、
        // 空的 .fts-superseded-* 包装一并移除。
        var notice = DataDirectoryCleanupProcessor.Run(managedRoot);
        Assert.IsNull(notice);
        Assert.IsFalse(Directory.Exists(managedRoot + Path.DirectorySeparatorChar + "Data"));
        var wrapper = Directory.GetParent(legacy)!.Parent!
            .EnumerateDirectories(".fts-superseded-*")
            .SingleOrDefault();
        Assert.IsNull(wrapper, "空的让位包装目录必须随清理移除。");
    }

    [TestMethod]
    public async Task KeepsLegacyRunningWithWarningWhenTargetOccupied()
    {
        // 迁移失败保旧：目标已存在非空 → 保旧形状继续运行＋告警，登记不动、零改动。
        var userParent = Path.Combine(_root, "store");
        var legacy = Path.Combine(userParent, "Data");
        CreateFiles(legacy, ("board.json", "{}"));
        var occupied = Path.Combine(userParent, "悬浮中转站");
        CreateFiles(occupied, ("user-notes.txt", "keep"));
        var registration = new FakeRegistration(legacy);

        var result = await StartupDataDirectoryMigration.TryMigrateAsync(
            registration,
            static () => @"C:\fts-test-app.exe",
            static _ => true);

        Assert.IsFalse(result.ExitApplication);
        StringAssert.Contains(result.Warning, "旧布局");
        Assert.AreEqual(0, registration.Commits.Count, "登记必须保持旧值。");
        Assert.AreEqual(legacy, registration.Current);
        Assert.IsTrue(File.Exists(Path.Combine(legacy, "board.json")), "旧目录原样保留。");
        Assert.IsTrue(File.Exists(Path.Combine(occupied, "user-notes.txt")), "非空目标内容不受触碰。");
    }

    [TestMethod]
    public async Task KeepsLegacyRunningWithWarningWhenCommitFails()
    {
        // 管线内失败（登记写入失败）：发布目录回滚、保旧运行＋告警。
        var userParent = Path.Combine(_root, "store");
        var legacy = Path.Combine(userParent, "Data");
        CreateFiles(legacy, ("board.json", "{}"));
        var registration = new FakeRegistration(legacy, commitSucceeds: false);

        var result = await StartupDataDirectoryMigration.TryMigrateAsync(
            registration,
            static () => @"C:\fts-test-app.exe",
            static _ => true);

        Assert.IsFalse(result.ExitApplication);
        StringAssert.Contains(result.Warning, "无法登记");
        Assert.IsTrue(File.Exists(Path.Combine(legacy, "board.json")), "源目录必须完好。");
        Assert.IsFalse(Directory.Exists(Path.Combine(userParent, "悬浮中转站")),
            "已发布目录必须按形状守卫回滚删除。");
    }

    [TestMethod]
    public async Task ManagedRegistrationAndMissingDirectoryTakeNoAction()
    {
        var managed = Path.Combine(_root, "store", "悬浮中转站");
        CreateFiles(managed, ("board.json", "{}"));
        var managedRegistration = new FakeRegistration(managed);
        Assert.AreEqual(
            StartupDataDirectoryMigrationResult.None,
            await StartupDataDirectoryMigration.TryMigrateAsync(managedRegistration, static () => @"C:\app.exe"));
        Assert.AreEqual(0, managedRegistration.Commits.Count);

        var missing = Path.Combine(_root, "store2", "Data");
        var missingRegistration = new FakeRegistration(missing);
        Assert.AreEqual(
            StartupDataDirectoryMigrationResult.None,
            await StartupDataDirectoryMigration.TryMigrateAsync(missingRegistration, static () => @"C:\app.exe"));
    }

    [TestMethod]
    public async Task MergesLegacyPluginsOverrideIntoDataRootBeforeMigration()
    {
        // 旧插件目录覆盖指向数据目录之外的非空目录：迁移前并入数据根 plugins
        //（同名冲突保留数据根版本），随迁移整体移动；覆盖目录本身保留。
        var userParent = Path.Combine(_root, "store");
        var legacy = Path.Combine(userParent, "Data");
        CreateFiles(
            legacy,
            ("board.json", "{}"),
            ("plugins/built-in/plugin.json", "{}"));
        var overrideDirectory = Path.Combine(_root, "external-plugins");
        CreateFiles(
            overrideDirectory,
            ("extra/plugin.json", "{}"),
            ("loose.dll", "bin"));
        var preferencesPath = Path.Combine(legacy, "preferences.json");
        File.WriteAllText(
            preferencesPath,
            $$"""{"themeMode":"Dark","pluginsDirectoryOverride":"{{overrideDirectory.Replace("\\", "\\\\")}}"}""");
        var registration = new FakeRegistration(legacy);

        var result = await StartupDataDirectoryMigration.TryMigrateAsync(
            registration,
            static () => @"C:\fts-test-app.exe",
            static _ => true);

        Assert.IsTrue(result.ExitApplication);
        Assert.IsNotNull(result.OverrideMerge);
        Assert.AreEqual(overrideDirectory, result.OverrideMerge.OverrideDirectory);
        Assert.AreEqual(2, result.OverrideMerge.MergedEntries, "两个覆盖条目并入数据根 plugins。");
        var managedRoot = Path.Combine(userParent, "悬浮中转站");
        Assert.IsTrue(File.Exists(Path.Combine(managedRoot, "plugins", "extra", "plugin.json")),
            "并入的覆盖插件随迁移移动到新根 plugins。");
        Assert.IsTrue(File.Exists(Path.Combine(managedRoot, "plugins", "loose.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(overrideDirectory, "extra", "plugin.json")),
            "覆盖目录本身绝不删除。");
    }

    [TestMethod]
    public async Task ConflictOnMergeKeepsDataRootVersion()
    {
        // 同名冲突（built-in 目录）：保留数据根版本，覆盖版本不并入。
        var userParent = Path.Combine(_root, "store");
        var legacy = Path.Combine(userParent, "Data");
        CreateFiles(
            legacy,
            ("board.json", "{}"),
            ("preferences.json", "{}"),
            ("plugins/built-in/plugin.json", "{}"));
        var overrideDirectory = Path.Combine(_root, "external-plugins");
        CreateFiles(overrideDirectory, ("built-in/plugin.json", "{}"));
        File.WriteAllText(
            Path.Combine(legacy, "preferences.json"),
            $$"""{"pluginsDirectoryOverride":"{{overrideDirectory.Replace("\\", "\\\\")}}"}""");
        var registration = new FakeRegistration(legacy);

        var result = await StartupDataDirectoryMigration.TryMigrateAsync(
            registration,
            static () => @"C:\fts-test-app.exe",
            static _ => true);

        Assert.IsTrue(result.ExitApplication);
        Assert.AreEqual(0, result.OverrideMerge!.MergedEntries, "冲突条目不并入。");
        Assert.AreEqual(1, result.OverrideMerge.SkippedConflicts);
        var managedRoot = Path.Combine(userParent, "悬浮中转站");
        Assert.AreEqual(
            "{}",
            File.ReadAllText(Path.Combine(managedRoot, "plugins", "built-in", "plugin.json")),
            "同名冲突保留数据根版本。");
    }

    [TestMethod]
    public async Task IgnoresOverrideInsideOrEmptyOrAbsentDirectory()
    {
        // 覆盖位于数据目录之内/目录为空/键缺失：一律忽略，不并入、不报错。
        var userParent = Path.Combine(_root, "store");
        var legacy = Path.Combine(userParent, "Data");
        CreateFiles(legacy, ("board.json", "{}"));
        var inside = Path.Combine(legacy, "inside-plugins");
        CreateFiles(inside, ("p/plugin.json", "{}"));
        var empty = Path.Combine(_root, "empty-plugins");
        Directory.CreateDirectory(empty);
        var registration = new FakeRegistration(legacy);

        File.WriteAllText(
            Path.Combine(legacy, "preferences.json"),
            $$"""{"pluginsDirectoryOverride":"{{inside.Replace("\\", "\\\\")}}"}""");
        var insideResult = await StartupDataDirectoryMigration.TryMigrateAsync(
            registration, static () => @"C:\app.exe", static _ => true);
        Assert.IsTrue(insideResult.ExitApplication);
        Assert.IsNull(insideResult.OverrideMerge, "数据目录之内的覆盖必须忽略。");

        var legacy2 = Path.Combine(_root, "store3", "Data");
        CreateFiles(legacy2, ("board.json", "{}"));
        var registration2 = new FakeRegistration(legacy2);
        File.WriteAllText(
            Path.Combine(legacy2, "preferences.json"),
            $$"""{"pluginsDirectoryOverride":"{{empty.Replace("\\", "\\\\")}}"}""");
        var emptyResult = await StartupDataDirectoryMigration.TryMigrateAsync(
            registration2, static () => @"C:\app.exe", static _ => true);
        Assert.IsTrue(emptyResult.ExitApplication);
        Assert.IsNull(emptyResult.OverrideMerge, "空覆盖目录必须忽略。");
    }

    [TestMethod]
    public async Task SpawnFailureExitsApplicationWithManualStartNotice()
    {
        // 登记已指向新目录：重启失败不回滚，退出并提示手动启动（管线既有语义）。
        var userParent = Path.Combine(_root, "store");
        var legacy = Path.Combine(userParent, "Data");
        CreateFiles(legacy, ("board.json", "{}"));
        var registration = new FakeRegistration(legacy);

        var result = await StartupDataDirectoryMigration.TryMigrateAsync(
            registration,
            static () => @"C:\fts-test-app.exe",
            static _ => false);

        Assert.IsTrue(result.ExitApplication);
        StringAssert.Contains(result.Warning, "自动重启失败");
        Assert.AreEqual(1, registration.Commits.Count, "登记不回滚。");
    }
}
