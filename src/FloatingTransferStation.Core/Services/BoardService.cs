using System.Collections.ObjectModel;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public enum BoardMoveDisposition
{
    Invalid,
    NoChange,
    Changed
}

public sealed record BoardBatchMove(
    BoardMoveDisposition Disposition,
    BoardCategory SourceCategory,
    BoardCategory TargetCategory,
    IReadOnlyList<BoardItem> OriginalSourceItems,
    IReadOnlyList<BoardItem>? OriginalTargetItems)
{
    public bool Changed => Disposition == BoardMoveDisposition.Changed;
    public bool IsValid => Disposition != BoardMoveDisposition.Invalid;
}

public sealed record BoardPinChange(
    bool Changed,
    BoardCategory Category,
    IReadOnlyList<BoardItem> OriginalItems,
    IReadOnlyList<bool> OriginalPinStates);

public sealed record RemovedBoardItems(
    IReadOnlyDictionary<BoardCategory, IReadOnlyList<BoardItem>> OriginalCategories,
    IReadOnlyList<BoardItem> RemovedItems);

public sealed record RemovedBoardCategory(
    BoardCategory Category,
    IReadOnlyList<BoardItem> Items);

public sealed class BoardService
{
    private readonly Dictionary<BoardCategory, ObservableCollection<BoardItem>> _items =
        BoardCategoryCatalog.Ordered.ToDictionary(
            category => category,
            _ => new ObservableCollection<BoardItem>());

    public ObservableCollection<BoardItem> Items(BoardCategory category) => _items[category];

    public BoardItem AddText(string text, Guid? id = null, DateTimeOffset? createdAt = null) =>
        AddText(text, BoardCategory.Inbox, id, createdAt);

    public BoardItem AddText(
        string text,
        BoardCategory category,
        Guid? id = null,
        DateTimeOffset? createdAt = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text must contain visible content.", nameof(text));
        }

        var item = BoardItem.CreateText(text, id ?? Guid.NewGuid(), createdAt ?? DateTimeOffset.UtcNow);
        InsertAtTop(item, category);
        return item;
    }

    public BoardItem AddImage(
        Guid id,
        string relativePath,
        string absolutePath,
        DateTimeOffset? createdAt = null) =>
        AddImage(id, relativePath, absolutePath, BoardCategory.Inbox, createdAt);

    public BoardItem AddImage(
        Guid id,
        string relativePath,
        string absolutePath,
        BoardCategory category,
        DateTimeOffset? createdAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        var item = BoardItem.CreateImage(id, relativePath, absolutePath, createdAt ?? DateTimeOffset.UtcNow);
        InsertAtTop(item, category);
        return item;
    }

    public BoardPinChange SetPinnedMany(
        IReadOnlyCollection<Guid> itemIds,
        bool isPinned)
    {
        var context = ResolveBatch(itemIds);
        var originalStates = context.SourceBefore
            .Select(item => item.IsPinned)
            .ToArray();
        if (context.OrderedItems.All(item => item.IsPinned == isPinned))
        {
            return new BoardPinChange(
                false,
                context.SourceCategory,
                context.SourceBefore,
                originalStates);
        }

        foreach (var item in context.OrderedItems)
        {
            item.IsPinned = isPinned;
        }

        var remaining = context.SourceBefore
            .Where(item => !context.SelectedIds.Contains(item.Id))
            .ToArray();
        var sourceAfter = isPinned
            ? context.OrderedItems
                .Concat(remaining.Where(item => item.IsPinned))
                .Concat(remaining.Where(item => !item.IsPinned))
            : remaining.Where(item => item.IsPinned)
                .Concat(context.OrderedItems)
                .Concat(remaining.Where(item => !item.IsPinned));
        ReorderCategory(context.SourceCategory, sourceAfter);
        return new BoardPinChange(
            true,
            context.SourceCategory,
            context.SourceBefore,
            originalStates);
    }

    public void Undo(BoardPinChange change)
    {
        for (var index = 0; index < change.OriginalItems.Count; index++)
        {
            change.OriginalItems[index].IsPinned = change.OriginalPinStates[index];
        }

        ReorderCategory(change.Category, change.OriginalItems);
    }

    public BoardBatchMove MoveMany(
        IReadOnlyCollection<Guid> itemIds,
        BoardCategory targetCategory,
        int targetIndex)
    {
        if (!BoardCategoryCatalog.IsDefined(targetCategory))
        {
            throw new ArgumentOutOfRangeException(nameof(targetCategory));
        }

        var context = ResolveBatch(itemIds);
        var remaining = context.SourceBefore
            .Where(item => !context.SelectedIds.Contains(item.Id))
            .ToList();
        if (context.SourceCategory == targetCategory)
        {
            if (!IsSameCategoryTargetValid(context, targetIndex))
            {
                return new BoardBatchMove(
                    BoardMoveDisposition.Invalid,
                    context.SourceCategory,
                    targetCategory,
                    context.SourceBefore,
                    null);
            }

            var preRemovalIndex = Math.Clamp(targetIndex, 0, context.SourceBefore.Length);
            var removedBefore = context.SourceBefore
                .Take(preRemovalIndex)
                .Count(item => context.SelectedIds.Contains(item.Id));
            var insertionIndex = Math.Clamp(
                preRemovalIndex - removedBefore,
                0,
                remaining.Count);
            var sourceAfter = remaining
                .Take(insertionIndex)
                .Concat(context.OrderedItems)
                .Concat(remaining.Skip(insertionIndex))
                .ToArray();
            if (context.SourceBefore.Select(item => item.Id)
                .SequenceEqual(sourceAfter.Select(item => item.Id)))
            {
                return new BoardBatchMove(
                    BoardMoveDisposition.NoChange,
                    context.SourceCategory,
                    targetCategory,
                    context.SourceBefore,
                    null);
            }

            ReplaceCategory(context.SourceCategory, sourceAfter);
            return new BoardBatchMove(
                BoardMoveDisposition.Changed,
                context.SourceCategory,
                targetCategory,
                context.SourceBefore,
                null);
        }

        var targetBefore = _items[targetCategory].ToArray();
        var sourceAfterCrossCategory = remaining.ToArray();
        var targetAfter = context.OrderedItems
            .Where(item => item.IsPinned)
            .Concat(targetBefore.Where(item => item.IsPinned))
            .Concat(context.OrderedItems.Where(item => !item.IsPinned))
            .Concat(targetBefore.Where(item => !item.IsPinned))
            .ToArray();

        ReplaceCategory(context.SourceCategory, sourceAfterCrossCategory);
        ReplaceCategory(targetCategory, targetAfter);
        return new BoardBatchMove(
            BoardMoveDisposition.Changed,
            context.SourceCategory,
            targetCategory,
            context.SourceBefore,
            targetBefore);
    }

    public BoardBatchMove MoveManyToCategoryTop(
        IReadOnlyCollection<Guid> itemIds,
        BoardCategory targetCategory)
    {
        if (!BoardCategoryCatalog.IsDefined(targetCategory))
        {
            throw new ArgumentOutOfRangeException(nameof(targetCategory));
        }

        var context = ResolveBatch(itemIds);
        if (context.SourceCategory != targetCategory)
        {
            return MoveMany(itemIds, targetCategory, 0);
        }

        if (context.OrderedItems.Select(item => item.IsPinned).Distinct().Count() != 1)
        {
            return new BoardBatchMove(
                BoardMoveDisposition.Invalid,
                context.SourceCategory,
                targetCategory,
                context.SourceBefore,
                null);
        }

        var targetIndex = context.OrderedItems[0].IsPinned
            ? 0
            : context.SourceBefore.Count(item => item.IsPinned);
        return MoveMany(itemIds, targetCategory, targetIndex);
    }

    public bool CanMoveMany(
        IReadOnlyCollection<Guid> itemIds,
        BoardCategory targetCategory,
        int targetIndex)
    {
        try
        {
            if (!BoardCategoryCatalog.IsDefined(targetCategory))
            {
                return false;
            }

            var context = ResolveBatch(itemIds);
            return context.SourceCategory != targetCategory ||
                IsSameCategoryTargetValid(context, targetIndex);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    public bool CanMoveManyToCategoryTop(
        IReadOnlyCollection<Guid> itemIds,
        BoardCategory targetCategory)
    {
        try
        {
            if (!BoardCategoryCatalog.IsDefined(targetCategory))
            {
                return false;
            }

            var context = ResolveBatch(itemIds);
            return context.SourceCategory != targetCategory ||
                context.OrderedItems.Select(item => item.IsPinned).Distinct().Count() == 1;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    public void Undo(BoardBatchMove move)
    {
        ArgumentNullException.ThrowIfNull(move);
        if (!move.Changed)
        {
            return;
        }

        ReplaceCategory(move.SourceCategory, move.OriginalSourceItems);
        if (move.SourceCategory != move.TargetCategory)
        {
            ReplaceCategory(move.TargetCategory, move.OriginalTargetItems!);
        }
    }

    public void Remove(Guid itemId)
    {
        foreach (var category in BoardCategoryCatalog.Ordered)
        {
            var collection = _items[category];
            var index = IndexOf(collection, itemId);
            if (index < 0)
            {
                continue;
            }

            collection.RemoveAt(index);
            Reindex(category);
            return;
        }
    }

    public RemovedBoardItems? RemoveMany(IReadOnlyCollection<Guid> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (itemIds.Count == 0 || itemIds.Count != itemIds.Distinct().Count())
        {
            return null;
        }

        var selected = itemIds.ToHashSet();
        var originals = BoardCategoryCatalog.Ordered
            .Where(category => _items[category].Any(item => selected.Contains(item.Id)))
            .ToDictionary(
                category => category,
                category => (IReadOnlyList<BoardItem>)_items[category].ToArray());
        var removed = originals.Values
            .SelectMany(items => items)
            .Where(item => selected.Contains(item.Id))
            .ToArray();
        if (removed.Length != selected.Count)
        {
            return null;
        }

        foreach (var (category, original) in originals)
        {
            ReplaceCategory(
                category,
                original.Where(item => !selected.Contains(item.Id)));
        }

        return new RemovedBoardItems(originals, removed);
    }

    public void Restore(RemovedBoardItems removed)
    {
        ArgumentNullException.ThrowIfNull(removed);
        foreach (var (category, items) in removed.OriginalCategories)
        {
            ReplaceCategory(category, items);
        }
    }

    /// <summary>
    /// 把删除的条目插回当前面板:优先插到删除前同区(置顶/普通)的前驱邻居之后,
    /// 前驱缺失或已变区时回退到该区顶部;同一批删除保持原始相对顺序。与
    /// <see cref="Restore(RemovedBoardItems)"/> 的整类替换不同,本方法不回退
    /// 删除之后发生的其他改动(新增、移动、重新置顶),适合延迟恢复(撤销)。
    /// </summary>
    public void RestoreInsert(RemovedBoardItems removed)
    {
        ArgumentNullException.ThrowIfNull(removed);
        var removedIds = removed.RemovedItems.Select(item => item.Id).ToHashSet();
        foreach (var (category, originalItems) in removed.OriginalCategories)
        {
            var collection = _items[category];
            BoardItem? chainedAnchor = null;
            foreach (var item in originalItems.Where(item => removedIds.Contains(item.Id)))
            {
                // 链式锚点只在同区(置顶/普通)内生效,避免普通内容跟在置顶链后落入置顶区。
                var anchor = FindSurvivingAnchor(originalItems, item, removedIds, collection)
                    ?? (chainedAnchor is { } chain && chain.IsPinned == item.IsPinned ? chain : null);
                var index = anchor is null
                    ? (item.IsPinned ? 0 : FirstNormalIndex(collection))
                    : IndexOf(collection, anchor.Id) + 1;
                collection.Insert(Math.Clamp(index, 0, collection.Count), item);
                chainedAnchor = item;
            }

            Reindex(category);
        }
    }

    private BoardItem? FindSurvivingAnchor(
        IReadOnlyList<BoardItem> originalItems,
        BoardItem removedItem,
        HashSet<Guid> removedIds,
        ObservableCollection<BoardItem> current)
    {
        BoardItem? anchor = null;
        foreach (var candidate in originalItems)
        {
            if (candidate.Id == removedItem.Id)
            {
                break;
            }

            if (!removedIds.Contains(candidate.Id) &&
                candidate.IsPinned == removedItem.IsPinned &&
                IndexOf(current, candidate.Id) >= 0)
            {
                anchor = candidate;
            }
        }

        return anchor;
    }

    private static int FirstNormalIndex(ObservableCollection<BoardItem> collection)
    {
        for (var index = 0; index < collection.Count; index++)
        {
            if (!collection[index].IsPinned)
            {
                return index;
            }
        }

        return collection.Count;
    }

    public RemovedBoardCategory RemoveCategory(BoardCategory category)
    {
        if (!BoardCategoryCatalog.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        var collection = _items[category];
        var removed = collection.ToArray();
        collection.Clear();
        return new RemovedBoardCategory(category, removed);
    }

    public void Restore(RemovedBoardCategory removed)
    {
        if (!BoardCategoryCatalog.IsDefined(removed.Category))
        {
            throw new ArgumentOutOfRangeException(nameof(removed));
        }

        var collection = _items[removed.Category];
        foreach (var item in removed.Items.OrderBy(item => item.Order))
        {
            item.Category = removed.Category;
            collection.Insert(Math.Clamp(item.Order, 0, collection.Count), item);
        }

        Reindex(removed.Category);
    }

    public void Restore(BoardSnapshot snapshot)
    {
        foreach (var collection in _items.Values)
        {
            collection.Clear();
        }

        foreach (var category in BoardCategoryCatalog.Ordered)
        {
            foreach (var item in snapshot.Items
                         .Where(item => item.Category == category)
                         .OrderByDescending(item => item.IsPinned)
                         .ThenBy(item => item.Order)
                         .ThenBy(item => item.CreatedAt))
            {
                _items[category].Add(item);
            }

            Reindex(category);
        }
    }

    public BoardSnapshot CreateSnapshot() => new()
    {
        Items = BoardCategoryCatalog.Ordered
            .SelectMany(category => _items[category])
            .Select(item => item.CloneForSnapshot())
            .ToList()
    };

    private void InsertAtTop(BoardItem item, BoardCategory category)
    {
        if (!BoardCategoryCatalog.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        var collection = _items[category];
        var normalStart = collection.TakeWhile(candidate => candidate.IsPinned).Count();
        collection.Insert(normalStart, item);
        Reindex(category);
    }

    private BatchContext ResolveBatch(IReadOnlyCollection<Guid> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (itemIds.Count == 0 || itemIds.Count != itemIds.Distinct().Count())
        {
            throw new ArgumentException(
                "Batch IDs must be non-empty and unique.",
                nameof(itemIds));
        }

        var selectedIds = itemIds.ToHashSet();
        var matches = BoardCategoryCatalog.Ordered
            .Select(category => (
                Category: category,
                Items: _items[category]
                    .Where(item => selectedIds.Contains(item.Id))
                    .ToArray()))
            .Where(match => match.Items.Length > 0)
            .ToArray();
        if (matches.Sum(match => match.Items.Length) != selectedIds.Count)
        {
            throw new KeyNotFoundException("One or more board items were not found.");
        }

        if (matches.Length != 1)
        {
            throw new ArgumentException(
                "All batch items must share one source category.",
                nameof(itemIds));
        }

        var sourceBefore = _items[matches[0].Category].ToArray();
        return new BatchContext(
            matches[0].Category,
            sourceBefore,
            sourceBefore.Where(item => selectedIds.Contains(item.Id)).ToArray(),
            selectedIds);
    }

    private static bool IsSameCategoryTargetValid(
        BatchContext context,
        int targetIndex)
    {
        if (context.OrderedItems.Select(item => item.IsPinned).Distinct().Count() != 1)
        {
            return false;
        }

        var pinnedCount = context.SourceBefore.Count(item => item.IsPinned);
        var clamped = Math.Clamp(targetIndex, 0, context.SourceBefore.Length);
        return context.OrderedItems[0].IsPinned
            ? clamped <= pinnedCount
            : clamped >= pinnedCount;
    }

    private static int IndexOf(IList<BoardItem> collection, Guid itemId)
    {
        for (var index = 0; index < collection.Count; index++)
        {
            if (collection[index].Id == itemId)
            {
                return index;
            }
        }

        return -1;
    }

    private void ReplaceCategory(
        BoardCategory category,
        IEnumerable<BoardItem> items)
    {
        var collection = _items[category];
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }

        Reindex(category);
    }

    private void ReorderCategory(
        BoardCategory category,
        IEnumerable<BoardItem> items)
    {
        var collection = _items[category];
        var desired = items.ToArray();
        if (desired.Length != collection.Count)
        {
            throw new InvalidOperationException("A reorder must preserve category membership.");
        }

        for (var targetIndex = 0; targetIndex < desired.Length; targetIndex++)
        {
            var item = desired[targetIndex];
            if (collection[targetIndex].Id == item.Id)
            {
                continue;
            }

            var currentIndex = IndexOf(collection, item.Id);
            if (currentIndex < 0)
            {
                throw new InvalidOperationException("A reorder must preserve category membership.");
            }

            if (currentIndex != targetIndex)
            {
                collection.Move(currentIndex, targetIndex);
            }
        }

        Reindex(category);
    }

    private void Reindex(BoardCategory category)
    {
        var collection = _items[category];
        for (var index = 0; index < collection.Count; index++)
        {
            collection[index].Category = category;
            collection[index].Order = index;
            collection[index].StartsNormalRegion =
                index > 0 &&
                !collection[index].IsPinned &&
                collection[index - 1].IsPinned;
        }
    }

    private sealed record BatchContext(
        BoardCategory SourceCategory,
        BoardItem[] SourceBefore,
        BoardItem[] OrderedItems,
        HashSet<Guid> SelectedIds);
}
