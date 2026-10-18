using System.Collections.Specialized;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 启动项目录串行化协议（#30，Issue #162）：信号量包住「生成不可变快照 → 保存 →
/// 成功发布 / 失败还原内存原状」全过程；整集合读写；添加去重（规范化
/// (TargetPath, Arguments)）；200 可执行上限；未知版本只读停用（绝不以空集合覆盖
/// 回写）；重命名/移除/重排/图标戳走同一协议。
/// </summary>
[TestClass]
public sealed class LauncherCatalogTests
{
    private static LauncherItem Item(string target = @"C:\Apps\A.exe", string? arguments = null) =>
        new(Guid.NewGuid(), LauncherKind.Application, target, "应用", DateTimeOffset.UtcNow, arguments);

    private sealed class FailingLauncherStore : ILauncherStore
    {
        public int SaveCount { get; private set; }

        public Task<LauncherStoreData> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(LauncherStoreData.Empty);

        public Task SaveAsync(LauncherStoreData data, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            throw new IOException("Injected launcher save failure.");
        }
    }

    private sealed class RecordingLauncherStore : ILauncherStore
    {
        public List<LauncherStoreData> Saved { get; } = [];
        public Func<LauncherStoreData, CancellationToken, Task>? Respond { get; set; }

        public Task<LauncherStoreData> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(LauncherStoreData.Empty);

        public Task SaveAsync(LauncherStoreData data, CancellationToken cancellationToken = default)
        {
            Saved.Add(data);
            return Respond?.Invoke(data, cancellationToken) ?? Task.CompletedTask;
        }
    }

    [TestMethod]
    public async Task Add_PublishesItemsAndPersistsSnapshot()
    {
        using var directory = new TestDirectory();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var messages = new List<string>();
        var catalog = await LauncherCatalog.LoadAsync(store, messages.Add);

        var outcome = await catalog.AddRangeAsync([Item(), Item(@"C:\Apps\B.exe")]);

        Assert.AreEqual(2, outcome.AddedCount);
        Assert.AreEqual(2, catalog.Items.Count);
        var persisted = await store.LoadAsync();
        Assert.AreEqual(2, persisted.Items.Count);
        Assert.IsEmpty(messages);
    }

    [TestMethod]
    public async Task Add_DeduplicatesByNormalizedTargetAndArguments()
    {
        using var directory = new TestDirectory();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var catalog = await LauncherCatalog.LoadAsync(store, _ => { });
        var first = Item(@"C:\Apps\A.exe");
        await catalog.AddRangeAsync([first]);

        var outcome = await catalog.AddRangeAsync([
            Item(@"c:\apps\a.exe"),
            Item(@"C:\Apps\A.exe", "--other"),
            Item(@"C:\Apps\B.exe")
        ]);

        Assert.AreEqual(2, outcome.AddedCount, "大小写差异归一去重；参数不同算不同条目（另两条新增）。");
        Assert.AreSame(first, outcome.Duplicates.Single(), "命中返回已有条目供滚动+脉冲。");
        Assert.AreEqual(3, catalog.Items.Count);
    }

    [TestMethod]
    public async Task Add_RejectsBeyondTwoHundredItems()
    {
        using var directory = new TestDirectory();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var catalog = await LauncherCatalog.LoadAsync(store, _ => { });

        var batch = Enumerable.Range(0, LauncherCatalog.MaxItems + 3)
            .Select(index => Item($@"C:\Apps\No{index}.exe"))
            .ToArray();
        var outcome = await catalog.AddRangeAsync(batch);

        Assert.AreEqual(LauncherCatalog.MaxItems, outcome.AddedCount, "恰到 200 上限。");
        Assert.AreEqual(3, outcome.OverflowSkippedCount, "超额部分跳过并计数。");
        Assert.AreEqual(LauncherCatalog.MaxItems, catalog.Items.Count);
    }

    [TestMethod]
    public async Task SaveFailure_RestoresMemoryAndSurfacesStatus()
    {
        var store = new FailingLauncherStore();
        var messages = new List<string>();
        var catalog = await LauncherCatalog.LoadAsync(store, messages.Add);

        var outcome = await catalog.AddRangeAsync([Item()]);

        Assert.AreEqual(0, outcome.AddedCount);
        Assert.AreEqual(0, catalog.Items.Count, "失败还原内存原状（不发布）。");
        CollectionAssert.AreEqual(new[] { "启动项未保存，已还原。" }, messages);
    }

    [TestMethod]
    public async Task Rename_SavesAndPublishes_RollbackOnFailure()
    {
        var store = new RecordingLauncherStore();
        var catalog = await LauncherCatalog.LoadAsync(store, _ => { });
        var item = Item();
        await catalog.AddRangeAsync([item]);

        Assert.IsTrue(await catalog.RenameAsync(item.Id, "新名字"));
        Assert.AreEqual("新名字", catalog.Items.Single().DisplayName);
        Assert.AreEqual("新名字", store.Saved.Last().Items.Single().DisplayName);

        store.Respond = (_, _) => throw new IOException("fail");
        Assert.IsFalse(await catalog.RenameAsync(item.Id, "不该出现"));
        Assert.AreEqual("新名字", catalog.Items.Single().DisplayName, "保存失败还原原名。");
    }

    [TestMethod]
    public async Task Remove_ReturnsItemAndIndexForUndoToast()
    {
        var store = new RecordingLauncherStore();
        var catalog = await LauncherCatalog.LoadAsync(store, _ => { });
        var first = Item(@"C:\Apps\A.exe");
        var second = Item(@"C:\Apps\B.exe");
        var third = Item(@"C:\Apps\C.exe");
        await catalog.AddRangeAsync([first, second, third]);

        var outcome = await catalog.RemoveAsync(second.Id);

        Assert.IsNotNull(outcome);
        Assert.AreSame(second, outcome.Item);
        Assert.AreEqual(1, outcome.Index, "原下标供撤销提示条按 min(原下标, 当前数) 重插。");
        CollectionAssert.AreEqual(
            new[] { first.Id, third.Id },
            catalog.Items.Select(entry => entry.Id).ToArray());
    }

    [TestMethod]
    public async Task Move_ReordersAndPersistsOrderOnlyTruth()
    {
        var store = new RecordingLauncherStore();
        var catalog = await LauncherCatalog.LoadAsync(store, _ => { });
        var first = Item(@"C:\Apps\A.exe");
        var second = Item(@"C:\Apps\B.exe");
        var third = Item(@"C:\Apps\C.exe");
        await catalog.AddRangeAsync([first, second, third]);

        Assert.IsTrue(await catalog.MoveAsync(first.Id, 2));

        CollectionAssert.AreEqual(
            new[] { second.Id, third.Id, first.Id },
            catalog.Items.Select(entry => entry.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { second.Id, third.Id, first.Id },
            store.Saved.Last().Items.Select(entry => entry.Id).ToArray());
    }

    [TestMethod]
    public async Task LoadFromNewerVersion_EntersReadOnlyAndNeverOverwrites()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        Directory.CreateDirectory(paths.DataDirectory);
        await File.WriteAllTextAsync(paths.LaunchersFile, """
            { "version": 99, "items": [] }
            """);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var messages = new List<string>();

        var catalog = await LauncherCatalog.LoadAsync(store, messages.Add);
        Assert.IsTrue(catalog.IsReadOnlyFromNewerVersion);
        Assert.AreEqual(0, catalog.Items.Count, "显示空列表（数据保留在原文件）。");

        var outcome = await catalog.AddRangeAsync([Item()]);

        Assert.AreEqual(0, outcome.AddedCount, "只读停用下拒绝写路径。");
        Assert.AreEqual(
            "启动项数据来自更新的版本，已暂停修改。",
            messages.Last(),
            "状态条提示只读停用。");
        var json = await File.ReadAllTextAsync(paths.LaunchersFile);
        StringAssert.Contains(json, "\"version\": 99", "只读停用绝不以空集合覆盖回写原文件。");
    }

    [TestMethod]
    public async Task UpdateIconStamp_SavesThroughSameProtocol()
    {
        var store = new RecordingLauncherStore();
        var catalog = await LauncherCatalog.LoadAsync(store, _ => { });
        var item = Item();
        await catalog.AddRangeAsync([item]);

        Assert.IsTrue(await catalog.UpdateIconStampAsync(item.Id, "abc123def456"));
        Assert.AreEqual("abc123def456", catalog.Items.Single().IconStamp);
        Assert.AreEqual("abc123def456", store.Saved.Last().Items.Single().IconStamp);
    }

    // ---- 乐观发布＋最小操作（#30 修订，Issue #176）----

    /// <summary>记录集合通知形态（Move/Reset/Add/Remove/Replace 计数）。</summary>
    private static (int Move, int Reset, int Add, int Remove, int Replace) TrackChanges(
        LauncherCatalog catalog,
        Action act)
    {
        var (move, reset, add, remove, replace) = (0, 0, 0, 0, 0);
        NotifyCollectionChangedEventHandler onChange = (_, args) =>
        {
            switch (args.Action)
            {
                case NotifyCollectionChangedAction.Move: move++; break;
                case NotifyCollectionChangedAction.Reset: reset++; break;
                case NotifyCollectionChangedAction.Add: add++; break;
                case NotifyCollectionChangedAction.Remove: remove++; break;
                case NotifyCollectionChangedAction.Replace: replace++; break;
            }
        };
        catalog.Items.CollectionChanged += onChange;
        try
        {
            act();
        }
        finally
        {
            catalog.Items.CollectionChanged -= onChange;
        }

        return (move, reset, add, remove, replace);
    }

    [TestMethod]
    public async Task Move_PublishesExactlyOneMoveNotification()
    {
        var store = new RecordingLauncherStore();
        var catalog = await LauncherCatalog.LoadAsync(store, _ => { });
        var first = Item(@"C:\Apps\A.exe");
        var second = Item(@"C:\Apps\B.exe");
        var third = Item(@"C:\Apps\C.exe");
        await catalog.AddRangeAsync([first, second, third]);

        var changes = TrackChanges(catalog, () =>
            catalog.MoveAsync(first.Id, 2).GetAwaiter().GetResult());

        Assert.AreEqual(1, changes.Move, "重排发布恰一条 Move 通知。");
        Assert.AreEqual(0, changes.Reset, "无 Reset（全量重建退役为装载专用）。");
        Assert.AreEqual(0, changes.Add);
        Assert.AreEqual(0, changes.Remove);
    }

    [TestMethod]
    public async Task RenameAndIconStamp_PublishNoCollectionChange()
    {
        var store = new RecordingLauncherStore();
        var catalog = await LauncherCatalog.LoadAsync(store, _ => { });
        var item = Item();
        await catalog.AddRangeAsync([item]);

        var changes = TrackChanges(catalog, () =>
        {
            catalog.RenameAsync(item.Id, "新名").GetAwaiter().GetResult();
            catalog.UpdateIconStampAsync(item.Id, "stamp").GetAwaiter().GetResult();
        });

        Assert.AreEqual(0, changes.Move + changes.Reset + changes.Add + changes.Remove + changes.Replace,
            "改名/图标戳是实例属性通知，无集合变更。");
        Assert.AreEqual("新名", catalog.Items.Single().DisplayName);
        Assert.AreEqual("stamp", catalog.Items.Single().IconStamp);
    }

    [TestMethod]
    public async Task Move_AppliesOptimisticallyWhileSaveIsInFlight()
    {
        var store = new RecordingLauncherStore();
        var catalog = await LauncherCatalog.LoadAsync(store, _ => { });
        var first = Item(@"C:\Apps\A.exe");
        var second = Item(@"C:\Apps\B.exe");
        var third = Item(@"C:\Apps\C.exe");
        await catalog.AddRangeAsync([first, second, third]);

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Respond = (_, _) => held.Task;
        var moveTask = catalog.MoveAsync(first.Id, 2);
        await Task.Delay(50);

        Assert.IsFalse(moveTask.IsCompleted, "前置：保存仍在途。");
        CollectionAssert.AreEqual(
            new[] { second.Id, third.Id, first.Id },
            catalog.Items.Select(entry => entry.Id).ToArray(),
            "保存挂起期间 UI 已新序（乐观上屏，发布不再被落盘往返门住）。");

        held.SetResult();
        Assert.IsTrue(await moveTask);
        CollectionAssert.AreEqual(
            new[] { second.Id, third.Id, first.Id },
            store.Saved.Last().Items.Select(entry => entry.Id).ToArray(),
            "持久化以新序落盘。");
    }

    [TestMethod]
    public async Task MoveSaveFailure_RollsBackByReverseMove()
    {
        var store = new RecordingLauncherStore();
        var messages = new List<string>();
        var catalog = await LauncherCatalog.LoadAsync(store, messages.Add);
        var first = Item(@"C:\Apps\A.exe");
        var second = Item(@"C:\Apps\B.exe");
        var third = Item(@"C:\Apps\C.exe");
        await catalog.AddRangeAsync([first, second, third]);

        store.Respond = (_, _) => throw new IOException("fail");
        var changes = TrackChanges(catalog, () =>
            catalog.MoveAsync(first.Id, 2).GetAwaiter().GetResult());

        CollectionAssert.AreEqual(
            new[] { first.Id, second.Id, third.Id },
            catalog.Items.Select(entry => entry.Id).ToArray(),
            "保存失败按逆操作回滚（反向 Move，不做全量重建）。");
        Assert.AreEqual(2, changes.Move, "失败回滚=第二条 Move 通知。");
        Assert.AreEqual(0, changes.Reset, "回滚不触发 Reset（用户最差时刻不做全量重建）。");
        CollectionAssert.AreEqual(new[] { "启动项未保存，已还原。" }, messages);
    }

    [TestMethod]
    public async Task MutationDuringSaveInFlight_WaitsForGate_NotClobberedByFailure()
    {
        // T7 评审 BLOCKER 的守护：门横跨「应用→保存→失败回滚」全程——A 的保存
        // 在途时 B 无法开始，不存在「A 失败回滚踩掉已应用的 B」的交错；A 失败
        // 回滚后 B 在原序上照常执行。
        var store = new RecordingLauncherStore();
        var catalog = await LauncherCatalog.LoadAsync(store, _ => { });
        var first = Item(@"C:\Apps\A.exe");
        var second = Item(@"C:\Apps\B.exe");
        var third = Item(@"C:\Apps\C.exe");
        await catalog.AddRangeAsync([first, second, third]);

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveAttempts = 0;
        store.Respond = (_, _) => ++saveAttempts == 1 ? held.Task : Task.CompletedTask;
        var moveTask = catalog.MoveAsync(first.Id, 2);
        await Task.Delay(50);
        var removeTask = catalog.RemoveAsync(second.Id);
        await Task.Delay(50);

        Assert.IsFalse(removeTask.IsCompleted, "B（删除）在 A 的保存往返期间排队等门。");
        CollectionAssert.AreEqual(
            new[] { second.Id, third.Id, first.Id },
            catalog.Items.Select(entry => entry.Id).ToArray(),
            "期间集合=A 的乐观序（B 尚未应用）。");

        held.SetException(new IOException("fail"));
        Assert.IsFalse(await moveTask, "A 保存失败。");
        var outcome = await removeTask;
        Assert.IsNotNull(outcome, "A 失败回滚后 B 在原序上执行。");
        CollectionAssert.AreEqual(
            new[] { first.Id, third.Id },
            catalog.Items.Select(entry => entry.Id).ToArray(),
            "终态：A 已回滚＋B 已应用（互不踩踏）。");
    }

    [TestMethod]
    public async Task MoveBy_ResolvesTargetInsideGateAgainstAuthoritativeOrder()
    {
        // 菜单上下移的相对语义：目标下标在门内权威序上解析（Issue #176 T7 收紧）；
        // 端点 delta 出界=同序早退（无写盘）。
        var store = new RecordingLauncherStore();
        var catalog = await LauncherCatalog.LoadAsync(store, _ => { });
        var first = Item(@"C:\Apps\A.exe");
        var second = Item(@"C:\Apps\B.exe");
        var third = Item(@"C:\Apps\C.exe");
        await catalog.AddRangeAsync([first, second, third]);
        var savesBefore = store.Saved.Count;

        Assert.IsTrue(await catalog.MoveByAsync(third.Id, -1), "third 上移一位。");
        CollectionAssert.AreEqual(
            new[] { first.Id, third.Id, second.Id },
            catalog.Items.Select(entry => entry.Id).ToArray());

        Assert.IsFalse(await catalog.MoveByAsync(first.Id, -1), "首项上移=同序早退。");
        Assert.AreEqual(
            savesBefore + 1,
            store.Saved.Count,
            "端点早退零写盘。");
    }

    [TestMethod]
    public async Task RemoveSaveFailure_ReinsertsAtOriginalIndex()
    {
        var store = new RecordingLauncherStore();
        var messages = new List<string>();
        var catalog = await LauncherCatalog.LoadAsync(store, messages.Add);
        var first = Item(@"C:\Apps\A.exe");
        var second = Item(@"C:\Apps\B.exe");
        var third = Item(@"C:\Apps\C.exe");
        await catalog.AddRangeAsync([first, second, third]);

        store.Respond = (_, _) => throw new IOException("fail");
        var changes = TrackChanges(catalog, () =>
            catalog.RemoveAsync(second.Id).GetAwaiter().GetResult());

        CollectionAssert.AreEqual(
            new[] { first.Id, second.Id, third.Id },
            catalog.Items.Select(entry => entry.Id).ToArray(),
            "删除保存失败：按原下标重插回滚。");
        Assert.AreEqual(1, changes.Remove, "乐观删除=一条 Remove 通知。");
        Assert.AreEqual(1, changes.Add, "失败回滚=一条 Insert(Add) 通知。");
        Assert.AreEqual(0, changes.Reset);
        CollectionAssert.AreEqual(new[] { "启动项未保存，已还原。" }, messages);
    }

    // ---- Url 条目（#30 修订，Issue #179）：创建入口归一化产出的规范形经同一目录协议 ----

    [TestMethod]
    public async Task Add_UrlItems_PublishesAndPersists()
    {
        using var directory = new TestDirectory();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var messages = new List<string>();
        var catalog = await LauncherCatalog.LoadAsync(store, messages.Add);
        var url = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://github.com/", "github.com", DateTimeOffset.UtcNow);

        var outcome = await catalog.AddRangeAsync([url]);

        Assert.AreEqual(1, outcome.AddedCount);
        Assert.AreEqual(LauncherKind.Url, catalog.Items.Single().Kind, "Url 条目按一等条目落位。");
        var persisted = await store.LoadAsync();
        Assert.AreEqual("https://github.com/", persisted.Items.Single().TargetPath, "Url 条目随 launchers.json 持久化。");
        Assert.IsEmpty(messages);
    }

    [TestMethod]
    public async Task Add_SameCanonicalUrl_Deduplicates()
    {
        using var directory = new TestDirectory();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var catalog = await LauncherCatalog.LoadAsync(store, _ => { });
        var first = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://github.com/", "github.com", DateTimeOffset.UtcNow);
        await catalog.AddRangeAsync([first]);

        var outcome = await catalog.AddRangeAsync([new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://github.com/", "github.com", DateTimeOffset.UtcNow)]);

        Assert.AreEqual(0, outcome.AddedCount, "同规范形 Url 去重（大小写/空白差异由创建入口归一化收敛，见 LauncherUrlInputTests）。");
        Assert.AreEqual(1, outcome.Duplicates.Count);
        Assert.AreEqual(1, catalog.Items.Count);
        Assert.AreSame(first, outcome.Duplicates[0], "命中项回传供滚动+脉冲。");
    }

    [TestMethod]
    public async Task Add_MixedKinds_AreIndependentEntries()
    {
        using var directory = new TestDirectory();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var catalog = await LauncherCatalog.LoadAsync(store, _ => { });
        var application = Item(@"C:\Apps\A.exe");
        var packaged = new LauncherItem(
            Guid.NewGuid(), LauncherKind.PackagedApp, "App!Publisher", "打包应用", DateTimeOffset.UtcNow);
        var url = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://example.com/", "example.com", DateTimeOffset.UtcNow);

        var firstBatch = await catalog.AddRangeAsync([application, packaged, url]);
        var secondBatch = await catalog.AddRangeAsync([application, packaged, url]);

        Assert.AreEqual(3, firstBatch.AddedCount, "三种 Kind 互不干扰各自落位。");
        Assert.AreEqual(0, secondBatch.AddedCount, "同目标重复按 DedupKey 各自去重。");
        Assert.AreEqual(3, secondBatch.Duplicates.Count);
        Assert.AreEqual(3, catalog.Items.Count);
    }

    [TestMethod]
    public async Task Add_UrlItems_RespectMaxItemsLimit()
    {
        using var directory = new TestDirectory();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var catalog = await LauncherCatalog.LoadAsync(store, _ => { });
        var fill = Enumerable.Range(0, LauncherCatalog.MaxItems)
            .Select(index => new LauncherItem(
                Guid.NewGuid(), LauncherKind.Url, $"https://example.com/{index}/", $"example{index}", DateTimeOffset.UtcNow));
        await catalog.AddRangeAsync(fill);

        var outcome = await catalog.AddRangeAsync([new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://overflow.example.com/", "overflow", DateTimeOffset.UtcNow)]);

        Assert.AreEqual(0, outcome.AddedCount);
        Assert.AreEqual(1, outcome.OverflowSkippedCount, "200 上限对 Url 条目同语义。");
        Assert.AreEqual(LauncherCatalog.MaxItems, catalog.Items.Count);
    }

    // ---- 编辑目标（#30 修订 2026-10-18，Issue #186 D7）：目标创建后可改——走既有
    // 串行突变协议（乐观应用→保存→失败回滚），目标变更刷新 IconStamp 触发图标
    // 自动重取，用户名保留，规范化重复拒绝。----

    [TestMethod]
    public async Task UpdateTarget_ChangesKindTargetAndArgs_KeepsName_Persists()
    {
        var store = new RecordingLauncherStore();
        var catalog = await LauncherCatalog.LoadAsync(store, _ => { });
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://old.example.com/", "我的站点", DateTimeOffset.UtcNow);
        await catalog.AddRangeAsync([item]);

        Assert.AreEqual(LauncherUpdateTargetOutcome.Updated, await catalog.UpdateTargetAsync(
            item.Id,
            LauncherKind.Url,
            "https://new.example.com/path"));
        var live = catalog.Items.Single();
        Assert.AreEqual(LauncherKind.Url, live.Kind);
        Assert.AreEqual("https://new.example.com/path", live.TargetPath);
        Assert.AreEqual("我的站点", live.DisplayName, "用户名保留。");
        Assert.AreEqual("https://new.example.com/path", store.Saved.Last().Items.Single().TargetPath, "目标持久化。");
    }

    [TestMethod]
    public async Task UpdateTarget_RefreshesIconStampForAutomaticIconRefetch()
    {
        var store = new RecordingLauncherStore();
        var catalog = await LauncherCatalog.LoadAsync(store, _ => { });
        var item = new LauncherItem(
            Guid.NewGuid(),
            LauncherKind.Application,
            @"C:\Apps\A.exe",
            "应用",
            DateTimeOffset.UtcNow,
            iconStamp: "old-stamp");
        await catalog.AddRangeAsync([item]);

        Assert.AreEqual(LauncherUpdateTargetOutcome.Updated, await catalog.UpdateTargetAsync(item.Id, LauncherKind.Application, @"C:AppsB.exe"));
        Assert.IsNull(catalog.Items.Single().IconStamp, "目标变更→IconStamp 置空→图标管线自动重取。");
    }

    [TestMethod]
    public async Task UpdateTarget_DuplicateCanonicalTarget_IsRejectedWithoutMutation()
    {
        var store = new RecordingLauncherStore();
        var catalog = await LauncherCatalog.LoadAsync(store, _ => { });
        var first = Item(@"C:\Apps\A.exe");
        var second = Item(@"C:\Apps\B.exe");
        await catalog.AddRangeAsync([first, second]);

        var rejected = await catalog.UpdateTargetAsync(second.Id, LauncherKind.Application, @"c:\apps\a.exe");
        Assert.AreEqual(LauncherUpdateTargetOutcome.Duplicate, rejected, "规范化后与既有条目重复→拒绝。");
        Assert.AreEqual(@"C:\Apps\B.exe", catalog.Items.Single(entry => entry.Id == second.Id).TargetPath, "拒绝不突变。");
    }

    [TestMethod]
    public async Task UpdateTarget_SameTarget_NoOpSucceeds()
    {
        var store = new RecordingLauncherStore();
        var catalog = await LauncherCatalog.LoadAsync(store, _ => { });
        var item = Item(@"C:\Apps\A.exe");
        await catalog.AddRangeAsync([item]);
        var savesBefore = store.Saved.Count;

        Assert.AreEqual(LauncherUpdateTargetOutcome.Updated, await catalog.UpdateTargetAsync(item.Id, LauncherKind.Application, @"C:\Apps\A.exe"));
        Assert.AreEqual(savesBefore, store.Saved.Count, "目标未变=无写盘。");
    }

    [TestMethod]
    public async Task UpdateTarget_SaveFailure_RestoresTargetKindArgsAndStamp()
    {
        var store = new RecordingLauncherStore();
        var catalog = await LauncherCatalog.LoadAsync(store, _ => { });
        var item = new LauncherItem(
            Guid.NewGuid(),
            LauncherKind.Application,
            @"C:\Apps\A.exe",
            "应用",
            DateTimeOffset.UtcNow,
            arguments: "/x",
            workingDirectory: @"C:\Apps",
            iconStamp: "stamp-1");
        await catalog.AddRangeAsync([item]);

        store.Respond = (_, _) => throw new IOException("fail");
        Assert.AreEqual(LauncherUpdateTargetOutcome.Failed, await catalog.UpdateTargetAsync(
            item.Id,
            LauncherKind.Application,
            @"C:\Apps\B.exe",
            "/y",
            @"C:\Other"));
        var live = catalog.Items.Single();
        Assert.AreEqual(@"C:\Apps\A.exe", live.TargetPath, "保存失败还原目标。");
        Assert.AreEqual("/x", live.Arguments, "还原参数。");
        Assert.AreEqual(@"C:\Apps", live.WorkingDirectory, "还原工作目录。");
        Assert.AreEqual("stamp-1", live.IconStamp, "还原图标戳。");
    }

    [TestMethod]
    public async Task UpdateTarget_UnknownIdOrReadOnly_Fails()
    {
        var store = new RecordingLauncherStore();
        var catalog = await LauncherCatalog.LoadAsync(store, _ => { });
        Assert.AreEqual(LauncherUpdateTargetOutcome.Failed, await catalog.UpdateTargetAsync(Guid.NewGuid(), LauncherKind.Url, "https://x.example.com/"));
    }
}
