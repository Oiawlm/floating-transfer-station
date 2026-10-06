using System.Collections.Specialized;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 批量结构变更的事件契约：删除/置顶/重排/撤销恢复都必须收敛为单个 Reset
/// 通知，不再逐条 Add/Move——每次事件都会驱动列表容器处理与入场动画调度，
/// 大数量级时是界面卡顿的直接来源。
/// </summary>
[TestClass]
public sealed class BoardServiceBatchNotificationTests
{
    [TestMethod]
    public void SetPinnedMany_RaisesSingleReset()
    {
        var board = new BoardService();
        var items = Enumerable.Range(0, 50)
            .Select(index => board.AddText($"item {index}"))
            .ToArray();
        using var events = CaptureEvents(board, BoardCategory.Inbox);

        board.SetPinnedMany([items[0].Id, items[1].Id], true);

        Assert.AreEqual("1xReset", events.ToString());
    }

    [TestMethod]
    public void UndoPinChange_RaisesSingleReset()
    {
        var board = new BoardService();
        var items = Enumerable.Range(0, 50)
            .Select(index => board.AddText($"item {index}"))
            .ToArray();
        var change = board.SetPinnedMany([items[0].Id, items[1].Id], true);
        using var events = CaptureEvents(board, BoardCategory.Inbox);

        board.Undo(change);

        Assert.AreEqual("1xReset", events.ToString());
        // AddText 插在普通区顶部，原始集合顺序与加入顺序相反。
        CollectionAssert.AreEqual(
            items.Reverse().Select(item => item.Id).ToArray(),
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public void RemoveMany_RaisesSingleResetPerAffectedCategory()
    {
        var board = new BoardService();
        var inbox = Enumerable.Range(0, 30)
            .Select(index => board.AddText($"inbox {index}"))
            .ToArray();
        _ = Enumerable.Range(0, 5)
            .Select(index => board.AddText($"prompt {index}", BoardCategory.Prompt))
            .ToArray();
        using var inboxEvents = CaptureEvents(board, BoardCategory.Inbox);
        using var promptEvents = CaptureEvents(board, BoardCategory.Prompt);

        var removed = board.RemoveMany([inbox[0].Id, inbox[7].Id]);

        Assert.IsNotNull(removed);
        Assert.AreEqual("1xReset", inboxEvents.ToString());
        Assert.AreEqual(string.Empty, promptEvents.ToString());
    }

    [TestMethod]
    public void RestoreInsert_RaisesSingleResetAndPreservesAnchorSemantics()
    {
        var board = new BoardService();
        var items = Enumerable.Range(0, 40)
            .Select(index => board.AddText($"item {index}"))
            .ToArray();
        var removed = board.RemoveMany([items[5].Id, items[6].Id]);
        Assert.IsNotNull(removed);
        // 删除后发生其他改动：再入库一条新内容。
        board.AddText("later capture");
        using var events = CaptureEvents(board, BoardCategory.Inbox);

        board.RestoreInsert(removed);

        Assert.AreEqual("1xReset", events.ToString());
        var current = board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray();
        var later = board.Items(BoardCategory.Inbox).Single(item => item.Text == "later capture");
        var index7 = Array.IndexOf(current, items[7].Id);
        var index5 = Array.IndexOf(current, items[5].Id);
        var index6 = Array.IndexOf(current, items[6].Id);
        var laterIndex = Array.IndexOf(current, later.Id);
        // 集合顺序与加入顺序相反：items[5] 的前驱幸存邻居是 items[7]（items[6] 同批删除）。
        Assert.AreEqual(index7 + 1, index5, "恢复项应插回前驱幸存邻居之后。");
        Assert.AreEqual(index5 + 1, index6, "同批删除保持原始相对顺序。");
        Assert.IsTrue(laterIndex < index5, "撤销不得回退删除之后的新增。");
    }

    [TestMethod]
    public void RestoreSnapshot_RebuildsCategoriesWithSingleResetEach()
    {
        var board = new BoardService();
        _ = board.AddText("stale", BoardCategory.Inbox);
        var snapshotItems = Enumerable.Range(0, 20)
            .Select(index => BoardItem.CreateText(
                $"restored {index}",
                Guid.NewGuid(),
                DateTimeOffset.UtcNow))
            .ToArray();
        using var inboxEvents = CaptureEvents(board, BoardCategory.Inbox);

        board.Restore(new BoardSnapshot { Items = [.. snapshotItems] });

        Assert.AreEqual("1xReset", inboxEvents.ToString());
        CollectionAssert.AreEqual(
            snapshotItems.Select(item => item.Id).ToArray(),
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray());
    }

    private static EventTally CaptureEvents(BoardService board, BoardCategory category) =>
        new(board.Items(category));

    private sealed class EventTally : IDisposable
    {
        private readonly System.Collections.ObjectModel.ObservableCollection<BoardItem> _items;
        private readonly Dictionary<NotifyCollectionChangedAction, int> _counts = [];

        public EventTally(System.Collections.ObjectModel.ObservableCollection<BoardItem> items)
        {
            _items = items;
            _items.CollectionChanged += OnChanged;
        }

        public override string ToString() => _counts.Count == 0
            ? string.Empty
            : string.Join("+", _counts.Select(pair => $"{pair.Value}x{pair.Key}"));

        private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
            _counts[e.Action] = _counts.GetValueOrDefault(e.Action) + 1;

        public void Dispose() => _items.CollectionChanged -= OnChanged;
    }
}
