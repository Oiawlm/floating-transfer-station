using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class BoardMutationServiceTests
{
    [TestMethod]
    public async Task MoveMany_ChangedBatchSavesExactlyOnce()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var bottom = board.AddText("bottom");
        var top = board.AddText("top");
        var existing = board.AddText("existing", BoardCategory.Prompt);
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        var result = await service.MoveManyAsync(
            [bottom.Id, top.Id],
            BoardCategory.Prompt,
            0);

        Assert.AreEqual(BoardBatchMoveResult.Moved, result);
        Assert.AreEqual(1, store.SaveCount);
        CollectionAssert.AreEqual(
            new[] { top.Id, bottom.Id, existing.Id },
            store.LastPersistedSnapshot!.Items
                .Where(item => item.Category == BoardCategory.Prompt)
                .OrderBy(item => item.Order)
                .Select(item => item.Id)
                .ToArray());
    }

    [TestMethod]
    public async Task MoveMany_EquivalentPositionDoesNotSave()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var bottom = board.AddText("bottom");
        var middle = board.AddText("middle");
        var top = board.AddText("top");
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        var result = await service.MoveManyAsync(
            [middle.Id, top.Id],
            BoardCategory.Inbox,
            2);

        Assert.AreEqual(BoardBatchMoveResult.NoChange, result);
        Assert.AreEqual(0, store.SaveCount);
        CollectionAssert.AreEqual(
            new[] { top.Id, middle.Id, bottom.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public async Task MoveMany_InvalidRegionReturnsInvalidWithoutSaving()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var normal = board.AddText("normal");
        var pinned = board.AddText("pinned");
        board.SetPinnedMany([pinned.Id], true);
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        var result = await service.MoveManyAsync(
            [normal.Id],
            BoardCategory.Inbox,
            0);

        Assert.AreEqual(BoardBatchMoveResult.Invalid, result);
        Assert.AreEqual(0, store.SaveCount);
    }

    [TestMethod]
    public async Task MoveManyToCategoryTop_MixedBatchPartitionsAndSavesOnce()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var movingNormal = board.AddText("moving normal");
        var movingPinned = board.AddText("moving pinned");
        board.SetPinnedMany([movingPinned.Id], true);
        var targetNormal = board.AddText("target normal", BoardCategory.Reference);
        var targetPinned = board.AddText("target pinned", BoardCategory.Reference);
        board.SetPinnedMany([targetPinned.Id], true);
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        var result = await service.MoveManyToCategoryTopAsync(
            [movingNormal.Id, movingPinned.Id],
            BoardCategory.Reference);

        Assert.AreEqual(BoardBatchMoveResult.Moved, result);
        Assert.AreEqual(1, store.SaveCount);
        CollectionAssert.AreEqual(
            new[] { movingPinned.Id, targetPinned.Id, movingNormal.Id, targetNormal.Id },
            board.Items(BoardCategory.Reference).Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public async Task SetPinned_ChangedStateSavesExactlyOnce()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("pin me");
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        var result = await service.SetPinnedAsync([item.Id], true);

        Assert.AreEqual(BoardPinResult.Changed, result);
        Assert.IsTrue(item.IsPinned);
        Assert.AreEqual(1, store.SaveCount);
        Assert.IsTrue(store.LastPersistedSnapshot!.Items.Single().IsPinned);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task SetPinned_SaveFailureRestoresStateOrderAndObjects()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var bottom = board.AddText("bottom");
        var top = board.AddText("top");
        var before = board.Items(BoardCategory.Inbox).ToArray();
        var store = new MutationStore(directory.Root) { FailSave = true };
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        var result = await service.SetPinnedAsync([bottom.Id], true);

        Assert.AreEqual(BoardPinResult.Failed, result);
        CollectionAssert.AreEqual(before, board.Items(BoardCategory.Inbox).ToArray());
        Assert.IsFalse(bottom.IsPinned);
        Assert.AreSame(top, board.Items(BoardCategory.Inbox)[0]);
        CollectionAssert.AreEqual(
            new[] { "置顶状态未保存，内容已恢复。" },
            messages);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task SetPinned_QueuedBehindDeleteReturnsInvalidWithoutSecondSave()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("delete before queued pin");
        var store = new MutationStore(directory.Root);
        var gate = new BoardOperationGate();
        var service = new BoardMutationService(board, store, _ => { }, gate);
        var blockerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = gate.RunAsync(async () =>
        {
            blockerStarted.TrySetResult();
            await releaseBlocker.Task;
            return true;
        });
        await blockerStarted.Task;

        var delete = service.DeleteManyAsync([item.Id]);
        var pin = service.SetPinnedAsync([item.Id], true);
        releaseBlocker.TrySetResult();

        Assert.IsTrue(await delete);
        Assert.AreEqual("Invalid", (await pin).ToString());
        Assert.IsTrue(await blocker);
        Assert.AreEqual(1, store.SaveCount);
        Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Adversarial")]
    public async Task MoveMany_QueuedBehindDeleteReturnsInvalidWithoutSecondSave(bool toCategoryTop)
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("delete before queued drag");
        var store = new MutationStore(directory.Root);
        var gate = new BoardOperationGate();
        var service = new BoardMutationService(board, store, _ => { }, gate);
        var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = gate.RunAsync(async () =>
        {
            await releaseBlocker.Task;
            return true;
        });

        var delete = service.DeleteManyAsync([item.Id]);
        Assert.IsTrue(board.CanMoveMany([item.Id], BoardCategory.Reference, 0));
        var move = toCategoryTop
            ? service.MoveManyToCategoryTopAsync([item.Id], BoardCategory.Reference)
            : service.MoveManyAsync([item.Id], BoardCategory.Reference, 0);
        releaseBlocker.TrySetResult();

        Assert.IsTrue(await blocker);
        Assert.IsTrue(await delete);
        Assert.AreEqual(BoardBatchMoveResult.Invalid, await move);
        Assert.AreEqual(1, store.SaveCount);
        Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
        Assert.AreEqual(0, board.Items(BoardCategory.Reference).Count);
    }

    [TestMethod]
    public async Task DeleteMany_RemovesMixedItemsWithOneSaveAndKeepsImagesForUndo()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var imagePath = Path.Combine(directory.Root, "selected.png");
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4E, 0x47]);
        var keep = board.AddText("keep");
        var image = board.AddImage(Guid.NewGuid(), "images/selected.png", imagePath);
        var text = board.AddText("selected text");
        board.SetPinnedMany([image.Id], true);
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        var success = await service.DeleteManyAsync([image.Id, text.Id]);

        Assert.IsTrue(success);
        CollectionAssert.AreEqual(
            new[] { keep.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
        Assert.AreEqual(1, store.SaveCount);
        Assert.AreEqual(1, service.PendingUndoDeleteCount);
        Assert.IsTrue(File.Exists(imagePath), "删除入撤销栈期间图片文件必须保留,保证撤销后缩略图可用。");

        service.DiscardUndoableDeletes();

        Assert.IsFalse(File.Exists(imagePath));
        Assert.AreEqual(1, store.DeleteCount);
        Assert.AreEqual(0, service.PendingUndoDeleteCount);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task DeleteMany_SaveFailureRestoresExactPinnedOrderWithoutDeletingImages()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var imagePath = Path.Combine(directory.Root, "restore-selected.png");
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4E, 0x47]);
        var normal = board.AddText("normal");
        var image = board.AddImage(Guid.NewGuid(), "images/restore-selected.png", imagePath);
        board.SetPinnedMany([image.Id], true);
        var before = board.Items(BoardCategory.Inbox).ToArray();
        var store = new MutationStore(directory.Root) { FailSave = true };
        var service = new BoardMutationService(board, store, _ => { });

        var success = await service.DeleteManyAsync([image.Id, normal.Id]);

        Assert.IsFalse(success);
        CollectionAssert.AreEqual(before, board.Items(BoardCategory.Inbox).ToArray());
        Assert.IsTrue(image.IsPinned);
        Assert.IsTrue(File.Exists(imagePath));
        Assert.AreEqual(0, store.DeleteCount);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task MoveMany_SaveFailureRestoresBothCategoriesAndObjects()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var bottom = board.AddText("bottom");
        var top = board.AddText("top");
        var existing = board.AddText("existing", BoardCategory.Reference);
        var inboxBefore = board.Items(BoardCategory.Inbox).ToArray();
        var referenceBefore = board.Items(BoardCategory.Reference).ToArray();
        var store = new MutationStore(directory.Root) { FailSave = true };
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        var result = await service.MoveManyAsync(
            [bottom.Id, top.Id],
            BoardCategory.Reference,
            0);

        Assert.AreEqual(BoardBatchMoveResult.Failed, result);
        CollectionAssert.AreEqual(inboxBefore, board.Items(BoardCategory.Inbox).ToArray());
        CollectionAssert.AreEqual(referenceBefore, board.Items(BoardCategory.Reference).ToArray());
        Assert.AreSame(top, board.Items(BoardCategory.Inbox)[0]);
        Assert.AreSame(bottom, board.Items(BoardCategory.Inbox)[1]);
        Assert.AreSame(existing, board.Items(BoardCategory.Reference)[0]);
        CollectionAssert.AreEqual(
            new[] { "移动未保存，内容已恢复到原位置。" },
            messages);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task MoveMany_FirstSaveFailureSerializesFollowingDelete()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var selectedBottom = board.AddText("selected bottom");
        var selectedTop = board.AddText("selected top");
        var deleteAfter = board.AddText("delete after");
        var store = new FailingFirstSaveBoardStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        var batch = service.MoveManyAsync(
            [selectedBottom.Id, selectedTop.Id],
            BoardCategory.Prompt,
            0);
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var delete = service.DeleteAsync(deleteAfter.Id);
        store.FailFirstSave();

        Assert.AreEqual(BoardBatchMoveResult.Failed, await batch);
        Assert.IsTrue(await delete);
        CollectionAssert.AreEqual(
            board.CreateSnapshot().Items
                .Select(item => (item.Id, item.Category, item.Order))
                .ToArray(),
            store.LastPersistedSnapshot!.Items
                .Select(item => (item.Id, item.Category, item.Order))
                .ToArray());
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task Delete_SaveFailureRestoresOriginalCategoryAndOrderAndDoesNotDeleteImage()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var last = board.AddText("较早内容");
        var path = Path.Combine(directory.Root, "image.png");
        await File.WriteAllBytesAsync(path, [0x89, 0x50, 0x4E, 0x47]);
        var middle = board.AddImage(Guid.NewGuid(), "images/image.png", path);
        var first = board.AddText("较新内容");
        board.MoveMany([first.Id], BoardCategory.Prompt, 0);
        board.MoveMany([middle.Id], BoardCategory.Prompt, 1);
        board.MoveMany([last.Id], BoardCategory.Prompt, 2);
        var originalIds = board.Items(BoardCategory.Prompt).Select(item => item.Id).ToArray();
        var store = new MutationStore(directory.Root) { FailSave = true };
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        var success = await service.DeleteAsync(middle.Id);

        Assert.IsFalse(success);
        var restoredItems = board.Items(BoardCategory.Prompt).ToArray();
        CollectionAssert.AreEqual(originalIds, restoredItems.Select(item => item.Id).ToArray());
        Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual(0, store.DeleteCount);
        CollectionAssert.AreEqual(
            new[] { "删除未保存，内容已恢复。" },
            messages);
        for (var index = 0; index < restoredItems.Length; index++)
        {
            Assert.AreEqual(BoardCategory.Prompt, restoredItems[index].Category);
            Assert.AreEqual(index, restoredItems[index].Order);
        }
    }

    [TestMethod]
    public async Task Delete_SaveSuccessRemovesRecordAndImageAfterUndoDiscard()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var path = Path.Combine(directory.Root, "image.png");
        await File.WriteAllBytesAsync(path, [0x89, 0x50, 0x4E, 0x47]);
        var item = board.AddImage(Guid.NewGuid(), "images/image.png", path);
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        var success = await service.DeleteAsync(item.Id);

        Assert.IsTrue(success);
        Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
        Assert.AreEqual(1, store.SaveCount);
        Assert.IsTrue(File.Exists(path), "撤销栈持有期间文件保留。");

        service.DiscardUndoableDeletes();

        Assert.IsFalse(File.Exists(path));
        Assert.AreEqual(1, store.DeleteCount);
    }

    [TestMethod]
    public async Task Delete_MissingItemReturnsFalseWithoutSaving()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("保留");
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        var success = await service.DeleteAsync(Guid.NewGuid());

        Assert.IsFalse(success);
        Assert.AreEqual(item.Id, board.Items(BoardCategory.Inbox).Single().Id);
        Assert.AreEqual(0, store.SaveCount);
        Assert.AreEqual(0, store.DeleteCount);
    }

    [TestMethod]
    public async Task ClearCategory_EmptyCategoryReturnsFalseWithoutSaving()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("保留在待分类");
        var store = new MutationStore(directory.Root);
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        var success = await service.ClearCategoryAsync(BoardCategory.Prompt);

        Assert.IsFalse(success);
        Assert.AreEqual(0, store.SaveCount);
        Assert.AreEqual(0, store.DeleteCount);
        Assert.IsEmpty(messages);
    }

    [TestMethod]
    public async Task ClearCategory_SaveSuccessRemovesOnlyTargetAndKeepsImagesForUndo()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var imagePath = Path.Combine(directory.Root, "prompt.png");
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4E, 0x47]);
        board.AddText("提示词", BoardCategory.Prompt);
        board.AddImage(
            Guid.NewGuid(),
            "images/prompt.png",
            imagePath,
            BoardCategory.Prompt);
        var inbox = board.AddText("其他分类保留");
        var store = new MutationStore(directory.Root);
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        var success = await service.ClearCategoryAsync(BoardCategory.Prompt);

        Assert.IsTrue(success);
        Assert.AreEqual(0, board.Items(BoardCategory.Prompt).Count);
        Assert.AreSame(inbox, board.Items(BoardCategory.Inbox).Single());
        Assert.AreEqual(1, store.SaveCount);
        Assert.IsFalse(store.LastPersistedSnapshot!.Items.Any(
            item => item.Category == BoardCategory.Prompt));
        Assert.AreEqual(1, service.PendingUndoDeleteCount);
        Assert.IsTrue(File.Exists(imagePath), "清空入撤销栈期间图片文件保留。");
        Assert.IsEmpty(messages);

        service.DiscardUndoableDeletes();

        Assert.IsFalse(File.Exists(imagePath));
        Assert.AreEqual(1, store.DeleteCount);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task ClearCategory_SaveFailureRestoresExactContentAndDoesNotDeleteImages()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var imagePath = Path.Combine(directory.Root, "restore.png");
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4E, 0x47]);
        var older = board.AddText("较早", BoardCategory.Reference);
        var image = board.AddImage(
            Guid.NewGuid(),
            "images/restore.png",
            imagePath,
            BoardCategory.Reference);
        var newer = board.AddText("较新", BoardCategory.Reference);
        var originalIds = board.Items(BoardCategory.Reference).Select(item => item.Id).ToArray();
        var store = new MutationStore(directory.Root) { FailSave = true };
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        var success = await service.ClearCategoryAsync(BoardCategory.Reference);

        Assert.IsFalse(success);
        CollectionAssert.AreEqual(
            originalIds,
            board.Items(BoardCategory.Reference).Select(item => item.Id).ToArray());
        Assert.AreSame(newer, board.Items(BoardCategory.Reference)[0]);
        Assert.AreSame(image, board.Items(BoardCategory.Reference)[1]);
        Assert.AreSame(older, board.Items(BoardCategory.Reference)[2]);
        Assert.IsTrue(File.Exists(imagePath));
        Assert.AreEqual(0, store.DeleteCount);
        CollectionAssert.AreEqual(new[] { "清空未保存，内容已恢复。" }, messages);
    }

    [TestMethod]
    public async Task ClearCategory_ImageCleanupFailureReportsOnceAtDiscardWithoutRestoringRecords()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddImage(Guid.NewGuid(), "images/one.png", Path.Combine(directory.Root, "one.png"));
        board.AddImage(Guid.NewGuid(), "images/two.png", Path.Combine(directory.Root, "two.png"));
        var store = new MutationStore(directory.Root) { FailDelete = true };
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        var success = await service.ClearCategoryAsync(BoardCategory.Inbox);

        Assert.IsTrue(success);
        Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
        Assert.AreEqual(0, store.DeleteCount, "清空即时不再触碰文件,清理延迟到丢弃撤销栈。");
        Assert.IsEmpty(messages);

        service.DiscardUndoableDeletes();

        Assert.AreEqual(2, store.DeleteCount);
        CollectionAssert.AreEqual(
            new[] { "部分已删除的图片副本暂时无法清理。" },
            messages);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task ClearCategory_FirstSaveFailureSerializesNextMutationAndPersistsRestoredBoard()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var prompt = board.AddText("恢复的提示词", BoardCategory.Prompt);
        var inbox = board.AddText("随后移动");
        var store = new FailingFirstSaveBoardStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        var clear = service.ClearCategoryAsync(BoardCategory.Prompt);
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var move = service.MoveManyAsync([inbox.Id], BoardCategory.CustomerOriginal, 0);
        store.FailFirstSave();

        Assert.IsFalse(await clear);
        Assert.AreEqual(BoardBatchMoveResult.Moved, await move);
        Assert.AreSame(prompt, board.Items(BoardCategory.Prompt).Single());
        CollectionAssert.AreEqual(
            board.CreateSnapshot().Items
                .Select(item => (item.Id, item.Category, item.Order))
                .ToArray(),
            store.LastPersistedSnapshot!.Items
                .Select(item => (item.Id, item.Category, item.Order))
                .ToArray());
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task ConcurrentDeletes_FirstSaveFailureLeavesPersistedSnapshotMatchingRestoredBoard()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var itemB = board.AddText("B", Guid.Parse("00000000-0000-0000-0000-00000000000b"));
        var itemA = board.AddText("A", Guid.Parse("00000000-0000-0000-0000-00000000000a"));
        var store = new FailingFirstSaveBoardStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        var deleteA = service.DeleteAsync(itemA.Id);
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var deleteB = service.DeleteAsync(itemB.Id);
        store.FailFirstSave();

        Assert.IsFalse(await deleteA);
        Assert.IsTrue(await deleteB);
        CollectionAssert.AreEqual(
            board.CreateSnapshot().Items.Select(item => item.Id).ToArray(),
            store.LastPersistedSnapshot!.Items.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task SaveForShutdown_FinalSaveFailureReopensGateForNextMutation()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("move after failed close");
        var store = new MutationStore(directory.Root) { FailSave = true };
        var service = new BoardMutationService(board, store, _ => { });

        await Assert.ThrowsExactlyAsync<IOException>(
            () => service.SaveForShutdownAsync(() => Task.CompletedTask));

        store.FailSave = false;
        Assert.AreEqual(
            BoardBatchMoveResult.Moved,
            await service.MoveManyAsync([item.Id], BoardCategory.Prompt, 0));
        Assert.AreEqual(BoardCategory.Prompt, item.Category);
        Assert.AreEqual(1, store.SaveCount);
        Assert.AreEqual(
            BoardCategory.Prompt,
            store.LastPersistedSnapshot!.Items.Single().Category);
    }

    [TestMethod]
    public async Task ClearNonPinned_RemovesOnlyNonPinnedAndKeepsPinnedOrder()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var pinnedTop = board.AddText("置顶一");
        var pinnedSecond = board.AddText("置顶二");
        var normalFirst = board.AddText("普通一");
        var normalSecond = board.AddText("普通二");
        board.SetPinnedMany([pinnedTop.Id, pinnedSecond.Id], true);
        var keep = board.AddText("其他分类保留", BoardCategory.Reference);
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        Assert.IsTrue(await service.ClearNonPinnedAsync(BoardCategory.Inbox));

        // 新卡插在普通区顶部,置顶批量保持来源相对顺序:
        // 清空前 = [置顶二, 置顶一, 普通二, 普通一]。
        CollectionAssert.AreEqual(
            new[] { pinnedSecond.Id, pinnedTop.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
        Assert.AreEqual(1, store.SaveCount);
        CollectionAssert.AreEqual(
            new[] { pinnedSecond.Id, pinnedTop.Id },
            store.LastPersistedSnapshot!.Items
                .Where(item => item.Category == BoardCategory.Inbox)
                .OrderBy(item => item.Order)
                .Select(item => item.Id)
                .ToArray());
        Assert.AreSame(keep, board.Items(BoardCategory.Reference).Single());
        Assert.AreEqual(1, service.PendingUndoDeleteCount, "被清空条目必须整批进入撤销栈。");
    }

    [TestMethod]
    public async Task ClearNonPinned_WithoutNonPinnedItemsIsNoOpWithoutSaving()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var pinned = board.AddText("只有置顶");
        board.SetPinnedMany([pinned.Id], true);
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        Assert.IsFalse(await service.ClearNonPinnedAsync(BoardCategory.Inbox));
        Assert.AreEqual(0, store.SaveCount);
        Assert.AreSame(pinned, board.Items(BoardCategory.Inbox).Single());
        Assert.AreEqual(0, service.PendingUndoDeleteCount, "空操作不得进入撤销栈。");
    }

    [TestMethod]
    public async Task ClearNonPinned_SaveFailureRestoresWholeCategoryInExactOrder()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var pinnedTop = board.AddText("置顶一");
        var normalFirst = board.AddText("普通一");
        var pinnedSecond = board.AddText("置顶二");
        board.SetPinnedMany([pinnedTop.Id, pinnedSecond.Id], true);
        var before = board.Items(BoardCategory.Inbox).ToArray();
        var store = new MutationStore(directory.Root) { FailSave = true };
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        Assert.IsFalse(await service.ClearNonPinnedAsync(BoardCategory.Inbox));

        CollectionAssert.AreEqual(
            before.Select(item => item.Id).ToArray(),
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
        CollectionAssert.AreEquivalent(new[] { "清空未保存，内容已恢复。" }, messages);
        Assert.AreEqual(0, service.PendingUndoDeleteCount);
    }

    [TestMethod]
    public async Task ClearNonPinned_IsUndoableAndKeepsItemsAddedAfterTheClear()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var pinned = board.AddText("置顶");
        var first = board.AddText("普通一");
        var second = board.AddText("普通二");
        board.SetPinnedMany([pinned.Id], true);
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        Assert.IsTrue(await service.ClearNonPinnedAsync(BoardCategory.Inbox));
        var later = board.AddText("清空后新增");

        Assert.IsTrue(await service.UndoLastDeleteAsync());

        // 清空前 = [置顶, 普通二, 普通一]（新卡在上）；清空后新增位于普通区顶部；
        // 撤销按锚点插回：普通一跟在普通二之后、新增保持最上。
        CollectionAssert.AreEqual(
            new[] { pinned.Id, second.Id, first.Id, later.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
        Assert.IsTrue(pinned.IsPinned);
        Assert.IsFalse(first.IsPinned);
        Assert.AreEqual(2, store.SaveCount, "撤销必须再次持久化。");
    }

    [TestMethod]
    public async Task ClearNonPinned_MultiCategory_SavesOnceAndEnqueuesSingleUndoBatch()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var imagePinned = board.AddText("图片置顶", BoardCategory.CustomerOriginal);
        board.SetPinnedMany([imagePinned.Id], true);
        var imageNormal = board.AddText("图片普通", BoardCategory.CustomerOriginal);
        var promptNormal = board.AddText("提示词普通", BoardCategory.Prompt);
        var referenceNormal = board.AddText("复盘标签条目", BoardCategory.Reference);
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        var outcome = await service.ClearNonPinnedAsync(AutoCleanupSchedule.SweepCategories);

        Assert.IsTrue(outcome.Saved);
        Assert.AreEqual(2, outcome.RemovedCount);
        Assert.AreEqual(1, store.SaveCount, "跨分类清扫必须单次原子保存。");
        Assert.AreEqual(1, service.PendingUndoDeleteCount, "跨分类清扫必须进单批撤销（可整批 Ctrl+Z）。");
        CollectionAssert.AreEqual(
            new[] { imagePinned.Id },
            store.LastPersistedSnapshot!.Items
                .Where(item => item.Category != BoardCategory.Reference)
                .Select(item => item.Id)
                .ToArray(),
            "保存的快照必须已不含被清扫条目(仅存活置顶项,复盘分类另计)。");
        Assert.AreSame(imagePinned, board.Items(BoardCategory.CustomerOriginal).Single());
        Assert.AreSame(referenceNormal, board.Items(BoardCategory.Reference).Single());
    }

    [TestMethod]
    public async Task ClearNonPinned_MultiCategory_NothingToRemoveIsSuccessfulNoOpWithoutSaving()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var pinned = board.AddText("只有置顶");
        board.SetPinnedMany([pinned.Id], true);
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        var outcome = await service.ClearNonPinnedAsync(AutoCleanupSchedule.SweepCategories);

        Assert.IsTrue(outcome.Saved);
        Assert.AreEqual(0, outcome.RemovedCount);
        Assert.AreEqual(0, store.SaveCount, "幂等空操作不得写盘。");
        Assert.AreEqual(0, service.PendingUndoDeleteCount);
        Assert.AreSame(pinned, board.Items(BoardCategory.Inbox).Single());
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task ClearNonPinned_MultiCategory_SaveFailureRestoresEveryCategoryInExactOrderWithoutUndoBatch()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var inboxPinned = board.AddText("收件置顶");
        var inboxNormal = board.AddText("收件普通");
        board.SetPinnedMany([inboxPinned.Id], true);
        var promptPinned = board.AddText("提示词置顶", BoardCategory.Prompt);
        var promptNormal = board.AddText("提示词普通", BoardCategory.Prompt);
        board.SetPinnedMany([promptPinned.Id], true);
        var referenceNormal = board.AddText("复盘标签条目", BoardCategory.Reference);
        var inboxBefore = board.Items(BoardCategory.Inbox).ToArray();
        var promptBefore = board.Items(BoardCategory.Prompt).ToArray();
        var store = new MutationStore(directory.Root) { FailSave = true };
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        var outcome = await service.ClearNonPinnedAsync(AutoCleanupSchedule.SweepCategories);

        Assert.IsFalse(outcome.Saved);
        Assert.AreEqual(2, outcome.RemovedCount);
        CollectionAssert.AreEqual(
            inboxBefore.Select(item => item.Id).ToArray(),
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(
            promptBefore.Select(item => item.Id).ToArray(),
            board.Items(BoardCategory.Prompt).Select(item => item.Id).ToArray());
        Assert.AreSame(inboxPinned, board.Items(BoardCategory.Inbox)[0]);
        Assert.AreSame(inboxNormal, board.Items(BoardCategory.Inbox)[1]);
        Assert.AreSame(promptPinned, board.Items(BoardCategory.Prompt)[0]);
        Assert.AreSame(promptNormal, board.Items(BoardCategory.Prompt)[1]);
        Assert.AreSame(referenceNormal, board.Items(BoardCategory.Reference).Single());
        Assert.AreEqual(0, service.PendingUndoDeleteCount, "保存失败不得进入撤销栈。");
        Assert.AreEqual(0, store.SaveCount);
        CollectionAssert.AreEqual(new[] { "清空未保存，内容已恢复。" }, messages);
    }

    [TestMethod]
    public async Task ClearNonPinned_MultiCategory_UndoRestoresWholeBatchAcrossCategories()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var imageNormal = board.AddText("图片普通", BoardCategory.CustomerOriginal);
        var promptNormal = board.AddText("提示词普通", BoardCategory.Prompt);
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        var outcome = await service.ClearNonPinnedAsync(AutoCleanupSchedule.SweepCategories);

        Assert.IsTrue(outcome.Saved);
        Assert.IsTrue(await service.UndoLastDeleteAsync());
        Assert.AreSame(imageNormal, board.Items(BoardCategory.CustomerOriginal).Single());
        Assert.AreSame(promptNormal, board.Items(BoardCategory.Prompt).Single());
        Assert.AreEqual(0, service.PendingUndoDeleteCount);
    }

    [TestMethod]
    public async Task ClearNonPinned_WithExpiryCutoff_RemovesOnlyExpiredSavesOnceAndUndoRestoresBatch()
    {
        // 逐卡 24h TTL（1.25.0）自动清扫路径：cutoff 经同一移除管线传递，
        // 未满期普通项保留；被移除批次仍可整批 Ctrl+Z 恢复（跨分类）。
        using var directory = new TestDirectory();
        var board = new BoardService();
        var expiredNormal = board.AddText("超龄普通", BoardCategory.CustomerOriginal);
        var freshNormal = board.AddText("新鲜普通", BoardCategory.CustomerOriginal);
        var expiredPromptNormal = board.AddText("超龄提示词", BoardCategory.Prompt);
        var expiredPinned = board.AddText("超龄置顶", BoardCategory.Prompt);
        board.SetPinnedMany([expiredPinned.Id], true);
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - AutoCleanupSchedule.CleanupInterval;
        board.SetItemCreatedAt(expiredNormal.Id, cutoff);
        board.SetItemCreatedAt(freshNormal.Id, cutoff.AddSeconds(1));
        board.SetItemCreatedAt(expiredPromptNormal.Id, cutoff);
        board.SetItemCreatedAt(expiredPinned.Id, cutoff);
        var store = new MutationStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        var outcome = await service.ClearNonPinnedAsync(
            AutoCleanupSchedule.SweepCategories,
            cutoff);

        Assert.IsTrue(outcome.Saved);
        Assert.AreEqual(2, outcome.RemovedCount, "只有超龄普通项到期；超龄置顶与未满期保留。");
        Assert.AreEqual(1, store.SaveCount, "清扫必须单次原子保存。");
        Assert.AreEqual(1, service.PendingUndoDeleteCount, "清扫必须单批可撤销。");
        // 做旧经快照回合重建条目实例，按 Id 断言而非实例同一。
        CollectionAssert.AreEqual(
            new[] { expiredPinned.Id },
            board.Items(BoardCategory.Prompt).Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { freshNormal.Id },
            board.Items(BoardCategory.CustomerOriginal).Select(item => item.Id).ToArray());

        Assert.IsTrue(await service.UndoLastDeleteAsync(), "被清扫批次必须可整批撤销恢复。");
        // 锚点插回（契约 #11）：新卡插普通区顶部，删除时显示序为 [新鲜, 超龄]，
        // 撤销按删除时显示序插回原位，不重排其余内容。
        CollectionAssert.AreEqual(
            new[] { freshNormal.Id, expiredNormal.Id },
            board.Items(BoardCategory.CustomerOriginal).Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { expiredPromptNormal.Id },
            board.Items(BoardCategory.Prompt).Where(item => !item.IsPinned).Select(item => item.Id).ToArray());
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task ClearNonPinned_SaveFailure_WithExpiryCutoff_RestoresEveryCategoryWithoutUndoBatch()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var expiredNormal = board.AddText("超龄普通", BoardCategory.Inbox);
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - AutoCleanupSchedule.CleanupInterval;
        board.SetItemCreatedAt(expiredNormal.Id, cutoff);
        var store = new MutationStore(directory.Root) { FailSave = true };
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        var outcome = await service.ClearNonPinnedAsync(
            AutoCleanupSchedule.SweepCategories,
            cutoff);

        Assert.IsFalse(outcome.Saved);
        Assert.AreEqual(1, outcome.RemovedCount);
        CollectionAssert.AreEqual(
            new[] { expiredNormal.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray(),
            "保存失败整批恢复原内容。");
        Assert.AreEqual(0, service.PendingUndoDeleteCount, "保存失败不得进入撤销栈。");
        Assert.AreEqual(0, store.SaveCount);
        CollectionAssert.AreEqual(new[] { "清空未保存，内容已恢复。" }, messages);
    }

    private sealed class MutationStore(string root) : IBoardStore
    {
        public bool FailSave { get; set; }
        public bool FailDelete { get; set; }
        public int SaveCount { get; private set; }
        public int DeleteCount { get; private set; }
        public BoardSnapshot? LastPersistedSnapshot { get; private set; }
        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public Task SaveBoardAsync(BoardSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            if (FailSave)
            {
                throw new IOException("Injected failure.");
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
            if (FailDelete)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(absolutePath) && File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
            }

            return true;
        }
    }

}
