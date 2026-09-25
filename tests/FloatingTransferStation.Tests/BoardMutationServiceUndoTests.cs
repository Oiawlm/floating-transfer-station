using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 会话级撤销最近删除(设计草案 2026-09-25 切片1)的回归:
/// 锚点插回不回退删除后的其他改动;图片文件按撤销栈生命周期清理。
/// </summary>
[TestClass]
public sealed class BoardMutationServiceUndoTests
{
    [TestMethod]
    public async Task UndoLastDelete_EmptyStackReturnsFalseWithStatusAndNoSave()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("保留");
        var store = new MutationStore(directory.Root);
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        var undone = await service.UndoLastDeleteAsync();

        Assert.IsFalse(undone);
        Assert.AreEqual(0, store.SaveCount);
        CollectionAssert.AreEqual(new[] { "没有可撤销的删除。" }, messages);
    }

    [TestMethod]
    public async Task UndoLastDelete_RestoresExactLayoutWhenNothingElseChanged()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var first = board.AddText("第一条");
        var second = board.AddText("第二条");
        var third = board.AddText("第三条");
        board.SetPinnedMany([first.Id], true);
        var originalIds = board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray();
        var store = new MutationStore(directory.Root);
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        Assert.IsTrue(await service.DeleteManyAsync([second.Id]));
        Assert.IsTrue(await service.UndoLastDeleteAsync());

        CollectionAssert.AreEqual(
            originalIds,
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray(),
            "无并发改动时撤销应精确回到删除前的布局(含置顶归属)。");
        Assert.IsTrue(board.Items(BoardCategory.Inbox).Single(item => item.Id == first.Id).IsPinned);
        Assert.AreEqual(2, store.SaveCount);
        CollectionAssert.AreEqual(new[] { "已恢复最近删除的内容。" }, messages);
        Assert.AreEqual(0, service.PendingUndoDeleteCount);
    }

    [TestMethod]
    public async Task UndoLastDelete_DoesNotRevertItemsAddedAfterTheDelete()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var older = board.AddText("较早");
        var deleted = board.AddText("被删");
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        Assert.IsTrue(await service.DeleteManyAsync([deleted.Id]));
        var newer = board.AddText("删除后新收集");

        Assert.IsTrue(await service.UndoLastDeleteAsync());

        var current = board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray();
        CollectionAssert.AreEqual(new[] { deleted.Id, newer.Id, older.Id }, current,
            "锚点插回:恢复内容回到删除时的原位(顶部),不覆盖删除后新增的内容。");
    }

    [TestMethod]
    public async Task UndoLastDelete_BatchKeepsSourceOrderAcrossCategories()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var inboxA = board.AddText("收件箱A");
        var promptA = board.AddText("提示词A", BoardCategory.Prompt);
        var inboxB = board.AddText("收件箱B");
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        Assert.IsTrue(await service.DeleteManyAsync([inboxA.Id, inboxB.Id, promptA.Id]));
        Assert.IsTrue(await service.UndoLastDeleteAsync());

        CollectionAssert.AreEqual(
            new[] { inboxB.Id, inboxA.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray(),
            "批内保持删除时的显示顺序(新增置顶,故 B 在 A 前)。");
        CollectionAssert.AreEqual(
            new[] { promptA.Id },
            board.Items(BoardCategory.Prompt).Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public async Task UndoLastDelete_FallsBackToZoneTopWhenAnchorChangedZone()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var normalAnchor = board.AddText("普通锚点");
        var deleted = board.AddText("被删的普通项");
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        Assert.IsTrue(await service.DeleteManyAsync([deleted.Id]));
        // 删除后用户把锚点置顶:锚点换区,恢复项不得落进置顶区。
        board.SetPinnedMany([normalAnchor.Id], true);

        Assert.IsTrue(await service.UndoLastDeleteAsync());

        var current = board.Items(BoardCategory.Inbox);
        Assert.AreEqual(2, current.Count);
        Assert.IsTrue(current[0].IsPinned);
        Assert.AreEqual(normalAnchor.Id, current[0].Id);
        Assert.AreEqual(deleted.Id, current[1].Id);
        Assert.IsFalse(current[1].IsPinned, "锚点已变区时应回退到普通区顶部,不得混入置顶区。");
    }

    [TestMethod]
    public async Task UndoLastDelete_SaveFailureKeepsItemsDeletedAndReturnsFalse()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("将被删除");
        var kept = board.AddText("保留");
        var store = new MutationStore(directory.Root);
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        Assert.IsTrue(await service.DeleteManyAsync([item.Id]));
        var afterDelete = board.Items(BoardCategory.Inbox).Select(other => other.Id).ToArray();
        CollectionAssert.AreEqual(new[] { kept.Id }, afterDelete);
        store.FailSave = true;

        var undone = await service.UndoLastDeleteAsync();

        Assert.IsFalse(undone);
        CollectionAssert.AreEqual(afterDelete, board.Items(BoardCategory.Inbox).Select(other => other.Id).ToArray(),
            "撤销保存失败必须回到删除后的状态。");
        CollectionAssert.AreEqual(new[] { "撤销未保存，内容保持删除状态。" }, messages);
    }

    [TestMethod]
    public async Task UndoStack_EvictsOldestBatchBeyondTwentyAndCleansItsImages()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var oldestPath = Path.Combine(directory.Root, "oldest.png");
        await File.WriteAllBytesAsync(oldestPath, [0x89, 0x50, 0x4E, 0x47]);
        var oldest = board.AddImage(Guid.NewGuid(), "images/oldest.png", oldestPath);
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        Assert.IsTrue(await service.DeleteManyAsync([oldest.Id]));
        for (var index = 0; index < 20; index++)
        {
            var item = board.AddText($"批量{index}");
            Assert.IsTrue(await service.DeleteManyAsync([item.Id]));
        }

        Assert.AreEqual(20, service.PendingUndoDeleteCount, "栈容量 20:最旧批次被驱逐。");
        Assert.IsFalse(File.Exists(oldestPath), "被驱逐批次的图片文件应立即清理。");
        Assert.AreEqual(1, store.DeleteCount);
    }

    [TestMethod]
    public async Task UndoLastDelete_ClearCategoryIsUndoableAsOneUnit()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var first = board.AddText("一", BoardCategory.Reference);
        var pinned = board.AddText("二", BoardCategory.Reference);
        var last = board.AddText("三", BoardCategory.Reference);
        board.SetPinnedMany([pinned.Id], true);
        var otherCategory = board.AddText("其他分类", BoardCategory.Prompt);
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        Assert.IsTrue(await service.ClearCategoryAsync(BoardCategory.Reference));
        Assert.IsTrue(await service.UndoLastDeleteAsync());

        var current = board.Items(BoardCategory.Reference).Select(item => item.Id).ToArray();
        CollectionAssert.AreEqual(new[] { pinned.Id, last.Id, first.Id }, current,
            "清空撤销按区语义恢复:置顶在前,普通区保持删除时的显示顺序(新增置顶)。");
        Assert.AreSame(otherCategory, board.Items(BoardCategory.Prompt).Single());
    }

    [TestMethod]
    public async Task UndoLastDelete_ImageFileSurvivesUntilUndoEntryConsumed()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var imagePath = Path.Combine(directory.Root, "keep-for-undo.png");
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4E, 0x47]);
        var image = board.AddImage(Guid.NewGuid(), "images/keep-for-undo.png", imagePath);
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        Assert.IsTrue(await service.DeleteManyAsync([image.Id]));
        Assert.IsTrue(File.Exists(imagePath));

        Assert.IsTrue(await service.UndoLastDeleteAsync());

        Assert.IsTrue(File.Exists(imagePath), "撤销恢复后条目回归看板,文件必须仍然存在。");
        Assert.AreSame(image, board.Items(BoardCategory.Inbox).Single());
    }

    private sealed class MutationStore : IBoardStore
    {
        public MutationStore(string root) => ImagesDirectory = Path.Combine(root, "images");

        public bool FailSave { get; set; }
        public bool FailDelete { get; set; }
        public int SaveCount { get; private set; }
        public int DeleteCount { get; private set; }
        public BoardSnapshot? LastPersistedSnapshot { get; private set; }
        public string ImagesDirectory { get; }

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public Task SaveBoardAsync(BoardSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            if (FailSave)
            {
                throw new IOException("Injected save failure.");
            }

            SaveCount++;
            LastPersistedSnapshot = snapshot;
            return Task.CompletedTask;
        }

        public Task<WindowSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(WindowSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool TryDeleteImage(string? absolutePath)
        {
            DeleteCount++;
            if (FailDelete || string.IsNullOrWhiteSpace(absolutePath) || !File.Exists(absolutePath))
            {
                return !FailDelete;
            }

            File.Delete(absolutePath);
            return true;
        }
    }

    [TestMethod]
    public async Task UpdateItemText_PersistsAtomicallyAndRevertsOnSaveFailure()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("旧文本");
        var store = new MutationStore(directory.Root);
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        Assert.IsFalse(await service.UpdateItemTextAsync(Guid.NewGuid(), "无此条目"));
        Assert.IsTrue(await service.UpdateItemTextAsync(item.Id, "旧文本"), "同文本不应触发保存。");
        Assert.AreEqual(0, store.SaveCount);

        Assert.IsTrue(await service.UpdateItemTextAsync(item.Id, "新文本"));
        Assert.AreEqual("新文本", board.FindItem(item.Id)!.Text);
        Assert.AreEqual(1, store.SaveCount);

        store.FailSave = true;
        Assert.IsFalse(await service.UpdateItemTextAsync(item.Id, "不会被保存的文本"));
        Assert.AreEqual("新文本", board.FindItem(item.Id)!.Text, "保存失败必须还原原文本。");
        CollectionAssert.AreEqual(new[] { "内容未保存，已还原原文本。" }, messages);

        var imagePath = Path.Combine(directory.Root, "no-text-edit.png");
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4E, 0x47]);
        var image = board.AddImage(Guid.NewGuid(), "images/no-text-edit.png", imagePath);
        Assert.IsFalse(await service.UpdateItemTextAsync(image.Id, "图片卡不可文本编辑"));
    }
}
