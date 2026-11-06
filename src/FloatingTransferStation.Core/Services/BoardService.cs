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

/// <summary>子分组批量指派的结果（契约 #57，照 <see cref="BoardPinChange"/> 惯例）：
/// 批量条目、目标槽与原始槽快照（供失败还原与撤销复用）。</summary>
public sealed record BoardSubgroupAssignment(
    bool Changed,
    BoardCategory Category,
    int Slot,
    IReadOnlyList<BoardItem> Items,
    IReadOnlyList<int?> OriginalSlots);

/// <summary>子分组稳定分区的结果（契约 #57）：分类整序快照（含置顶区原样），
/// Changed=false 表示已按槽聚簇（no-op）。</summary>
public sealed record BoardSubgroupPartition(
    bool Changed,
    BoardCategory Category,
    IReadOnlyList<BoardItem> OriginalOrder);

public sealed class BoardService
{
    private readonly Dictionary<BoardCategory, BoardItemCollection> _items =
        BoardCategoryCatalog.Ordered.ToDictionary(
            category => category,
            _ => new BoardItemCollection());

    // 子分组派生源（契约 #57）：呈现模式与名清单快照由窗口层经
    // SetSubgroupDisplay 喂入（ApplyPreferences/装载唯一管线）；settings 仍是
    // 名单持久化单一事实源，服务侧仅派生缓存。
    private SubgroupDisplayMode _subgroupMode = SubgroupDisplayMode.Off;
    private WindowSettings? _subgroupNamesSource;

    public BoardItemCollection Items(BoardCategory category) => _items[category];

    /// <summary>
    /// 喂入子分组呈现模式与名清单快照（契约 #57 唯一入口）：模式变化即全分类
    /// Reindex 刷新派生标记（StartsSubgroupRegion 仅 GroupedView 置位，Off/
    /// ChipRail 恒 false）；settings 给出时更新名清单快照（组头与轨名共用）。
    /// </summary>
    public void SetSubgroupDisplay(SubgroupDisplayMode mode, WindowSettings? settings = null)
    {
        _subgroupMode = mode;
        if (settings is not null)
        {
            _subgroupNamesSource = settings;
        }

        foreach (var category in BoardCategoryCatalog.Ordered)
        {
            Reindex(category);
        }
    }

    /// <summary>
    /// 批量指派子分组槽（契约 #57，形态①拖卡归属的唯一数据落点）：只改槽字段、
    /// 不动顺序（物理重排只由分区变更与用户拖拽写入）；同槽幂等返回 Changed=false。
    /// </summary>
    public BoardSubgroupAssignment AssignSubgroup(
        IReadOnlyCollection<Guid> itemIds,
        int slot)
    {
        if (!SubgroupCatalog.IsValidSlot(slot))
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, null);
        }

        var context = ResolveBatch(itemIds);
        var originalSlots = context.OrderedItems
            .Select(item => item.Subgroup)
            .ToArray();
        if (context.OrderedItems.All(item => item.Subgroup == slot))
        {
            return new BoardSubgroupAssignment(
                false,
                context.SourceCategory,
                slot,
                context.OrderedItems,
                originalSlots);
        }

        foreach (var item in context.OrderedItems)
        {
            item.Subgroup = slot;
        }

        Reindex(context.SourceCategory);
        return new BoardSubgroupAssignment(
            true,
            context.SourceCategory,
            slot,
            context.OrderedItems,
            originalSlots);
    }

    public void Undo(BoardSubgroupAssignment change)
    {
        if (!change.Changed)
        {
            return;
        }

        for (var index = 0; index < change.Items.Count; index++)
        {
            change.Items[index].Subgroup = change.OriginalSlots[index];
        }

        Reindex(change.Category);
    }

    /// <summary>
    /// 普通区按子分组槽一次稳定分区（契约 #57，形态②进入时的整队）：槽序升序
    /// （未指定 null 居首）→组内既有相对序（LINQ OrderBy 稳定排序承载），经
    /// Reindex 重写持久化 Order；置顶区不动（置顶优先不分组）。已按槽聚簇时
    /// no-op（Changed=false，不写盘）。
    /// </summary>
    public BoardSubgroupPartition PartitionNormalRegionBySubgroup(BoardCategory category)
    {
        if (!BoardCategoryCatalog.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, null);
        }

        var current = _items[category].ToArray();
        var pinnedCount = current.TakeWhile(item => item.IsPinned).Count();
        var normal = current.Skip(pinnedCount).ToArray();
        // null（未指定）→0 居首，槽 0–3 →1–4 升序；OrderBy 稳定＝组内既有相对序。
        var partitioned = normal
            .OrderBy(item => item.Subgroup is { } slot ? slot + 1 : 0)
            .ToArray();
        if (partitioned.SequenceEqual(normal))
        {
            return new BoardSubgroupPartition(false, category, current);
        }

        ReorderCategory(category, current.Take(pinnedCount).Concat(partitioned));
        return new BoardSubgroupPartition(true, category, current);
    }

    public void Undo(BoardSubgroupPartition change)
    {
        if (!change.Changed)
        {
            return;
        }

        ReorderCategory(change.Category, change.OriginalOrder);
    }

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

            // NoChange/Changed 判定经 PlanSameCategoryMove 单一实现（与
            // IsNoChangeMove 同源——指示线「同位不显示」与 Drop 落点共用同一谓词）。
            var plan = PlanSameCategoryMove(
                context.SourceBefore,
                context.SelectedIds,
                context.OrderedItems,
                targetIndex);
            if (plan.NoChange)
            {
                return new BoardBatchMove(
                    BoardMoveDisposition.NoChange,
                    context.SourceCategory,
                    targetCategory,
                    context.SourceBefore,
                    null);
            }

            ReplaceCategory(context.SourceCategory, plan.SourceAfter);
            return new BoardBatchMove(
                BoardMoveDisposition.Changed,
                context.SourceCategory,
                targetCategory,
                context.SourceBefore,
                null);
        }

        var targetBefore = _items[targetCategory].ToArray();
        var sourceAfterCrossCategory = context.SourceBefore
            .Where(item => !context.SelectedIds.Contains(item.Id))
            .ToArray();
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

    /// <summary>
    /// 同位判定（单一事实源，契约 #51 增补）：该落点对当前批量是否不改变分类内顺序
    /// ——与 <see cref="MoveMany"/> 的 NoChange 判定共用 <see cref="PlanSameCategoryMove"/>
    /// 同一实现（pre-removal 索引语义），指示线据此在整个 NoChange 区隐藏。
    /// 跨分类（必为真实移动）、非法落点或无法解析的批量返回 false；批量拖动按整批判定。
    /// </summary>
    public bool IsNoChangeMove(
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
            if (context.SourceCategory != targetCategory ||
                !IsSameCategoryTargetValid(context, targetIndex))
            {
                return false;
            }

            return PlanSameCategoryMove(
                context.SourceBefore,
                context.SelectedIds,
                context.OrderedItems,
                targetIndex).NoChange;
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

    /// <summary>
    /// 就地更新文字卡片备注(1.28.0,用户显式编辑;null=删除备注)。仅改备注
    /// 元数据,不动内容、分类、顺序与置顶;备注不参与搜索/复制/拖出。找不到
    /// 条目或条目不是文字卡(图片卡无备注)时返回 false。
    /// </summary>
    public bool UpdateNote(Guid itemId, string? note)
    {
        var item = FindItem(itemId);
        if (item is not { Kind: BoardItemKind.Text })
        {
            return false;
        }

        item.Note = note;
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
    /// 「清空非置顶」移除资格的唯一权威判定（2026-11-05 起）：非置顶且无备注。
    /// 备注卡完全同置顶豁免（自动清扫与手工清空两入口一致）；<see cref="RemoveNonPinned"/>
    /// 的移除谓词与 MainWindow 手工清空前的预筛同源调用本判定，全仓禁止第二处拼写
    /// 豁免条件（年龄 cutoff 组合只属自动路径，留在谓词内）。
    /// </summary>
    public static bool IsEligibleForNonPinnedRemoval(BoardItem item) =>
        !item.IsPinned && item.Note is null;

    /// <summary>
    /// 清除非置顶条目的唯一移除管线（手工清空与自动清扫共用）：置顶区顺序原样保留，
    /// 备注卡完全同置顶豁免（2026-11-05 起；豁免资格判定的唯一权威是本类公开静态
    /// 判定，移除谓词与 MainWindow 手工清空预筛同源调用；手工清空＝清空无备注的
    /// 非置顶卡）；给出 <paramref name="expiryCutoffUtc"/> 时只移除入库满期
    /// （CreatedAt ≤ 截止）的无备注非置顶条目（逐卡 TTL，自动清扫语义，截止由所选
    /// 保留时长档位推导），为 null 时移除全部无备注的非置顶条目（手工「清空非置顶」
    /// 语义）。返回被移除内容与各分类完整顺序（供保存失败整批回滚与整批撤销复用）；
    /// 没有可移除条目时为空操作。同一分类重复出现天然幂等。
    /// </summary>
    public RemovedBoardItems RemoveNonPinned(
        IReadOnlyCollection<BoardCategory> categories,
        DateTimeOffset? expiryCutoffUtc = null)
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
            var removed = original
                .Where(item => IsEligibleForNonPinnedRemoval(item) &&
                    (expiryCutoffUtc is not { } cutoff || item.CreatedAt <= cutoff))
                .ToArray();
            if (removed.Length == 0)
            {
                continue;
            }

            // 保留集合 = 原顺序中未被移除的全部条目（置顶区、备注卡原样 + 未到期
            // 普通区），不得假设「留下的只有置顶」——TTL cutoff 下未满期普通项与
            // 备注卡同样保留。
            var removedIds = removed.Select(item => item.Id).ToHashSet();
            ReplaceCategory(category, original.Where(item => !removedIds.Contains(item.Id)));
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

    /// <summary>
    /// 同分类落点的纯规划：pre-removal 索引（移除被拖批之前的语义）换算为移除后的
    /// 插入索引并给出结果序列与 NoChange 判定。MoveMany 的执行与 IsNoChangeMove 的
    /// 谓词共用本实现，两处判定永不漂移。
    /// </summary>
    private static BoardSameCategoryPlan PlanSameCategoryMove(
        BoardItem[] sourceBefore,
        HashSet<Guid> selectedIds,
        BoardItem[] orderedItems,
        int targetIndex)
    {
        var remaining = sourceBefore
            .Where(item => !selectedIds.Contains(item.Id))
            .ToArray();
        var preRemovalIndex = Math.Clamp(targetIndex, 0, sourceBefore.Length);
        var removedBefore = sourceBefore
            .Take(preRemovalIndex)
            .Count(item => selectedIds.Contains(item.Id));
        var insertionIndex = Math.Clamp(preRemovalIndex - removedBefore, 0, remaining.Length);
        var sourceAfter = remaining
            .Take(insertionIndex)
            .Concat(orderedItems)
            .Concat(remaining.Skip(insertionIndex))
            .ToArray();
        var noChange = sourceBefore.Select(item => item.Id)
            .SequenceEqual(sourceAfter.Select(item => item.Id));
        return new BoardSameCategoryPlan(sourceAfter, noChange);
    }

    private sealed record BoardSameCategoryPlan(BoardItem[] SourceAfter, bool NoChange);

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
        var grouped = _subgroupMode == SubgroupDisplayMode.GroupedView;
        for (var index = 0; index < collection.Count; index++)
        {
            collection[index].Category = category;
            collection[index].Order = index;
            collection[index].StartsNormalRegion =
                index > 0 &&
                !collection[index].IsPinned &&
                collection[index - 1].IsPinned;
            // 子分组组边界派生（契约 #57，StartsNormalRegion 机制的泛化）：普通区
            // 内与前邻卡槽不同且非置顶转换点；仅 GroupedView 置位，Off/ChipRail
            // 恒 false（模板触发器死路，渲染与现状逐字节一致）。组头名按名清单
            // 快照逐槽解析，未指定组（Subgroup=null）无组头。
            var startsSubgroup = grouped &&
                index > 0 &&
                !collection[index].IsPinned &&
                !collection[index - 1].IsPinned &&
                collection[index].Subgroup != collection[index - 1].Subgroup;
            collection[index].StartsSubgroupRegion = startsSubgroup;
            collection[index].SubgroupHeader = startsSubgroup && collection[index].Subgroup is { } slot
                ? _subgroupNamesSource?.SubgroupName(category, slot) ?? SubgroupCatalog.DefaultName(slot)
                : null;
        }
    }

    private sealed record BatchContext(
        BoardCategory SourceCategory,
        BoardItem[] SourceBefore,
        BoardItem[] OrderedItems,
        HashSet<Guid> SelectedIds);
}
