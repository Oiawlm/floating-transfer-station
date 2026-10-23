using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 启动·自动发现核心服务（#45）：差分三集合（启动目录 ∪ pending ∪ dismissed）、
/// pending 按键去重防重扫重复入列、采纳＝从 pending 移除、忽略＝入 dismissed
/// （FIFO 上限 500）、持久化往返与损坏回落、保存失败还原＋状态条。候选源全部
/// 注入假源（UpdateService 模式，契约 #26）。
/// </summary>
[TestClass]
public sealed class LauncherDiscoveryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

    private static LocalStore CreateStore(TestDirectory directory) =>
        new(AppPaths.ForTests(directory.Root), new AtomicTextWriter());

    private static LauncherDiscoveryEntry Entry(
        string targetPath,
        string displayName = "发现的应用",
        LauncherKind kind = LauncherKind.Application,
        string? arguments = null) =>
        new(kind, targetPath, arguments, displayName, Now);

    private static LauncherItem CatalogItem(
        string targetPath,
        string? arguments = null) =>
        new(Guid.NewGuid(), LauncherKind.Application, targetPath, "已添加", Now, arguments);

    private static Task<IReadOnlyList<LauncherDiscoveryEntry>> Source(
        params LauncherDiscoveryEntry[] entries) =>
        Task.FromResult<IReadOnlyList<LauncherDiscoveryEntry>>(entries);

    private static async Task<LauncherDiscoveryService> LoadAsync(
        ILauncherDiscoveryStore store,
        List<string> statuses)
    {
        var service = await LauncherDiscoveryService.LoadAsync(store, statuses.Add);
        return service;
    }

    [TestMethod]
    public async Task Rescan_DiscoversCandidatesOutsideTheThreeSets()
    {
        using var directory = new TestDirectory();
        var service = await LoadAsync(new VolatileLauncherDiscoveryStore(), []);

        var pending = await service.RescanAsync(
            () => Source(
                Entry(@"C:\Apps\New.exe", "新应用"),
                Entry(@"C:\Apps\Existing.exe", "已在启动目录")),
            [CatalogItem(@"C:\Apps\Existing.exe")],
            Now);

        Assert.AreEqual(1, pending, "只有不在启动目录的新候选入列。");
        Assert.AreEqual(@"C:\Apps\New.exe", service.PendingSnapshot().Single().TargetPath);
    }

    [TestMethod]
    public async Task Rescan_Twice_DoesNotDuplicatePending()
    {
        var service = await LoadAsync(new VolatileLauncherDiscoveryStore(), []);
        var candidates = new[]
        {
            Entry(@"C:\Apps\A.exe", "A"),
            Entry(@"C:\Apps\B.lnk", "B")
        };

        await service.RescanAsync(() => Source(candidates), [], Now);
        var second = await service.RescanAsync(() => Source(candidates), [], Now);

        Assert.AreEqual(2, second, "重扫不重复入列（pending 按 DedupKey 去重）。");
        Assert.AreEqual(2, service.PendingSnapshot().Count);
    }

    [TestMethod]
    public async Task Rescan_NormalizesKeysCaseAndSeparatorInsensitively()
    {
        var service = await LoadAsync(new VolatileLauncherDiscoveryStore(), []);

        // 目录里是盘符小写 + 尾分隔符变体；发现候选必须按同一规范化键判重。
        var pending = await service.RescanAsync(
            () => Source(Entry(@"c:\Apps\Tool.exe", "工具")),
            [CatalogItem(@"C:\Apps\Tool.exe\")],
            Now);

        Assert.AreEqual(0, pending, "规范化 (TargetPath, Arguments) 与目录侧同源——大小写/尾分隔符差异互认。");
    }

    [TestMethod]
    public async Task Rescan_PrunesPendingEntriesThatEnteredTheCatalog()
    {
        var service = await LoadAsync(new VolatileLauncherDiscoveryStore(), []);
        await service.RescanAsync(() => Source(Entry(@"C:\Apps\Added.exe", "手动添加")), [], Now);
        Assert.AreEqual(1, service.PendingCount);

        var pending = await service.RescanAsync(
            () => Source(),
            [CatalogItem(@"C:\Apps\Added.exe")],
            Now);

        Assert.AreEqual(0, pending, "用户经 ＋ 菜单手动添加后，发现清单不再重复提示。");
    }

    [TestMethod]
    public async Task Dismissed_ItemsAreNotRediscovered()
    {
        var service = await LoadAsync(new VolatileLauncherDiscoveryStore(), []);
        await service.RescanAsync(() => Source(Entry(@"C:\Apps\Noise.exe", "噪声")), [], Now);
        await service.DismissAsync(service.PendingSnapshot());

        var pending = await service.RescanAsync(
            () => Source(Entry(@"C:\Apps\Noise.exe", "噪声")),
            [],
            Now);

        Assert.AreEqual(0, pending, "忽略是持久抑制记忆——重扫不重新入列。");
    }

    [TestMethod]
    public async Task Adopt_DoesNotWriteDismissed_RediscoveredAfterCatalogDelete()
    {
        var store = new VolatileLauncherDiscoveryStore();
        var service = await LoadAsync(store, []);
        await service.RescanAsync(() => Source(Entry(@"C:\Apps\X.exe", "X")), [], Now);
        await service.AdoptAsync(service.PendingSnapshot());
        Assert.AreEqual(0, service.PendingCount);

        // 采纳≠忽略：条目其后被从启动目录删除（未忽略过）→ 重扫重新发现。
        // 采纳路径的去重由「进入启动目录」这一事实承载，不旁写抑制记忆。
        var pending = await service.RescanAsync(() => Source(Entry(@"C:\Apps\X.exe", "X")), [], Now);
        Assert.AreEqual(1, pending, "采纳不写 dismissed——目录删除后重新可发现（忽略才持久抑制）。");
    }

    [TestMethod]
    public async Task Adopt_RemovesSelectedEntriesAndKeepsOthers()
    {
        var service = await LoadAsync(new VolatileLauncherDiscoveryStore(), []);
        await service.RescanAsync(
            () => Source(Entry(@"C:\Apps\A.exe", "A"), Entry(@"C:\Apps\B.exe", "B")),
            [],
            Now);

        var remaining = await service.AdoptAsync([Entry(@"C:\Apps\A.exe", "A")]);

        Assert.AreEqual(1, remaining, "采纳＝从 pending 移除，未选中条目保留。");
        Assert.AreEqual(@"C:\Apps\B.exe", service.PendingSnapshot().Single().TargetPath);
    }

    [TestMethod]
    public async Task DismissAll_ClearsPendingAndPersistsKeys()
    {
        var store = new VolatileLauncherDiscoveryStore();
        var service = await LoadAsync(store, []);
        await service.RescanAsync(
            () => Source(Entry(@"C:\Apps\A.exe", "A"), Entry(@"C:\Apps\B.exe", "B")),
            [],
            Now);

        var remaining = await service.DismissAllAsync();

        Assert.AreEqual(0, remaining, "全部忽略清空 pending。");
        var reloaded = await LoadAsync(store, []);
        Assert.AreEqual(0, reloaded.PendingCount, "忽略键随清单持久化。");
    }

    [TestMethod]
    public async Task Dismissed_CapacityEvictsOldestKeyFifo()
    {
        var service = await LoadAsync(new VolatileLauncherDiscoveryStore(), []);
        var first = Entry(@"C:\Apps\First.exe", "最旧");
        var batch = new List<LauncherDiscoveryEntry> { first };
        for (var index = 0; index < LauncherDiscoveryService.DismissedCapacity; index++)
        {
            batch.Add(Entry($@"C:\Apps\N{index}.exe"));
        }

        await service.RescanAsync(() => Source([.. batch]), [], Now);
        await service.DismissAllAsync();
        Assert.AreEqual(0, service.PendingCount);

        // 最旧键被驱逐后重新可发现（容量上限语义）。
        var rediscovered = await service.RescanAsync(() => Source(first), [], Now);
        Assert.AreEqual(1, rediscovered, "超过容量的最旧忽略键被 FIFO 驱逐，重新入列。");
    }

    [TestMethod]
    public async Task PendingChanged_RaisesAfterMutationsOnly()
    {
        var service = await LoadAsync(new VolatileLauncherDiscoveryStore(), []);
        var raised = 0;
        service.PendingChanged += (_, _) => raised++;

        await service.RescanAsync(() => Source(Entry(@"C:\Apps\A.exe", "A")), [], Now);
        Assert.AreEqual(1, raised, "新发现入列触发一次通知。");

        await service.RescanAsync(() => Source(Entry(@"C:\Apps\A.exe", "A")), [], Now);
        Assert.AreEqual(1, raised, "零变化重扫不触发通知（零写盘同源）。");
    }

    // ---- 持久化（launcher-discoveries.json） ----

    [TestMethod]
    public async Task Persistence_RoundTripsPendingAndDismissed()
    {
        using var directory = new TestDirectory();
        var store = CreateStore(directory);
        var service = await LoadAsync(store, []);
        await service.RescanAsync(
            () => Source(
                Entry(@"C:\Apps\Keep.exe", "保留待确认"),
                Entry(@"C:\Apps\Gone.exe", "被忽略"),
                new LauncherDiscoveryEntry(
                    LauncherKind.PackagedApp,
                    "App!Publisher",
                    null,
                    "打包应用",
                    Now)),
            [],
            Now);
        await service.DismissAsync([Entry(@"C:\Apps\Gone.exe", "被忽略")]);

        var reloaded = await LauncherDiscoveryService.LoadAsync(CreateStore(directory), _ => { });
        Assert.AreEqual(2, reloaded.PendingCount, "pending 跨进程往返。");
        Assert.IsTrue(reloaded.PendingSnapshot().Any(entry =>
            entry.Kind == LauncherKind.PackagedApp && entry.TargetPath == "App!Publisher"));
        Assert.AreEqual(0, reloaded.PendingSnapshot().Count(entry => entry.TargetPath == @"C:\Apps\Gone.exe"));

        // 忽略键往返后仍抑制重发现（Gone 不重新入列，既有两条保留）。
        var pending = await reloaded.RescanAsync(() => Source(Entry(@"C:\Apps\Gone.exe", "被忽略")), [], Now);
        Assert.AreEqual(2, pending, "被忽略项不再入列；既有待确认项不受影响。");
    }

    [TestMethod]
    public async Task Persistence_CorruptFileFallsBackAndPreservesSnapshot()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = CreateStore(directory);
        await store.SaveLauncherDiscoveriesAsync(new LauncherDiscoveryStoreData(
            LauncherDiscoveryStoreData.CurrentVersion,
            [Entry(@"C:\Apps\Kept.exe", "保留")],
            ["key"]));
        await store.SaveLauncherDiscoveriesAsync(new LauncherDiscoveryStoreData(
            LauncherDiscoveryStoreData.CurrentVersion,
            [Entry(@"C:\Apps\Kept.exe", "保留")],
            ["key"]));
        await File.WriteAllTextAsync(paths.LauncherDiscoveriesFile, "{ not valid json");

        var loaded = await CreateStore(directory).LoadLauncherDiscoveriesAsync();

        Assert.AreEqual(1, loaded.Pending.Count, "损坏主文件回落 .bak——恢复到上次成功保存的内容。");
        Assert.IsTrue(
            Directory.EnumerateFiles(directory.Root, "launcher-discoveries.json.corrupt-*").Any(),
            "损坏主文件保留 .corrupt-* 快照。");
    }

    [TestMethod]
    public async Task Persistence_UnknownVersionFallsBackToEmpty()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        await File.WriteAllTextAsync(
            paths.LauncherDiscoveriesFile,
            """{"version":99,"pending":[],"dismissed":[]}""");

        var loaded = await CreateStore(directory).LoadLauncherDiscoveriesAsync();

        Assert.AreEqual(0, loaded.Pending.Count, "未知版本回落空清单（派生数据可重建，无只读停用）。");
        Assert.AreEqual(LauncherDiscoveryStoreData.CurrentVersion, loaded.Version);
    }

    [TestMethod]
    public async Task Persistence_LoadCleansDuplicatesAndInvalidEntries()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        await File.WriteAllTextAsync(
            paths.LauncherDiscoveriesFile,
            """
            {
              "version": 1,
              "pending": [
                { "kind": "Application", "targetPath": "C:\\Apps\\A.exe", "displayName": "甲", "firstSeenAtUtc": "2026-10-23T08:00:00Z" },
                { "kind": "Application", "targetPath": "c:\\apps\\a.exe", "displayName": "甲变体", "firstSeenAtUtc": "2026-10-23T08:00:00Z" },
                { "kind": "Application", "targetPath": "  ", "displayName": "空目标", "firstSeenAtUtc": "2026-10-23T08:00:00Z" },
                { "kind": "NotAKind", "targetPath": "C:\\Apps\\B.exe", "displayName": "未知类型", "firstSeenAtUtc": "2026-10-23T08:00:00Z" }
              ],
              "dismissedKeys": ["k1", "k1", " "]
            }
            """);

        var loaded = await CreateStore(directory).LoadLauncherDiscoveriesAsync();

        Assert.AreEqual(1, loaded.Pending.Count, "规范化键去重 + 空目标/未知 Kind 丢条。");
        Assert.AreEqual(1, loaded.DismissedKeys.Count, "忽略键去重。");
    }

    [TestMethod]
    public async Task SaveFailure_RollsBackAndShowsStatus()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = CreateStore(directory);
        await store.SaveLauncherDiscoveriesAsync(LauncherDiscoveryStoreData.Empty);
        // 用只写目录策略模拟保存失败：替换 writer 为总是抛 IO 异常的实现。
        var failing = new LocalStore(paths, new ThrowingWriter());
        var statuses = new List<string>();
        var service = await LauncherDiscoveryService.LoadAsync(failing, statuses.Add);

        var pending = await service.RescanAsync(() => Source(Entry(@"C:\Apps\A.exe", "A")), [], Now);

        Assert.AreEqual(0, pending, "保存失败不落内存基准。");
        Assert.AreEqual(1, statuses.Count, "保存失败状态条提示一次。");
        Assert.AreEqual("发现清单未保存，已还原。", statuses.Single());
        var onDisk = await CreateStore(directory).LoadLauncherDiscoveriesAsync();
        Assert.AreEqual(0, onDisk.Pending.Count, "磁盘未被失败突变污染。");
    }

    private sealed class ThrowingWriter : IAtomicTextWriter
    {
        public Task WriteAsync(string path, string content, CancellationToken cancellationToken = default) =>
            throw new IOException("disk full");
    }
}
