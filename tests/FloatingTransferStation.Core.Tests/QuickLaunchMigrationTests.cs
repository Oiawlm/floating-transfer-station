using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 快捷启动迁移（#30，Issue #162）跨文件提交协议锁定：迁移六态
/// （空桶/有卡收编/自定义名归位/失败回滚/幂等再跑/降级往返）与各阶段独立失败
/// （板面保存失败全还原且中止、settings.json 失败非致命重试、preferences.json
/// 戳失败采纳带戳内存基准）。收编保置顶分区（契约 #2）与批内显示顺序。
/// </summary>
[TestClass]
public sealed class QuickLaunchMigrationTests
{
    private static LocalStore CreateStore(TestDirectory directory, IAtomicTextWriter? writer = null) =>
        new(AppPaths.ForTests(directory.Root), writer ?? new AtomicTextWriter());

    [TestMethod]
    public async Task EnsureAsync_EmptyPromptBucket_OnlyStampsNameAndVersion()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = CreateStore(directory);
        var board = new BoardService();
        var settings = WindowSettings.Default;
        var preferences = AppPreferences.Default;

        var outcome = await new QuickLaunchMigration(store, store).EnsureAsync(board, settings, preferences);

        Assert.AreEqual("启动", outcome.Settings.CategoryName(BoardCategory.Prompt));
        Assert.AreEqual(QuickLaunchMigration.CurrentVersion, outcome.Preferences.QuickLaunchMigrationVersion);
        Assert.IsFalse(File.Exists(paths.BoardFile), "空桶收编不写 board.json。");
        Assert.IsTrue(File.Exists(paths.SettingsFile));
        Assert.IsTrue(File.Exists(paths.PreferencesFile));
        var persistedPreferences = await store.LoadPreferencesAsync();
        Assert.AreEqual(QuickLaunchMigration.CurrentVersion, persistedPreferences.QuickLaunchMigrationVersion);
    }

    [TestMethod]
    public async Task EnsureAsync_MovesPromptCardsToInbox_PreservingPinnedPartitionAndBatchOrder()
    {
        using var directory = new TestDirectory();
        var store = CreateStore(directory);
        var board = new BoardService();
        var promptFirst = board.AddText("提示甲", BoardCategory.Prompt);
        var promptPinned = board.AddText("提示置顶", BoardCategory.Prompt);
        board.SetPinnedMany([promptPinned.Id], true);
        var promptSecond = board.AddText("提示乙", BoardCategory.Prompt);
        var inboxOlder = board.AddText("存量旧", BoardCategory.Inbox);
        var inboxNewer = board.AddText("存量新", BoardCategory.Inbox);

        var outcome = await new QuickLaunchMigration(store, store).EnsureAsync(
            board,
            WindowSettings.Default,
            AppPreferences.Default);

        Assert.AreEqual(0, board.Items(BoardCategory.Prompt).Count, "Prompt 桶清空（收编）。");
        var inboxItems = board.Items(BoardCategory.Inbox).ToArray();
        // 契约 #2：置顶区在前；普通区收编卡按原显示顺序（乙先于甲）落在存量之上。
        Assert.AreEqual(promptPinned.Id, inboxItems[0].Id);
        Assert.IsTrue(inboxItems[0].IsPinned);
        Assert.AreEqual(promptSecond.Id, inboxItems[1].Id);
        Assert.AreEqual(promptFirst.Id, inboxItems[2].Id);
        Assert.AreEqual(inboxNewer.Id, inboxItems[3].Id);
        Assert.AreEqual(inboxOlder.Id, inboxItems[4].Id);
        var persisted = await store.LoadBoardAsync();
        Assert.AreEqual(0, persisted.Items.Count(item => item.Category == BoardCategory.Prompt));
        Assert.AreEqual(5, persisted.Items.Count(item => item.Category == BoardCategory.Inbox));
        Assert.AreEqual("启动", outcome.Settings.CategoryName(BoardCategory.Prompt));
    }

    [TestMethod]
    public async Task EnsureAsync_CustomPromptName_OverwrittenOnceThenBecomesUserStorage()
    {
        using var directory = new TestDirectory();
        var store = CreateStore(directory);
        var board = new BoardService();
        var settings = WindowSettings.Default.WithCategoryName(BoardCategory.Prompt, "我的提示");

        var migrated = await new QuickLaunchMigration(store, store).EnsureAsync(
            board, settings, AppPreferences.Default);

        Assert.AreEqual("启动", migrated.Settings.CategoryName(BoardCategory.Prompt), "一次性归位覆写自定义名（CHANGELOG 明示）。");

        // 戳 ≥1 后该键是合法用户改名存储：再改名不经迁移、重跑不覆写。
        var renamed = migrated.Settings.WithCategoryName(BoardCategory.Prompt, "快捷");
        var rerun = await new QuickLaunchMigration(store, store).EnsureAsync(
            board, renamed, migrated.Preferences);

        Assert.AreEqual("快捷", rerun.Settings.CategoryName(BoardCategory.Prompt));
        Assert.IsTrue(ReferenceEquals(renamed, rerun.Settings), "戳后重跑不再改写设置。");
    }

    [TestMethod]
    public async Task EnsureAsync_BoardSaveFailure_UndoesMoveSkipsNamingAndRetriesNextLaunch()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var failing = CreateStore(directory, new FailingFileWriter("board.json"));
        var board = new BoardService();
        var first = board.AddText("提示甲", BoardCategory.Prompt);
        var second = board.AddText("提示乙", BoardCategory.Prompt);
        // 存量快照存有旧名「文本」（升级用户普遍形态）：失败路径下显示名暂为旧名。
        var settings = WindowSettings.Default.WithCategoryName(BoardCategory.Prompt, "文本");
        var preferences = AppPreferences.Default;
        var promptOrderBefore = board.Items(BoardCategory.Prompt).Select(item => item.Id).ToArray();

        var outcome = await new QuickLaunchMigration(failing, failing).EnsureAsync(board, settings, preferences);

        Assert.IsTrue(ReferenceEquals(settings, outcome.Settings), "板面保存失败：名称归位本次不执行。");
        Assert.AreEqual(0, outcome.Preferences.QuickLaunchMigrationVersion);
        CollectionAssert.AreEqual(
            promptOrderBefore,
            board.Items(BoardCategory.Prompt).Select(item => item.Id).ToArray(),
            "失败全还原：Prompt 桶恢复原内容与顺序。");
        Assert.AreEqual("文本", outcome.Settings.CategoryName(BoardCategory.Prompt), "显示名暂为旧名。");
        Assert.IsFalse(File.Exists(paths.SettingsFile), "中止路径不落 settings.json。");
        Assert.AreEqual(second.Id, promptOrderBefore[0]);
        Assert.AreEqual(first.Id, promptOrderBefore[1]);

        var retrying = CreateStore(directory);
        var retried = await new QuickLaunchMigration(retrying, retrying).EnsureAsync(board, settings, preferences);
        Assert.AreEqual(0, board.Items(BoardCategory.Prompt).Count, "下次启动重试成功收编。");
        Assert.AreEqual("启动", retried.Settings.CategoryName(BoardCategory.Prompt));
    }

    [TestMethod]
    public async Task EnsureAsync_IdempotentSecondRunWritesNothingNew()
    {
        using var directory = new TestDirectory();
        var store = CreateStore(directory);
        var board = new BoardService();
        board.AddText("提示", BoardCategory.Prompt);

        var first = await new QuickLaunchMigration(store, store).EnsureAsync(
            board, WindowSettings.Default, AppPreferences.Default);
        var boardWriteTime = File.GetLastWriteTimeUtc(AppPaths.ForTests(directory.Root).BoardFile);
        var settingsWriteTime = File.GetLastWriteTimeUtc(AppPaths.ForTests(directory.Root).SettingsFile);

        var second = await new QuickLaunchMigration(store, store).EnsureAsync(
            board, first.Settings, first.Preferences);

        Assert.IsTrue(ReferenceEquals(first.Settings, second.Settings));
        Assert.IsTrue(ReferenceEquals(first.Preferences, second.Preferences));
        Assert.AreEqual(boardWriteTime, File.GetLastWriteTimeUtc(AppPaths.ForTests(directory.Root).BoardFile));
        Assert.AreEqual(settingsWriteTime, File.GetLastWriteTimeUtc(AppPaths.ForTests(directory.Root).SettingsFile));
    }

    [TestMethod]
    public async Task EnsureAsync_SettingsSaveFailure_NameStaysOldAndRetriesNextLaunch()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var failing = CreateStore(directory, new FailingFileWriter("settings.json"));
        var board = new BoardService();
        var settings = WindowSettings.Default.WithCategoryName(BoardCategory.Prompt, "文本");

        var outcome = await new QuickLaunchMigration(failing, failing).EnsureAsync(
            board, settings, AppPreferences.Default);

        Assert.IsTrue(ReferenceEquals(settings, outcome.Settings), "阶段 3 失败非致命：显示名暂为旧名。");
        Assert.AreEqual(0, outcome.Preferences.QuickLaunchMigrationVersion, "未归位成功不落戳。");
        Assert.IsFalse(File.Exists(paths.PreferencesFile));

        var retrying = CreateStore(directory);
        var retried = await new QuickLaunchMigration(retrying, retrying).EnsureAsync(
            board, settings, AppPreferences.Default);
        Assert.AreEqual("启动", retried.Settings.CategoryName(BoardCategory.Prompt));
        Assert.AreEqual(QuickLaunchMigration.CurrentVersion, retried.Preferences.QuickLaunchMigrationVersion);
    }

    [TestMethod]
    public async Task EnsureAsync_PreferencesStampFailure_AdoptsStampedMemoryBaseline()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var failing = CreateStore(directory, new FailingFileWriter("preferences.json"));
        var board = new BoardService();

        var outcome = await new QuickLaunchMigration(failing, failing).EnsureAsync(
            board, WindowSettings.Default, AppPreferences.Default);

        Assert.AreEqual("启动", outcome.Settings.CategoryName(BoardCategory.Prompt), "名称已先落盘。");
        Assert.AreEqual(
            QuickLaunchMigration.CurrentVersion,
            outcome.Preferences.QuickLaunchMigrationVersion,
            "采纳带戳内存基准：后续偏好保存把戳带回盘上自愈。");
        Assert.IsFalse(File.Exists(paths.PreferencesFile), "戳本体本次未落盘。");
    }

    [TestMethod]
    public async Task EnsureAsync_DowngradeRoundTrip_RecollectsCardsWrittenByOldVersion()
    {
        // 降级往返（codex r1 定稿场景）：升级迁移 → 旧版本又写 Prompt 新卡 → 再升级
        // 幂等再收编；断言幂等与守恒（无重复无丢失），不自动逆迁移。
        using var directory = new TestDirectory();
        var store = CreateStore(directory);
        var board = new BoardService();
        var legacyCard = board.AddText("升级前的存量提示词", BoardCategory.Prompt);

        var upgraded = await new QuickLaunchMigration(store, store).EnsureAsync(
            board, WindowSettings.Default, AppPreferences.Default);
        Assert.AreEqual(0, board.Items(BoardCategory.Prompt).Count);
        var inboxAfterUpgrade = board.Items(BoardCategory.Inbox).Select(item => item.Id).ToList();

        // 模拟降级期旧版本写入的新 Prompt 卡（角色机制前的写入路径）。
        var downgradeCard = board.AddText("降级期写入的新提示词", BoardCategory.Prompt);
        await store.SaveBoardAsync(board.CreateSnapshot());

        var reupgraded = await new QuickLaunchMigration(store, store).EnsureAsync(
            board, upgraded.Settings, upgraded.Preferences);

        Assert.AreEqual(0, board.Items(BoardCategory.Prompt).Count, "再升级幂等收编降级期新卡。");
        var inboxAfterReupgrade = board.Items(BoardCategory.Inbox).Select(item => item.Id).ToList();
        CollectionAssert.AreEqual(
            inboxAfterUpgrade,
            inboxAfterReupgrade.Skip(1).ToList(),
            "收编守恒：升级前卡片原位保留。");
        Assert.AreEqual(downgradeCard.Id, inboxAfterReupgrade[0], "降级期新卡按批内序收编在普通区顶部。");
        Assert.AreEqual(
            "启动",
            reupgraded.Settings.CategoryName(BoardCategory.Prompt),
            "戳已 ≥1：名称不再重放覆写。");
    }

    private sealed class FailingFileWriter(string fileName) : IAtomicTextWriter
    {
        public Task WriteAsync(string path, string content, CancellationToken cancellationToken = default) =>
            Path.GetFileName(path) == fileName
                ? throw new IOException($"Injected {fileName} failure.")
                : Task.CompletedTask;
    }
}
