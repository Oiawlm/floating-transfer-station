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
}
