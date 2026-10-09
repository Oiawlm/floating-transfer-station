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
    private readonly Dictionary<BoardCategory, BoardItemCollection> _items =
        BoardCategoryCatalog.Ordered.ToDictionary(
            category => category,
            _ => new BoardItemCollection());

    public BoardItemCollection Items(BoardCategory category) => _items[category];

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

    /// <summary>
    /// 就地更新文字卡片内容(用户显式编辑)。仅改内容,不动分类、顺序与置顶;
    /// 找不到条目或条目不是文字卡时返回 false。
    /// </summary>
    public bool UpdateText(Guid itemId, string text)
    {
        var item = FindItem(itemId);
        if (item is not { Kind: BoardItemKind.Text })
        {
            return false;
        }

        item.Text = text;
        return true;
    }

    public BoardItem? FindItem(Guid itemId)
    {
        foreach (var collection in _items.Values)
        {
            BoardItem? found = null;
            foreach (var candidate in collection)
            {
                if (candidate.Id == itemId)
                {
                    found = candidate;
                    break;
                }
            }

            if (found is not null)
            {
                return found;
            }
        }

        return null;
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
    /// 位置计算在当前状态的副本上完成后一次整批替换,只发出单个 Reset。
    /// </summary>
    public void RestoreInsert(RemovedBoardItems removed)
    {
        ArgumentNullException.ThrowIfNull(removed);
        var removedIds = removed.RemovedItems.Select(item => item.Id).ToHashSet();
        foreach (var (category, originalItems) in removed.OriginalCategories)
        {
            var working = _items[category].ToList();
            var survivingIds = new HashSet<Guid>();
            foreach (var item in working)
            {
                survivingIds.Add(item.Id);
            }

            BoardItem? chainedAnchor = null;
            foreach (var item in originalItems.Where(item => removedIds.Contains(item.Id)))
            {
                // 链式锚点只在同区(置顶/普通)内生效,避免普通内容跟在置顶链后落入置顶区。
                var anchor = FindSurvivingAnchor(originalItems, item, removedIds, survivingIds)
                    ?? (chainedAnchor is { } chain && chain.IsPinned == item.IsPinned ? chain : null);
                var index = anchor is null
                    ? (item.IsPinned ? 0 : FirstNormalIndex(working))
                    : IndexOf(working, anchor.Id) + 1;
                working.Insert(Math.Clamp(index, 0, working.Count), item);
                survivingIds.Add(item.Id);
                chainedAnchor = item;
            }

            _items[category].ReplaceAll(working);
            Reindex(category);
        }
    }

    private static BoardItem? FindSurvivingAnchor(
        IReadOnlyList<BoardItem> originalItems,
        BoardItem removedItem,
        HashSet<Guid> removedIds,
        HashSet<Guid> survivingIds)
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
                survivingIds.Contains(candidate.Id))
            {
                anchor = candidate;
            }
        }

        return anchor;
    }

    private static int FirstNormalIndex(IReadOnlyList<BoardItem> collection)
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

    /// <summary>
    /// 只移除分类内的非置顶条目,置顶区顺序原样保留;返回被移除内容与原分类完整
    /// 顺序(供保存失败整批回滚与延迟恢复复用)。没有非置顶条目时为空操作,
    /// 返回 RemovedItems 为空的记录,不触发任何集合变更。
    /// </summary>
    public RemovedBoardItems RemoveNonPinned(BoardCategory category)
    {
        if (!BoardCategoryCatalog.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        var original = _items[category].ToArray();
        var removed = original.Where(item => !item.IsPinned).ToArray();
        if (removed.Length > 0)
        {
            ReplaceCategory(category, original.Where(item => item.IsPinned));
        }

        return new RemovedBoardItems(
            new Dictionary<BoardCategory, IReadOnlyList<BoardItem>>
            {
                [category] = original
            },
            removed);
    }

    /// <summary>
    /// 跨分类版本的 <see cref="RemoveNonPinned(BoardCategory)"/>：只移除所给分类内的
    /// 非置顶条目（各分类置顶区在前、普通区原顺序），未给出的分类不动；组合为单个
    /// <see cref="RemovedBoardItems"/>（与 RemoveMany 一致，只收录发生移除的分类），
    /// 供调用方单次保存、失败整批回滚与整批撤销。没有非置顶条目时为空操作。
    /// 同一分类重复出现天然幂等（第二遍已无非置顶可移）。
    /// </summary>
    public RemovedBoardItems RemoveNonPinned(IReadOnlyCollection<BoardCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        foreach (var category in categories)
        {
            if (!BoardCategoryCatalog.IsDefined(category))
            {
                throw new ArgumentOutOfRangeException(nameof(categories));
            }
        }

        var originals = new Dictionary<BoardCategory, IReadOnlyList<BoardItem>>();
        var removedItems = new List<BoardItem>();
        foreach (var category in categories)
        {
            if (originals.ContainsKey(category))
            {
                continue;
            }

            var original = _items[category].ToArray();
            var removed = original.Where(item => !item.IsPinned).ToArray();
            if (removed.Length == 0)
            {
                continue;
            }

            ReplaceCategory(category, original.Where(item => item.IsPinned));
            originals[category] = original;
            removedItems.AddRange(removed);
        }

        return new RemovedBoardItems(originals, removedItems);
    }

    public void Restore(RemovedBoardCategory removed)
    {
        if (!BoardCategoryCatalog.IsDefined(removed.Category))
        {
            throw new ArgumentOutOfRangeException(nameof(removed));
        }

        // 空分类上按 Order 升序逐条钳位插入,等价于按序整批替换。
        var ordered = removed.Items.OrderBy(item => item.Order).ToArray();
        foreach (var item in ordered)
        {
            item.Category = removed.Category;
        }

        _items[removed.Category].ReplaceAll(ordered);
        Reindex(removed.Category);
    }

    public void Restore(BoardSnapshot snapshot)
    {
        foreach (var category in BoardCategoryCatalog.Ordered)
        {
            _items[category].ReplaceAll(snapshot.Items
                .Where(item => item.Category == category)
                .OrderByDescending(item => item.IsPinned)
                .ThenBy(item => item.Order)
                .ThenBy(item => item.CreatedAt));
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
        _items[category].ReplaceAll(items);
        Reindex(category);
    }

    private void ReorderCategory(
        BoardCategory category,
        IEnumerable<BoardItem> items)
    {
        var desired = items as IList<BoardItem> ?? items.ToList();
        var collection = _items[category];
        if (!PreservesMembership(collection, desired))
        {
            throw new InvalidOperationException("A reorder must preserve category membership.");
        }

        collection.ReplaceAll(desired);
        Reindex(category);
    }

    /// <summary>成员一致性校验：目标序列与本分类包含完全相同的条目集合（O(N)）。</summary>
    private static bool PreservesMembership(IList<BoardItem> current, IList<BoardItem> desired)
    {
        if (desired.Count != current.Count)
        {
            return false;
        }

        var ids = new HashSet<Guid>();
        foreach (var item in desired)
        {
            // 重复 Id 在合法面板状态中不存在；出现重复直接视为破坏成员关系。
            if (!ids.Add(item.Id))
            {
                return false;
            }
        }

        for (var index = 0; index < current.Count; index++)
        {
            if (!ids.Contains(current[index].Id))
            {
                return false;
            }
        }

        return true;
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
