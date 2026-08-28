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
    private readonly BoardService _board;
    private readonly IBoardStore _store;
    private readonly Action<string> _showStatus;
    private readonly BoardOperationGate _operationGate;

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

    public async Task<bool> MoveAsync(
        Guid itemId,
        BoardCategory targetCategory,
        int targetIndex,
        CancellationToken cancellationToken = default)
    {
        return await _operationGate.RunAsync(async () =>
        {
            var move = _board.Move(itemId, targetCategory, targetIndex);
            try
            {
                await _store.SaveBoardAsync(_board.CreateSnapshot(), cancellationToken);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _board.Undo(move);
                _showStatus("移动未保存，内容已恢复到原位置。");
                return false;
            }
            catch
            {
                _board.Undo(move);
                throw;
            }
        }, cancellationToken);
    }

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
            var move = createMove();
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

            var imageCleanupFailed = false;
            foreach (var item in removed.RemovedItems.Where(
                         item => item.Kind == BoardItemKind.Image))
            {
                imageCleanupFailed |= !_store.TryDeleteImage(item.ImageAbsolutePath);
            }

            if (imageCleanupFailed)
            {
                _showStatus("内容已删除，但部分图片副本暂时无法删除。");
            }

            return true;
        }, cancellationToken);

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

            var imageCleanupFailed = false;
            foreach (var item in removed.Items.Where(item => item.Kind == BoardItemKind.Image))
            {
                imageCleanupFailed |= !_store.TryDeleteImage(item.ImageAbsolutePath);
            }

            if (imageCleanupFailed)
            {
                _showStatus("分类已清空，但部分图片副本暂时无法删除。");
            }

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
