using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public enum BoardBatchMoveResult
{
    Invalid,
    NoChange,
    Moved,
    Failed
}

public enum BoardPinResult
{
    Invalid,
    NoChange,
    Changed,
    Failed
}

public sealed class BoardMutationService
{
    private const int MaxUndoableBatches = 20;

    private readonly BoardService _board;
    private readonly IBoardStore _store;
    private readonly Action<string> _showStatus;
    private readonly BoardOperationGate _operationGate;
    // 有界 LIFO 撤销栈(1.41.0 泛化为可撤销操作批):尾端是最近批(撤销弹出端),
    // 头端是最旧批(驱逐端)。删除批与 AI 文本替换批共用同一条 LIFO/驱逐/丢弃语义。
    private readonly LinkedList<IUndoableBatch> _undoableBatches = new();

    public BoardMutationService(
        BoardService board,
        IBoardStore store,
        Action<string> showStatus,
        BoardOperationGate? operationGate = null)
    {
        _board = board;
        _store = store;
        _showStatus = showStatus;
        _operationGate = operationGate ?? new BoardOperationGate();
    }

    /// <summary>当前可撤销的操作批次数(会话内,用于界面提示与测试)。</summary>
    public int PendingUndoCount => _undoableBatches.Count;

    public Task<BoardBatchMoveResult> MoveManyAsync(
        IReadOnlyCollection<Guid> itemIds,
        BoardCategory targetCategory,
        int targetIndex,
        CancellationToken cancellationToken = default) =>
        PersistMoveManyAsync(
            () => _board.MoveMany(itemIds, targetCategory, targetIndex),
            cancellationToken);

    public Task<BoardBatchMoveResult> MoveManyToCategoryTopAsync(
        IReadOnlyCollection<Guid> itemIds,
        BoardCategory targetCategory,
        CancellationToken cancellationToken = default) =>
        PersistMoveManyAsync(
            () => _board.MoveManyToCategoryTop(itemIds, targetCategory),
            cancellationToken);

    private Task<BoardBatchMoveResult> PersistMoveManyAsync(
        Func<BoardBatchMove> createMove,
        CancellationToken cancellationToken) =>
        _operationGate.RunAsync(async () =>
        {
            BoardBatchMove move;
            try
            {
                move = createMove();
            }
            catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
            {
                return BoardBatchMoveResult.Invalid;
            }

            if (!move.IsValid)
            {
                return BoardBatchMoveResult.Invalid;
            }

            if (!move.Changed)
            {
                return BoardBatchMoveResult.NoChange;
            }

            try
            {
                await _store.SaveBoardAsync(_board.CreateSnapshot(), cancellationToken);
                return BoardBatchMoveResult.Moved;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _board.Undo(move);
                _showStatus("移动未保存，内容已恢复到原位置。");
                return BoardBatchMoveResult.Failed;
            }
            catch
            {
                _board.Undo(move);
                throw;
            }
        }, cancellationToken);

    public Task<BoardPinResult> SetPinnedAsync(
        IReadOnlyCollection<Guid> itemIds,
        bool isPinned,
        CancellationToken cancellationToken = default) =>
        _operationGate.RunAsync(async () =>
        {
            BoardPinChange change;
            try
            {
                change = _board.SetPinnedMany(itemIds, isPinned);
            }
            catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
            {
                return BoardPinResult.Invalid;
            }

            if (!change.Changed)
            {
                return BoardPinResult.NoChange;
            }

            try
            {
                await _store.SaveBoardAsync(_board.CreateSnapshot(), cancellationToken);
                return BoardPinResult.Changed;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _board.Undo(change);
                _showStatus("置顶状态未保存，内容已恢复。");
                return BoardPinResult.Failed;
            }
            catch
            {
                _board.Undo(change);
                throw;
            }
        }, cancellationToken);

    public Task<bool> DeleteAsync(
        Guid itemId,
        CancellationToken cancellationToken = default) =>
        DeleteManyAsync([itemId], cancellationToken);

    public Task<bool> DeleteManyAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken = default) =>
        _operationGate.RunAsync(async () =>
        {
            var removed = _board.RemoveMany(itemIds);
            if (removed is null)
            {
                return false;
            }

            try
            {
                await _store.SaveBoardAsync(_board.CreateSnapshot(), cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _board.Restore(removed);
                _showStatus("删除未保存，内容已恢复。");
                return false;
            }
            catch
            {
                _board.Restore(removed);
                throw;
            }

            RecordUndoableDelete(removed);
            return true;
        }, cancellationToken);

    /// <summary>
    /// 就地更新文字卡片内容并原子持久化;保存失败时还原原文本并提示。
    /// </summary>
    public Task<bool> UpdateItemTextAsync(
        Guid itemId,
        string text,
        CancellationToken cancellationToken = default) =>
        _operationGate.RunAsync(async () =>
        {
            var item = _board.FindItem(itemId);
            if (item is not { Kind: BoardItemKind.Text })
            {
                return false;
            }

            var original = item.Text ?? string.Empty;
            if (string.Equals(original, text, StringComparison.Ordinal))
            {
                return true;
            }

            return await SaveItemFieldChangeAsync(
                cancellationToken,
                apply: () => _board.UpdateText(itemId, text),
                restore: () => _board.UpdateText(itemId, original),
                failureStatus: "内容未保存，已还原原文本。");
        }, cancellationToken);

    /// <summary>
    /// 就地更新文字卡片备注并原子持久化(1.28.0):备注是元数据,卡片内容语义
    /// 零变化;note 传 null=删除备注(「空白提交=删除已有备注」的归一由调用方
    /// 完成),无备注时传 null 等值短路、不动磁盘;保存失败时还原原备注并提示。
    /// </summary>
    public Task<bool> UpdateItemNoteAsync(
        Guid itemId,
        string? note,
        CancellationToken cancellationToken = default) =>
        _operationGate.RunAsync(async () =>
        {
            var item = _board.FindItem(itemId);
            if (item is not { Kind: BoardItemKind.Text })
            {
                return false;
            }

            var original = item.Note;
            if (string.Equals(original, note, StringComparison.Ordinal))
            {
                return true;
            }

            return await SaveItemFieldChangeAsync(
                cancellationToken,
                apply: () => _board.UpdateNote(itemId, note),
                restore: () => _board.UpdateNote(itemId, original),
                failureStatus: "备注未保存，已还原原备注。");
        }, cancellationToken);

    /// <summary>
    /// 单条目字段变更的共享保存路径：应用变更→原子快照→保存失败还原变更并
    /// 提示（正文与备注更新的唯一持久化实现，不平行复制两套保存管线）。其他
    /// 异常同样先还原再抛出，不把半变更状态留给调用方。
    /// </summary>
    private async Task<bool> SaveItemFieldChangeAsync(
        CancellationToken cancellationToken,
        Action apply,
        Action restore,
        string failureStatus)
    {
        apply();
        try
        {
            await _store.SaveBoardAsync(_board.CreateSnapshot(), cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            restore();
            _showStatus(failureStatus);
            return false;
        }
        catch
        {
            restore();
            throw;
        }
    }

    /// <summary>
    /// 用 AI 整理结果替换文字卡正文，并把「替换前的原文」压入撤销栈（AI 文本
    /// 替换批）：与 <see cref="UpdateItemTextAsync"/>（手工编辑，仍不进撤销栈，
    /// 契约 #22 口径）同族共用 <see cref="SaveItemFieldChangeAsync"/> 保存管线，
    /// 不平行复制持久化路径；保存失败内存还原原文并提示、不入栈。原文在应用
    /// 变更前捕获（<c>BoardService.UpdateText</c> 原地改 <c>item.Text</c>）。
    /// 条目不存在或整理结果与原文相同时不落盘（幂等）。
    /// <paramref name="expectedCurrentText"/> 非空时在门内复检当前正文：等待
    /// 门期间正文被其他路径修改（手工编辑等）则放弃本次替换并提示（并发守卫
    /// 的原子闭合——调用方在门外做的前置比对存在让出窗口）。
    /// </summary>
    public Task<bool> ReplaceItemTextUndoableAsync(
        Guid itemId,
        string newText,
        string? expectedCurrentText = null,
        CancellationToken cancellationToken = default) =>
        _operationGate.RunAsync(async () =>
        {
            var item = _board.FindItem(itemId);
            if (item is not { Kind: BoardItemKind.Text })
            {
                return false;
            }

            var original = item.Text ?? string.Empty;
            if (expectedCurrentText is not null &&
                !string.Equals(original, expectedCurrentText, StringComparison.Ordinal))
            {
                _showStatus("卡片内容已变化，整理结果已放弃。");
                return false;
            }

            if (string.Equals(original, newText, StringComparison.Ordinal))
            {
                return true;
            }

            var saved = await SaveItemFieldChangeAsync(
                cancellationToken,
                apply: () => _board.UpdateText(itemId, newText),
                restore: () => _board.UpdateText(itemId, original),
                failureStatus: "内容未保存，已还原原文本。");
            if (!saved)
            {
                return false;
            }

            RecordUndoableBatch(new UndoableTextBatch(this, itemId, original, newText));
            return true;
        }, cancellationToken);

    /// <summary>
    /// 撤销最近一批可撤销操作(删除批按锚点插回原分类不回退删除后的其他改动 /
    /// AI 文本替换批恢复原文):多批次时后进先出,恢复的总是最近一批。空栈返回
    /// false 并提示。
    /// </summary>
    public Task<bool> UndoLastAsync(CancellationToken cancellationToken = default) =>
        _operationGate.RunAsync(async () =>
        {
            if (_undoableBatches.Count == 0)
            {
                _showStatus("没有可撤销的操作。");
                return false;
            }

            var batch = _undoableBatches.Last!.Value;
            _undoableBatches.RemoveLast();
            return await batch.UndoAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// 退出时丢弃全部可撤销批次(删除批随之清理对应图片文件);调用后不再可恢复。
    /// </summary>
    public void DiscardUndoableBatches()
    {
        while (_undoableBatches.Count > 0)
        {
            RemoveOldestBatch();
        }
    }

    /// <summary>
    /// 删除成功后记录为会话级撤销栈的最近删除批(后进先出):图片文件保留到批次被
    /// 驱逐、显式丢弃或进程退出,保证撤销时缩略图可用;超过上限时驱逐最旧批次并
    /// 同步清理其图片文件。
    /// </summary>
    private void RecordUndoableDelete(RemovedBoardItems removed) =>
        RecordUndoableBatch(new UndoableDeleteBatch(this, removed));

    /// <summary>批次入栈(尾端)并执行有界驱逐。</summary>
    private void RecordUndoableBatch(IUndoableBatch batch)
    {
        _undoableBatches.AddLast(batch);
        while (_undoableBatches.Count > MaxUndoableBatches)
        {
            RemoveOldestBatch();
        }
    }

    /// <summary>
    /// 从栈头摘除最旧批次并回收其资源(删除批清理图片文件,文本批 no-op)。
    /// 必须先摘链、后回收:删除批的清理经 DeleteImagesBestEffort 同步回调状态
    /// 提示,持链操作其后进行才不受重入影响。
    /// </summary>
    private void RemoveOldestBatch()
    {
        var oldest = _undoableBatches.First!.Value;
        _undoableBatches.RemoveFirst();
        oldest.OnEvicted();
    }

    private void DeleteImagesBestEffort(RemovedBoardItems removed)
    {
        var imageCleanupFailed = false;
        foreach (var item in removed.RemovedItems.Where(item => item.Kind == BoardItemKind.Image))
        {
            imageCleanupFailed |= !_store.TryDeleteImage(item.ImageAbsolutePath);
        }

        if (imageCleanupFailed)
        {
            _showStatus("部分已删除的图片副本暂时无法清理。");
        }
    }

    public async Task<bool> ClearCategoryAsync(
        BoardCategory category,
        CancellationToken cancellationToken = default)
    {
        return await _operationGate.RunAsync(async () =>
        {
            var removed = _board.RemoveCategory(category);
            if (removed.Items.Count == 0)
            {
                return false;
            }

            try
            {
                await _store.SaveBoardAsync(_board.CreateSnapshot(), cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _board.Restore(removed);
                _showStatus("清空未保存，内容已恢复。");
                return false;
            }
            catch
            {
                _board.Restore(removed);
                throw;
            }

            // 清空等同于批量移入撤销栈:可整体撤销,图片文件按栈生命周期清理。
            RecordUndoableDelete(new RemovedBoardItems(
                new Dictionary<BoardCategory, IReadOnlyList<BoardItem>>
                {
                    [removed.Category] = removed.Items
                },
                removed.Items.ToArray()));
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// 清空分类的非置顶条目(置顶区顺序原样保留):成功后进入现有撤销栈,
    /// 保存失败整批回滚;没有非置顶条目时为空操作,返回 false。
    /// 契约风格与 <see cref="ClearCategoryAsync"/> 一致。
    /// </summary>
    public async Task<bool> ClearNonPinnedAsync(
        BoardCategory category,
        CancellationToken cancellationToken = default)
    {
        var outcome = await ClearNonPinnedAsync([category], expiryCutoffUtc: null, cancellationToken);
        return outcome.Saved && outcome.RemovedCount > 0;
    }

    /// <summary>跨分类清扫结果：<see cref="Saved"/> 且 <see cref="RemovedCount"/> &gt; 0
    /// 才有内容被移除并持久化；Saved=true 且 RemovedCount=0 是幂等空操作（未写盘）。</summary>
    public readonly record struct ClearNonPinnedOutcome(bool Saved, int RemovedCount);

    /// <summary>
    /// 跨分类清空非置顶条目（单次原子操作）：一次移除、一次保存、一批撤销
    /// （可整批 Ctrl+Z）；保存失败所有分类整批回滚并提示，不进撤销栈。
    /// 给出 <paramref name="expiryCutoffUtc"/> 时只移除入库满期的非置顶条目
    /// （逐卡 24h TTL，自动清扫路径）；为 null 时移除全部非置顶条目（手工清空路径，
    /// 语义不变）。全部分类无可移除条目时不写盘，返回 Saved=true、RemovedCount=0。
    /// </summary>
    public Task<ClearNonPinnedOutcome> ClearNonPinnedAsync(
        IReadOnlyCollection<BoardCategory> categories,
        DateTimeOffset? expiryCutoffUtc = null,
        CancellationToken cancellationToken = default)
    {
        return _operationGate.RunAsync(async () =>
        {
            var removed = _board.RemoveNonPinned(categories, expiryCutoffUtc);
            if (removed.RemovedItems.Count == 0)
            {
                return new ClearNonPinnedOutcome(true, 0);
            }

            try
            {
                await _store.SaveBoardAsync(_board.CreateSnapshot(), cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _board.Restore(removed);
                _showStatus("清空未保存，内容已恢复。");
                return new ClearNonPinnedOutcome(false, removed.RemovedItems.Count);
            }
            catch
            {
                _board.Restore(removed);
                throw;
            }

            // 与清空全部分类同构:进入会话级撤销栈,图片文件按栈生命周期清理。
            RecordUndoableDelete(removed);
            return new ClearNonPinnedOutcome(true, removed.RemovedItems.Count);
        }, cancellationToken);
    }

    public Task SaveForShutdownAsync(
        Func<Task> saveAdditionalState,
        CancellationToken cancellationToken = default) =>
        _operationGate.SealAndRunAsync(async () =>
        {
            await _store.SaveBoardAsync(_board.CreateSnapshot(), cancellationToken);
            await saveAdditionalState();
        }, cancellationToken);

    /// <summary>
    /// 会话级撤销栈的可撤销操作批（1.41.0 泛化，契约 #11 修订）：Ctrl+Z 弹出
    /// 尾端批次执行 <see cref="UndoAsync"/>；驱逐/显式丢弃时回调
    /// <see cref="OnEvicted"/> 回收批次独占资源。删除批与 AI 文本替换批共用
    /// 同一条 LIFO、20 批驱逐与保存失败回滚语义。
    /// </summary>
    private interface IUndoableBatch
    {
        /// <summary>撤销本批并持久化；返回 false 表示撤销未落盘（状态已回滚）。</summary>
        Task<bool> UndoAsync(CancellationToken cancellationToken);

        /// <summary>批次被驱逐或显式丢弃时回收独占资源（文本批无资源，no-op）。</summary>
        void OnEvicted();
    }

    /// <summary>
    /// 删除批：既有撤销删除语义原样迁入——按锚点插回原分类并持久化，保存失败
    /// 回到删除状态；驱逐/丢弃时清理批次内图片文件（保留到撤销机会消失）。
    /// </summary>
    private sealed class UndoableDeleteBatch(BoardMutationService owner, RemovedBoardItems removed)
        : IUndoableBatch
    {
        public async Task<bool> UndoAsync(CancellationToken cancellationToken)
        {
            try
            {
                owner._board.RestoreInsert(removed);
                await owner._store.SaveBoardAsync(owner._board.CreateSnapshot(), cancellationToken);
                owner._showStatus("已恢复最近删除的内容。");
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _ = owner._board.RemoveMany(removed.RemovedItems.Select(item => item.Id).ToArray());
                owner._showStatus("撤销未保存，内容保持删除状态。");
                return false;
            }
            catch
            {
                _ = owner._board.RemoveMany(removed.RemovedItems.Select(item => item.Id).ToArray());
                throw;
            }
        }

        public void OnEvicted() => owner.DeleteImagesBestEffort(removed);
    }

    /// <summary>
    /// AI 文本替换批：撤销＝把捕获的原文写回同一条字段变更保存管线（失败时
    /// 保留整理后文字并提示）。作废消费的两个条件（批消费但不写盘、不覆盖）：
    /// 条目已被删除（原文随卡片消失）；或撤销时正文已不是本批写入的整理结果
    /// ——之后的手工编辑不进撤销栈（#22 口径），其结果是用户的最新状态，本批
    /// 撤销不得越过后进的手工编辑把正文拉回整理前。无独占资源，驱逐 no-op。
    /// </summary>
    private sealed class UndoableTextBatch(
        BoardMutationService owner,
        Guid itemId,
        string originalText,
        string replacedText) : IUndoableBatch
    {
        public async Task<bool> UndoAsync(CancellationToken cancellationToken)
        {
            if (owner._board.FindItem(itemId) is not { Kind: BoardItemKind.Text } item)
            {
                return true;
            }

            if (!string.Equals(item.Text ?? string.Empty, replacedText, StringComparison.Ordinal))
            {
                return true;
            }

            var current = item.Text ?? string.Empty;
            var restored = await owner.SaveItemFieldChangeAsync(
                cancellationToken,
                apply: () => owner._board.UpdateText(itemId, originalText),
                restore: () => owner._board.UpdateText(itemId, current),
                failureStatus: "撤销未保存，卡片保持整理后的内容。");
            if (restored)
            {
                owner._showStatus("已恢复卡片原文。");
            }

            return restored;
        }

        public void OnEvicted()
        {
        }
    }
}
