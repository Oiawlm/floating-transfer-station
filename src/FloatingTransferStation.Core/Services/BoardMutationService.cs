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
    private const int MaxUndoableDeletes = 20;

    private readonly BoardService _board;
    private readonly IBoardStore _store;
    private readonly Action<string> _showStatus;
    private readonly BoardOperationGate _operationGate;
    private readonly Queue<RemovedBoardItems> _undoableDeletes = new();

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

    /// <summary>当前可撤销的删除批次数(会话内,用于界面提示与测试)。</summary>
    public int PendingUndoDeleteCount => _undoableDeletes.Count;

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

            EnqueueUndoableDelete(removed);
            return true;
        }, cancellationToken);

    /// <summary>
    /// 撤销最近一次成功删除(单条、批量或清空):按锚点插回原分类(不回退删除后的
    /// 其他改动)并持久化;保存失败时回到删除状态。空栈返回 false 并提示。
    /// </summary>
    public Task<bool> UndoLastDeleteAsync(CancellationToken cancellationToken = default) =>
        _operationGate.RunAsync(async () =>
        {
            if (_undoableDeletes.Count == 0)
            {
                _showStatus("没有可撤销的删除。");
                return false;
            }

            var removed = _undoableDeletes.Dequeue();
            try
            {
                _board.RestoreInsert(removed);
                await _store.SaveBoardAsync(_board.CreateSnapshot(), cancellationToken);
                _showStatus("已恢复最近删除的内容。");
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _ = _board.RemoveMany(removed.RemovedItems.Select(item => item.Id).ToArray());
                _showStatus("撤销未保存，内容保持删除状态。");
                return false;
            }
            catch
            {
                _ = _board.RemoveMany(removed.RemovedItems.Select(item => item.Id).ToArray());
                throw;
            }
        }, cancellationToken);

    /// <summary>
    /// 退出时丢弃全部可撤销删除并清理对应图片文件;调用后删除不再可恢复。
    /// </summary>
    public void DiscardUndoableDeletes()
    {
        while (_undoableDeletes.Count > 0)
        {
            DeleteImagesBestEffort(_undoableDeletes.Dequeue());
        }
    }

    /// <summary>
    /// 删除成功后进入会话级撤销栈:图片文件保留到条目被驱逐、显式丢弃或进程退出,
    /// 保证撤销时缩略图可用。驱逐最旧批次时同步清理其图片文件。
    /// </summary>
    private void EnqueueUndoableDelete(RemovedBoardItems removed)
    {
        _undoableDeletes.Enqueue(removed);
        while (_undoableDeletes.Count > MaxUndoableDeletes)
        {
            DeleteImagesBestEffort(_undoableDeletes.Dequeue());
        }
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
            EnqueueUndoableDelete(new RemovedBoardItems(
                new Dictionary<BoardCategory, IReadOnlyList<BoardItem>>
                {
                    [removed.Category] = removed.Items
                },
                removed.Items.ToArray()));
            return true;
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
}
