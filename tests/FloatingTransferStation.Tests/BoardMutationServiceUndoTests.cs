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

        var undone = await service.UndoLastAsync();

        Assert.IsFalse(undone);
        Assert.AreEqual(0, store.SaveCount);
        // 1.41.0 撤销栈泛化为可撤销操作批：空栈提示随之泛化（删除批语义不变）。
        CollectionAssert.AreEqual(new[] { "没有可撤销的操作。" }, messages);
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
        Assert.IsTrue(await service.UndoLastAsync());

        CollectionAssert.AreEqual(
            originalIds,
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray(),
            "无并发改动时撤销应精确回到删除前的布局(含置顶归属)。");
        Assert.IsTrue(board.Items(BoardCategory.Inbox).Single(item => item.Id == first.Id).IsPinned);
        Assert.AreEqual(2, store.SaveCount);
        CollectionAssert.AreEqual(new[] { "已恢复最近删除的内容。" }, messages);
        Assert.AreEqual(0, service.PendingUndoCount);
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

        Assert.IsTrue(await service.UndoLastAsync());

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
        Assert.IsTrue(await service.UndoLastAsync());

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

        Assert.IsTrue(await service.UndoLastAsync());

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

        var undone = await service.UndoLastAsync();

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

        Assert.AreEqual(20, service.PendingUndoCount, "栈容量 20:最旧批次被驱逐。");
        Assert.IsFalse(File.Exists(oldestPath), "被驱逐批次的图片文件应立即清理。");
        Assert.AreEqual(1, store.DeleteCount);
    }

    [TestMethod]
    public async Task UndoStack_EvictionKeepsLifoOrder()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var oldestPath = Path.Combine(directory.Root, "oldest.png");
        await File.WriteAllBytesAsync(oldestPath, [0x89, 0x50, 0x4E, 0x47]);
        var oldest = board.AddImage(Guid.NewGuid(), "images/oldest.png", oldestPath);
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        Assert.IsTrue(await service.DeleteManyAsync([oldest.Id]));
        var newestPath = Path.Combine(directory.Root, "newest.png");
        await File.WriteAllBytesAsync(newestPath, [0x89, 0x50, 0x4E, 0x47]);
        var newestImage = board.AddImage(Guid.NewGuid(), "images/newest.png", newestPath);

        for (var index = 0; index < 19; index++)
        {
            var item = board.AddText($"批量{index}");
            Assert.IsTrue(await service.DeleteManyAsync([item.Id]));
        }

        // 第 21 批(最新,含图片)入栈:最旧图片批被驱逐,最新批的图片必须幸存。
        Assert.IsTrue(await service.DeleteManyAsync([newestImage.Id]));

        Assert.AreEqual(20, service.PendingUndoCount, "第 21 批入栈驱逐最旧一批。");
        Assert.IsFalse(File.Exists(oldestPath), "被驱逐批次的图片文件应立即清理。");
        Assert.IsTrue(File.Exists(newestPath), "非驱逐批次的图片不受驱逐影响。");
        Assert.AreEqual(1, store.DeleteCount);

        Assert.IsTrue(await service.UndoLastAsync());
        Assert.AreEqual(newestImage.Id, board.Items(BoardCategory.Inbox).Single().Id,
            "驱逐最旧批后,撤销得的仍是最近一批,而非次旧批。");
        Assert.IsTrue(File.Exists(newestPath), "撤销恢复后最新批的图片文件仍然存在。");
        Assert.AreEqual(19, service.PendingUndoCount);
    }

    [TestMethod]
    public async Task UndoLastDelete_SaveFailureWithMultipleBatchesKeepsOlderBatches()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var earlier = board.AddText("较早删除");
        var latest = board.AddText("最近删除");
        var store = new MutationStore(directory.Root);
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        Assert.IsTrue(await service.DeleteManyAsync([earlier.Id]));
        Assert.IsTrue(await service.DeleteManyAsync([latest.Id]));
        store.FailSave = true;

        Assert.IsFalse(await service.UndoLastAsync());
        Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count,
            "多批下撤销保存失败:本批回到删除态,无恢复残留。");
        Assert.AreEqual(1, service.PendingUndoCount, "旧批仍在栈,后续仍可撤销。");
        CollectionAssert.AreEqual(new[] { "撤销未保存，内容保持删除状态。" }, messages);

        store.FailSave = false;
        Assert.IsTrue(await service.UndoLastAsync());
        CollectionAssert.AreEqual(
            new[] { earlier.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray(),
            "失败后的下一次撤销恢复旧批;保存失败批保持删除态且已被消费,不再回到栈。");
        Assert.AreEqual(0, service.PendingUndoCount);
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
        Assert.IsTrue(await service.UndoLastAsync());

        var current = board.Items(BoardCategory.Reference).Select(item => item.Id).ToArray();
        CollectionAssert.AreEqual(new[] { pinned.Id, last.Id, first.Id }, current,
            "清空撤销按区语义恢复:置顶在前,普通区保持删除时的显示顺序(新增置顶)。");
        Assert.AreSame(otherCategory, board.Items(BoardCategory.Prompt).Single());
    }

    [TestMethod]
    public async Task UndoLastDelete_PopsMostRecentBatchNotOldest()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var earlier = board.AddText("较早删除");
        var latest = board.AddText("最近删除");
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        Assert.IsTrue(await service.DeleteManyAsync([earlier.Id]));
        Assert.IsTrue(await service.DeleteManyAsync([latest.Id]));
        Assert.AreEqual(2, service.PendingUndoCount);

        Assert.IsTrue(await service.UndoLastAsync());
        CollectionAssert.AreEqual(
            new[] { latest.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray(),
            "第一次撤销必须恢复最近删除的一批,而不是最旧的一批。");

        Assert.IsTrue(await service.UndoLastAsync());
        CollectionAssert.AreEqual(
            new[] { latest.Id, earlier.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray(),
            "再次撤销按后进先出恢复前一批,回到删除前的布局。");
        Assert.AreEqual(0, service.PendingUndoCount);
    }

    [TestMethod]
    public async Task UndoLastDelete_PinnedCardRestoredOverAutoCleanupBatch()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var now = DateTimeOffset.Now;
        var expired = board.AddText("超龄普通卡", createdAt: now - TimeSpan.FromHours(25));
        var pinned = board.AddText("被删置顶卡", createdAt: now - TimeSpan.FromHours(1));
        board.SetPinnedMany([pinned.Id], true);
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        var cleanup = await service.ClearNonPinnedAsync([BoardCategory.Inbox], now - TimeSpan.FromHours(24));
        Assert.IsTrue(cleanup.Saved);
        Assert.AreEqual(1, cleanup.RemovedCount);
        Assert.IsTrue(board.Items(BoardCategory.Inbox).Single().IsPinned, "自动清扫只删非置顶,置顶卡保留。");

        Assert.IsTrue(await service.DeleteManyAsync([pinned.Id]));
        Assert.AreEqual(2, service.PendingUndoCount, "清扫批与置顶卡删除批共栈。");

        Assert.IsTrue(await service.UndoLastAsync());
        Assert.IsTrue(board.Items(BoardCategory.Inbox).Single(item => item.Id == pinned.Id).IsPinned,
            "一次撤销必须恢复被删的置顶卡且置顶属性保留(落置顶区),而不是旧清扫批。");
        Assert.AreEqual(1, service.PendingUndoCount, "清扫批仍在栈,未被弹出。");

        Assert.IsTrue(await service.UndoLastAsync());
        CollectionAssert.AreEqual(
            new[] { pinned.Id, expired.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray(),
            "再次撤销恢复清扫批(置顶区在前,普通区按锚点插回)。");
        Assert.AreEqual(0, service.PendingUndoCount);
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

        Assert.IsTrue(await service.UndoLastAsync());

        Assert.IsTrue(File.Exists(imagePath), "撤销恢复后条目回归看板,文件必须仍然存在。");
        Assert.AreSame(image, board.Items(BoardCategory.Inbox).Single());
    }

    [TestMethod]
    public async Task ReplaceItemTextUndoable_ThenUndoRestoresOriginalText()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("原始转写稿");
        var store = new MutationStore(directory.Root);
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        Assert.IsTrue(await service.ReplaceItemTextUndoableAsync(item.Id, "整理后的文本"));
        Assert.AreEqual("整理后的文本", board.FindItem(item.Id)!.Text);
        Assert.AreEqual(1, service.PendingUndoCount, "AI 文本替换必须压入撤销批。");
        Assert.AreEqual(1, store.SaveCount);

        Assert.IsTrue(await service.UndoLastAsync());
        Assert.AreEqual("原始转写稿", board.FindItem(item.Id)!.Text, "Ctrl+Z 必须恢复替换前的原文。");
        Assert.AreEqual(0, service.PendingUndoCount);
        Assert.AreEqual(2, store.SaveCount);
        CollectionAssert.AreEqual(new[] { "已恢复卡片原文。" }, messages);

        // 幂等与守卫：不存在的条目、与原文相同的结果都不落盘不入栈。
        Assert.IsFalse(await service.ReplaceItemTextUndoableAsync(Guid.NewGuid(), "无此条目"));
        Assert.IsTrue(await service.ReplaceItemTextUndoableAsync(item.Id, "原始转写稿"), "同文本不触发保存。");
        Assert.AreEqual(2, store.SaveCount);
        Assert.AreEqual(0, service.PendingUndoCount);
    }

    [TestMethod]
    public async Task UndoStack_DeleteAndTextBatchesMixInLifoOrder()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var first = board.AddText("第一张原文");
        var second = board.AddText("第二张原文");
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        // 时序：替换第二张 → 删除第一张 → 替换第一张所在撤销批之下。
        Assert.IsTrue(await service.ReplaceItemTextUndoableAsync(second.Id, "第二张整理后"));
        Assert.IsTrue(await service.DeleteManyAsync([first.Id]));
        Assert.IsTrue(await service.ReplaceItemTextUndoableAsync(second.Id, "第二张再次整理后"));
        Assert.AreEqual(3, service.PendingUndoCount);

        // LIFO：先撤销最近的文本批，再撤销删除批，最后撤销最旧文本批。
        Assert.IsTrue(await service.UndoLastAsync());
        Assert.AreEqual("第二张整理后", board.FindItem(second.Id)!.Text, "最近文本批先恢复。");
        Assert.IsTrue(await service.UndoLastAsync());
        Assert.IsNotNull(board.FindItem(first.Id), "删除批随后恢复条目。");
        Assert.AreEqual("第二张整理后", board.FindItem(second.Id)!.Text);
        Assert.IsTrue(await service.UndoLastAsync());
        Assert.AreEqual("第二张原文", board.FindItem(second.Id)!.Text, "最旧文本批最后恢复。");
        Assert.AreEqual(0, service.PendingUndoCount);
    }

    [TestMethod]
    public async Task UndoStack_EvictionDropsOldestTextBatchHarmlessly()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        // 21 个文本替换批（同一张卡反复整理）：第 21 批驱逐最旧文本批，无害。
        var item = board.AddText("原文零");
        for (var round = 1; round <= 21; round++)
        {
            Assert.IsTrue(await service.ReplaceItemTextUndoableAsync(item.Id, $"原文{round}"));
        }

        Assert.AreEqual(20, service.PendingUndoCount, "栈容量 20：最旧文本批被驱逐。");
        // 驱逐的文本批无独占资源（不清理图片文件），条目仍在、当前文本不受影响。
        Assert.AreEqual("原文21", board.FindItem(item.Id)!.Text);
        Assert.AreEqual(0, store.DeleteCount, "文本批驱逐不得触发图片清理。");
        // 最旧批（第 2 轮，捕获原文1）已被驱逐到栈底之外；最近批捕获原文20。
        Assert.IsTrue(await service.UndoLastAsync());
        Assert.AreEqual("原文20", board.FindItem(item.Id)!.Text);
    }

    [TestMethod]
    public async Task ReplaceItemText_SaveFailureKeepsTextAndDoesNotEnterStack()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("原文");
        var store = new MutationStore(directory.Root);
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        store.FailSave = true;
        Assert.IsFalse(await service.ReplaceItemTextUndoableAsync(item.Id, "整理后"));
        Assert.AreEqual("原文", board.FindItem(item.Id)!.Text, "保存失败必须还原原文。");
        Assert.AreEqual(0, service.PendingUndoCount, "保存失败不得进入撤销栈。");
        CollectionAssert.AreEqual(new[] { "内容未保存，已还原原文本。" }, messages);
    }

    [TestMethod]
    public async Task UndoTextBatch_ItemAlreadyDeletedIsConsumedAsVoid()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("将被删除的原文");
        var store = new MutationStore(directory.Root);
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        Assert.IsTrue(await service.ReplaceItemTextUndoableAsync(item.Id, "整理后"));
        // 绕过撤销栈直接移除（模拟外部装载竞态）：文本批撤销时条目已不存在。
        _ = board.RemoveMany([item.Id]);
        store.FailSave = true;

        Assert.IsTrue(await service.UndoLastAsync(), "条目已删的文本批作废消费，不报失败。");
        Assert.AreEqual(0, service.PendingUndoCount);
        Assert.IsNull(board.FindItem(item.Id));
        Assert.AreEqual(1, store.SaveCount, "作废批不得再写盘。");
    }

    [TestMethod]
    public async Task UndoTextBatch_DoesNotClobberManualEditAfterReplace()
    {
        // T7 评审 M1：AI 整理完成后用户手工改文（手工编辑不进撤销栈，#22 口径），
        // 随后的 Ctrl+Z 不得越过后进的手工编辑把正文拉回整理前——批作废消费。
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("原始转写稿");
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        Assert.IsTrue(await service.ReplaceItemTextUndoableAsync(item.Id, "整理后的文本"));
        Assert.IsTrue(await service.UpdateItemTextAsync(item.Id, "手工改过的文本", default));

        Assert.IsTrue(await service.UndoLastAsync(), "正文已非本批写入值：批作废消费，不报失败。");
        Assert.AreEqual(
            "手工改过的文本",
            board.FindItem(item.Id)!.Text,
            "撤销不得覆盖整理完成后的手工编辑。");
        Assert.AreEqual(0, service.PendingUndoCount);
    }

    [TestMethod]
    public async Task ReplaceItemText_ExpectedCurrentTextMismatchAbandonsInsideGate()
    {
        // T7 评审 m1：门外前置比对存在让出窗口——期望原文在门内复检，不符即
        // 放弃替换并提示，不落盘、不入栈。
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("原始转写稿");
        var store = new MutationStore(directory.Root);
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        Assert.IsFalse(await service.ReplaceItemTextUndoableAsync(
            item.Id,
            "整理后的文本",
            expectedCurrentText: "过期的期望原文"));
        Assert.AreEqual("原始转写稿", board.FindItem(item.Id)!.Text, "期望不符必须放弃替换。");
        Assert.AreEqual(0, service.PendingUndoCount);
        Assert.AreEqual(0, store.SaveCount, "放弃路径不得写盘。");
        CollectionAssert.AreEqual(new[] { "卡片内容已变化，整理结果已放弃。" }, messages);
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
