using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class BoardServiceTests
{
    [TestMethod]
    public void Categories_AreLockedInDisplayOrder()
    {
        var displayNameField = typeof(ProductIdentity).GetField(nameof(ProductIdentity.DisplayName));

        Assert.IsNotNull(displayNameField);
        Assert.AreEqual("悬浮中转站", displayNameField.GetValue(null));
        CollectionAssert.AreEqual(
            new[] { "客户原图", "对标图", "提示词", "待分类" },
            BoardCategoryCatalog.Ordered.Select(BoardCategoryCatalog.DisplayName).ToArray());
    }

    [TestMethod]
    public void CategoryNameSettings_AllowsBlankAndSixCharacters()
    {
        var settings = WindowSettings.Default;

        settings = settings.WithCategoryName(BoardCategory.CustomerOriginal, string.Empty);
        Assert.AreEqual(string.Empty, settings.CategoryName(BoardCategory.CustomerOriginal));

        settings = settings.WithCategoryName(BoardCategory.CustomerOriginal, "客户原图六");
        Assert.AreEqual("客户原图六", settings.CategoryName(BoardCategory.CustomerOriginal));
    }

    [TestMethod]
    public void CategoryNameSettings_RejectsNamesLongerThanSixCharacters()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            WindowSettings.Default.WithCategoryName(
                BoardCategory.CustomerOriginal,
                "七个字以上名称"));
    }

    [TestMethod]
    public void CategoryNameSettings_CountsCompositeEmojiAsOneVisibleCharacter()
    {
        const string name = "👩‍🚀👩‍🚀👩‍🚀👩‍🚀👩‍🚀👩‍🚀";

        var settings = WindowSettings.Default.WithCategoryName(
            BoardCategory.CustomerOriginal,
            name);

        Assert.AreEqual(name, settings.CategoryName(BoardCategory.CustomerOriginal));
    }

    [TestMethod]
    public void AddText_InsertsAtTopOfInboxAndReindexesExistingItems()
    {
        var board = new BoardService();

        var first = board.AddText("第一条", Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var second = board.AddText("第二条", Guid.Parse("00000000-0000-0000-0000-000000000002"));

        CollectionAssert.AreEqual(
            new[] { second.Id, first.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { 0, 1 },
            board.Items(BoardCategory.Inbox).Select(item => item.Order).ToArray());
    }

    [TestMethod]
    public void AddImage_InsertsAtTopOfInboxAndKeepsManagedPaths()
    {
        var board = new BoardService();
        var id = Guid.Parse("00000000-0000-0000-0000-000000000020");

        var item = board.AddImage(id, "images/asset.png", @"C:\Data\images\asset.png");

        Assert.AreEqual(BoardItemKind.Image, item.Kind);
        Assert.AreEqual(BoardCategory.Inbox, item.Category);
        Assert.AreEqual("images/asset.png", item.ImageRelativePath);
        Assert.AreEqual(@"C:\Data\images\asset.png", item.ImageAbsolutePath);
        Assert.AreSame(item, board.Items(BoardCategory.Inbox).Single());
    }

    [TestMethod]
    public void AddText_WithTargetCategoryInsertsAtThatCategoryTopAndReindexes()
    {
        var board = new BoardService();
        var older = board.AddText(
            "较早提示词",
            BoardCategory.Prompt,
            Guid.Parse("00000000-0000-0000-0000-000000000031"));
        var newer = board.AddText(
            "较新提示词",
            BoardCategory.Prompt,
            Guid.Parse("00000000-0000-0000-0000-000000000032"));

        CollectionAssert.AreEqual(
            new[] { newer.Id, older.Id },
            board.Items(BoardCategory.Prompt).Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { 0, 1 },
            board.Items(BoardCategory.Prompt).Select(item => item.Order).ToArray());
        Assert.IsTrue(board.Items(BoardCategory.Inbox).Count == 0);
    }

    [TestMethod]
    public void AddImage_WithTargetCategoryKeepsManagedPathsInThatCategory()
    {
        var board = new BoardService();
        var id = Guid.Parse("00000000-0000-0000-0000-000000000033");

        var item = board.AddImage(
            id,
            "images/customer.png",
            @"C:\Data\images\customer.png",
            BoardCategory.CustomerOriginal);

        Assert.AreEqual(BoardCategory.CustomerOriginal, item.Category);
        Assert.AreSame(item, board.Items(BoardCategory.CustomerOriginal).Single());
        Assert.IsTrue(board.Items(BoardCategory.Inbox).Count == 0);
    }

    [TestMethod]
    public void Add_WithUndefinedTargetDoesNotChangeAnyCategory()
    {
        var board = new BoardService();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            board.AddText("不能加入", (BoardCategory)99));

        Assert.IsTrue(BoardCategoryCatalog.Ordered.All(category => board.Items(category).Count == 0));
    }

    [TestMethod]
    public void Add_NewContentStartsAtTopOfNormalRegionBelowPins()
    {
        var board = new BoardService();
        var pinned = board.AddText("置顶");
        board.SetPinnedMany([pinned.Id], true);

        var captured = board.AddText("新内容");

        CollectionAssert.AreEqual(
            new[] { pinned.Id, captured.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
        Assert.IsTrue(pinned.IsPinned);
        Assert.IsTrue(captured.StartsNormalRegion);
    }

    [TestMethod]
    public void SetPinnedMany_MovesSourceOrderedItemsToPinnedTopAndCanUndo()
    {
        var board = new BoardService();
        var bottom = board.AddText("bottom");
        var middle = board.AddText("middle");
        var top = board.AddText("top");
        var existingPin = board.AddText("existing pin");
        board.SetPinnedMany([existingPin.Id], true);
        var before = board.Items(BoardCategory.Inbox).ToArray();

        var change = board.SetPinnedMany([bottom.Id, top.Id], true);

        Assert.IsTrue(change.Changed);
        CollectionAssert.AreEqual(
            new[] { top.Id, bottom.Id, existingPin.Id, middle.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
        board.Undo(change);
        CollectionAssert.AreEqual(before, board.Items(BoardCategory.Inbox).ToArray());
        Assert.IsFalse(top.IsPinned);
        Assert.IsFalse(bottom.IsPinned);
        Assert.IsTrue(existingPin.IsPinned);
    }

    [TestMethod]
    public void SetPinnedMany_UnpinMovesItemsToNormalTopInSourceOrder()
    {
        var board = new BoardService();
        var normal = board.AddText("normal");
        var lowerPin = board.AddText("lower pin");
        var upperPin = board.AddText("upper pin");
        board.SetPinnedMany([lowerPin.Id, upperPin.Id], true);

        board.SetPinnedMany([lowerPin.Id, upperPin.Id], false);

        CollectionAssert.AreEqual(
            new[] { upperPin.Id, lowerPin.Id, normal.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
        Assert.IsFalse(upperPin.IsPinned);
        Assert.IsFalse(upperPin.StartsNormalRegion);
    }

    [TestMethod]
    public void Move_CrossCategoryChangesOnlyRequestedItemAndCanUndo()
    {
        var board = new BoardService();
        var item = board.AddText("提示词", Guid.Parse("00000000-0000-0000-0000-000000000003"));

        var move = board.Move(item.Id, BoardCategory.Prompt, 0);

        Assert.AreEqual(BoardCategory.Prompt, item.Category);
        Assert.AreEqual(0, item.Order);
        Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);

        board.Undo(move);

        Assert.AreEqual(BoardCategory.Inbox, item.Category);
        Assert.AreEqual(0, item.Order);
    }

    [TestMethod]
    public void Move_WithinCategoryUsesPreMoveTargetIndex()
    {
        var board = new BoardService();
        var third = board.AddText("三", Guid.Parse("00000000-0000-0000-0000-000000000003"));
        var second = board.AddText("二", Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var first = board.AddText("一", Guid.Parse("00000000-0000-0000-0000-000000000001"));

        board.Move(first.Id, BoardCategory.Inbox, 3);

        CollectionAssert.AreEqual(
            new[] { second.Id, third.Id, first.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public void Move_ClampsTargetIndexToItemsPinnedRegion()
    {
        var board = new BoardService();
        var normal = board.AddText("normal");
        var pinned = board.AddText("pinned");
        board.SetPinnedMany([pinned.Id], true);

        var movedNormal = board.AddText("moved normal", BoardCategory.Prompt);
        board.Move(movedNormal.Id, BoardCategory.Inbox, 0);

        CollectionAssert.AreEqual(
            new[] { pinned.Id, movedNormal.Id, normal.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());

        var movedPinned = board.AddText("moved pinned", BoardCategory.Prompt);
        board.SetPinnedMany([movedPinned.Id], true);
        board.Move(movedPinned.Id, BoardCategory.Inbox, int.MaxValue);

        CollectionAssert.AreEqual(
            new[] { pinned.Id, movedPinned.Id, movedNormal.Id, normal.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public void MoveMany_CrossCategoryUsesSourceOrderAndCanUndo()
    {
        var board = new BoardService();
        var bottom = board.AddText("bottom");
        var middle = board.AddImage(
            Guid.NewGuid(),
            "images/middle.png",
            @"C:\Data\middle.png");
        var top = board.AddText("top");
        var existing = board.AddText("existing", BoardCategory.CustomerOriginal);

        var move = board.MoveMany(
            [bottom.Id, top.Id],
            BoardCategory.CustomerOriginal,
            0);

        Assert.IsTrue(move.Changed);
        CollectionAssert.AreEqual(
            new[] { top.Id, bottom.Id, existing.Id },
            board.Items(BoardCategory.CustomerOriginal).Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { middle.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
        Assert.AreEqual(BoardCategory.CustomerOriginal, top.Category);
        Assert.AreEqual(BoardCategory.CustomerOriginal, bottom.Category);

        board.Undo(move);

        CollectionAssert.AreEqual(
            new[] { top.Id, middle.Id, bottom.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { existing.Id },
            board.Items(BoardCategory.CustomerOriginal).Select(item => item.Id).ToArray());
        Assert.AreSame(top, board.Items(BoardCategory.Inbox)[0]);
        Assert.AreSame(middle, board.Items(BoardCategory.Inbox)[1]);
        Assert.AreSame(bottom, board.Items(BoardCategory.Inbox)[2]);
        Assert.AreSame(existing, board.Items(BoardCategory.CustomerOriginal)[0]);
    }

    [TestMethod]
    public void MoveMany_SameCategoryTreatsSelectionAsOneOrderedBlockAndCanUndo()
    {
        var board = new BoardService();
        var e = board.AddText("E");
        var d = board.AddText("D");
        var c = board.AddText("C");
        var b = board.AddText("B");
        var a = board.AddText("A");

        var move = board.MoveMany([d.Id, b.Id], BoardCategory.Inbox, 5);

        Assert.IsTrue(move.Changed);
        CollectionAssert.AreEqual(
            new[] { a.Id, c.Id, e.Id, b.Id, d.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());

        board.Undo(move);

        CollectionAssert.AreEqual(
            new[] { a.Id, b.Id, c.Id, d.Id, e.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
        Assert.AreSame(b, board.Items(BoardCategory.Inbox)[1]);
        Assert.AreSame(d, board.Items(BoardCategory.Inbox)[3]);
    }

    [TestMethod]
    public void MoveMany_EquivalentPositionDoesNotMutateCollection()
    {
        var board = new BoardService();
        var d = board.AddText("D");
        var c = board.AddText("C");
        var b = board.AddText("B");
        var a = board.AddText("A");
        var collection = board.Items(BoardCategory.Inbox);
        var changeCount = 0;
        collection.CollectionChanged += (_, _) => changeCount++;

        var move = board.MoveMany([c.Id, b.Id], BoardCategory.Inbox, 3);

        Assert.IsFalse(move.Changed);
        Assert.AreEqual(0, changeCount);
        CollectionAssert.AreEqual(
            new[] { a.Id, b.Id, c.Id, d.Id },
            collection.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public void MoveMany_InvalidBatchIsRejectedBeforeAnyCollectionChanges()
    {
        var board = new BoardService();
        var inbox = board.AddText("inbox");
        var prompt = board.AddText("prompt", BoardCategory.Prompt);
        var before = board.CreateSnapshot().Items
            .Select(item => (item.Id, item.Category, item.Order))
            .ToArray();

        Assert.ThrowsExactly<ArgumentException>(() =>
            board.MoveMany([], BoardCategory.Reference, 0));
        Assert.ThrowsExactly<ArgumentException>(() =>
            board.MoveMany([inbox.Id, inbox.Id], BoardCategory.Reference, 0));
        Assert.ThrowsExactly<KeyNotFoundException>(() =>
            board.MoveMany([Guid.NewGuid()], BoardCategory.Reference, 0));
        Assert.ThrowsExactly<ArgumentException>(() =>
            board.MoveMany([inbox.Id, prompt.Id], BoardCategory.Reference, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            board.MoveMany([inbox.Id], (BoardCategory)999, 0));

        CollectionAssert.AreEqual(
            before,
            board.CreateSnapshot().Items
                .Select(item => (item.Id, item.Category, item.Order))
                .ToArray());
    }

    [TestMethod]
    public void MoveMany_SameCategoryRejectsCrossRegionAndMixedSelection()
    {
        var board = new BoardService();
        var normalBottom = board.AddText("normal bottom");
        var normalTop = board.AddText("normal top");
        var pinned = board.AddText("pinned");
        board.SetPinnedMany([pinned.Id], true);
        var before = board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray();

        var pinnedIntoNormal = board.MoveMany([pinned.Id], BoardCategory.Inbox, 2);
        var normalIntoPinned = board.MoveMany([normalTop.Id], BoardCategory.Inbox, 0);
        var mixed = board.MoveMany([pinned.Id, normalBottom.Id], BoardCategory.Inbox, 1);

        Assert.AreEqual(BoardMoveDisposition.Invalid, pinnedIntoNormal.Disposition);
        Assert.AreEqual(BoardMoveDisposition.Invalid, normalIntoPinned.Disposition);
        Assert.AreEqual(BoardMoveDisposition.Invalid, mixed.Disposition);
        CollectionAssert.AreEqual(
            before,
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public void MoveMany_CrossCategoryPartitionsMixedBatchAndCanUndo()
    {
        var board = new BoardService();
        var movingNormal = board.AddText("moving normal");
        var movingPinned = board.AddText("moving pinned");
        board.SetPinnedMany([movingPinned.Id], true);
        var targetNormal = board.AddText("target normal", BoardCategory.Reference);
        var targetPinned = board.AddText("target pinned", BoardCategory.Reference);
        board.SetPinnedMany([targetPinned.Id], true);
        var inboxBefore = board.Items(BoardCategory.Inbox).ToArray();
        var targetBefore = board.Items(BoardCategory.Reference).ToArray();

        var move = board.MoveManyToCategoryTop(
            [movingNormal.Id, movingPinned.Id],
            BoardCategory.Reference);

        Assert.AreEqual(BoardMoveDisposition.Changed, move.Disposition);
        CollectionAssert.AreEqual(
            new[] { movingPinned.Id, targetPinned.Id, movingNormal.Id, targetNormal.Id },
            board.Items(BoardCategory.Reference).Select(item => item.Id).ToArray());
        board.Undo(move);
        CollectionAssert.AreEqual(inboxBefore, board.Items(BoardCategory.Inbox).ToArray());
        CollectionAssert.AreEqual(targetBefore, board.Items(BoardCategory.Reference).ToArray());
    }

    [TestMethod]
    public void CanMoveMany_OnlyAcceptsGapsInsideTheBatchRegion()
    {
        var board = new BoardService();
        var normal = board.AddText("normal");
        var pinned = board.AddText("pinned");
        board.SetPinnedMany([pinned.Id], true);

        Assert.IsTrue(board.CanMoveMany([pinned.Id], BoardCategory.Inbox, 1));
        Assert.IsFalse(board.CanMoveMany([pinned.Id], BoardCategory.Inbox, 2));
        Assert.IsTrue(board.CanMoveMany([normal.Id], BoardCategory.Inbox, 1));
        Assert.IsFalse(board.CanMoveMany([normal.Id], BoardCategory.Inbox, 0));
    }

    [TestMethod]
    public void Remove_ReturnsEnoughStateToRestoreExactPosition()
    {
        var board = new BoardService();
        var older = board.AddText("旧", Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var newer = board.AddText("新", Guid.Parse("00000000-0000-0000-0000-000000000002"));

        var removed = board.Remove(newer.Id);
        Assert.IsNotNull(removed);

        board.Restore(removed.Value);

        CollectionAssert.AreEqual(
            new[] { newer.Id, older.Id },
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public void RemoveCategory_ReturnsWholeCategoryAndRestoresExactOrder()
    {
        var board = new BoardService();
        var older = board.AddText(
            "较早提示词",
            BoardCategory.Prompt,
            Guid.Parse("00000000-0000-0000-0000-000000000041"));
        var newer = board.AddText(
            "较新提示词",
            BoardCategory.Prompt,
            Guid.Parse("00000000-0000-0000-0000-000000000042"));
        var inbox = board.AddText("其他分类保留");
        var original = board.Items(BoardCategory.Prompt).Select(item => item.Id).ToArray();

        var removed = board.RemoveCategory(BoardCategory.Prompt);

        Assert.AreEqual(BoardCategory.Prompt, removed.Category);
        CollectionAssert.AreEqual(original, removed.Items.Select(item => item.Id).ToArray());
        Assert.AreEqual(0, board.Items(BoardCategory.Prompt).Count);
        Assert.AreSame(inbox, board.Items(BoardCategory.Inbox).Single());

        board.Restore(removed);

        CollectionAssert.AreEqual(
            new[] { newer.Id, older.Id },
            board.Items(BoardCategory.Prompt).Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { 0, 1 },
            board.Items(BoardCategory.Prompt).Select(item => item.Order).ToArray());
        Assert.IsTrue(board.Items(BoardCategory.Prompt).All(
            item => item.Category == BoardCategory.Prompt));
    }

    [TestMethod]
    public void Snapshot_PreservesPinnedStateAndIsDetachedFromLaterMutations()
    {
        var board = new BoardService();
        var item = board.AddText("内容", Guid.Parse("00000000-0000-0000-0000-000000000004"));
        item.IsPinned = true;
        var snapshot = board.CreateSnapshot();

        board.Move(item.Id, BoardCategory.Prompt, 0);
        item.IsPinned = false;

        Assert.AreEqual(BoardCategory.Inbox, snapshot.Items.Single().Category);
        Assert.IsTrue(snapshot.Items.Single().IsPinned);
    }

    [TestMethod]
    public void BoardItem_IsPinnedRaisesOneChangeNotificationPerActualChange()
    {
        var item = BoardItem.CreateText("内容", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var changed = new List<string?>();
        item.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        item.IsPinned = true;
        item.IsPinned = true;

        CollectionAssert.AreEqual(new[] { nameof(BoardItem.IsPinned) }, changed);
    }

    [TestMethod]
    public void WindowSettings_NormalizeKeepsWindowInsideWorkArea()
    {
        var normalized = new WindowSettings(900, 900, -20).Normalize(500, 400);

        Assert.AreEqual(442, normalized.PanelWidth);
        Assert.AreEqual(400, normalized.WindowHeight);
        Assert.AreEqual(0, normalized.Top);
    }

    [TestMethod]
    public void WindowSettings_ResetToDefaultRestoresSizeAndVerticalPosition()
    {
        var customized = new WindowSettings(520, 410, 370);

        var reset = customized.ResetToDefault(1920, 1040);

        Assert.AreEqual(WindowSettings.Default, reset);
    }

    [TestMethod]
    public void WindowSettings_ResetToDefaultNormalizesAgainstCurrentWorkArea()
    {
        var customized = new WindowSettings(520, 410, 370);

        var reset = customized.ResetToDefault(340, 500);

        Assert.AreEqual(WindowSettings.Default.Normalize(340, 500), reset);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Restore_NormalizesSparseAndDuplicateOrderValues()
    {
        var board = new BoardService();
        board.Restore(new BoardSnapshot
        {
            Items =
            [
                BoardItem.CreateText("后", Guid.Parse("00000000-0000-0000-0000-000000000011"), DateTimeOffset.Parse("2026-08-09T00:00:02Z")),
                BoardItem.CreateText("前", Guid.Parse("00000000-0000-0000-0000-000000000010"), DateTimeOffset.Parse("2026-08-09T00:00:01Z"))
            ]
        });

        CollectionAssert.AreEqual(
            new[] { 0, 1 },
            board.Items(BoardCategory.Inbox).Select(item => item.Order).ToArray());
    }

    [TestMethod]
    public void Restore_PartitionsPinsBeforeNormalWhilePreservingEachOrder()
    {
        var pinnedLater = BoardItem.CreateText("pinned later", Guid.NewGuid(), DateTimeOffset.UtcNow);
        pinnedLater.Category = BoardCategory.Prompt;
        pinnedLater.Order = 3;
        pinnedLater.IsPinned = true;
        var normalFirst = BoardItem.CreateText("normal first", Guid.NewGuid(), DateTimeOffset.UtcNow);
        normalFirst.Category = BoardCategory.Prompt;
        normalFirst.Order = 0;
        var pinnedFirst = BoardItem.CreateText("pinned first", Guid.NewGuid(), DateTimeOffset.UtcNow);
        pinnedFirst.Category = BoardCategory.Prompt;
        pinnedFirst.Order = 1;
        pinnedFirst.IsPinned = true;
        var normalLater = BoardItem.CreateText("normal later", Guid.NewGuid(), DateTimeOffset.UtcNow);
        normalLater.Category = BoardCategory.Prompt;
        normalLater.Order = 2;
        var board = new BoardService();

        board.Restore(new BoardSnapshot
        {
            Items = [normalFirst, pinnedFirst, normalLater, pinnedLater]
        });

        CollectionAssert.AreEqual(
            new[] { pinnedFirst.Id, pinnedLater.Id, normalFirst.Id, normalLater.Id },
            board.Items(BoardCategory.Prompt).Select(item => item.Id).ToArray());
        Assert.IsTrue(normalFirst.StartsNormalRegion);
    }
}
