using System.Runtime.InteropServices;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public sealed class ClipboardCaptureService
{
    private readonly IClipboardReader _reader;
    private readonly IImageNormalizer _normalizer;
    private readonly BoardService _board;
    private readonly IBoardStore _store;
    private readonly Action<string> _showStatus;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private readonly BoardOperationGate _operationGate;
    private readonly DefaultCaptureCategoryState _defaultCaptureCategory;
    private readonly object _queueLock = new();
    private Task _processingTail = Task.CompletedTask;
    private uint? _lastSequence;

    public ClipboardCaptureService(
        IClipboardReader reader,
        IImageNormalizer normalizer,
        BoardService board,
        IBoardStore store,
        Action<string> showStatus,
        IReadOnlyList<TimeSpan>? retryDelays = null,
        BoardOperationGate? operationGate = null,
        DefaultCaptureCategoryState? defaultCaptureCategory = null)
    {
        _reader = reader;
        _normalizer = normalizer;
        _board = board;
        _store = store;
        _showStatus = showStatus;
        _retryDelays = retryDelays ??
            new[] { TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(150) };
        _operationGate = operationGate ?? new BoardOperationGate();
        _defaultCaptureCategory = defaultCaptureCategory ?? new DefaultCaptureCategoryState();
    }

    public Task HandleClipboardUpdateAsync(CancellationToken cancellationToken = default)
    {
        var targetCategory = _defaultCaptureCategory.Current;
        Task predecessor;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_queueLock)
        {
            predecessor = _processingTail;
            _processingTail = completion.Task;
        }

        return HandleReservedUpdateAsync(
            targetCategory,
            predecessor,
            completion,
            cancellationToken);
    }

    private bool TryAcceptSequence(uint sequenceNumber)
    {
        lock (_queueLock)
        {
            if (_lastSequence == sequenceNumber)
            {
                return false;
            }

            _lastSequence = sequenceNumber;
            return true;
        }
    }

    private async Task HandleReservedUpdateAsync(
        BoardCategory targetCategory,
        Task predecessor,
        TaskCompletionSource completion,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await ReadWithRetryAsync(cancellationToken);
            if (snapshot is null || !TryAcceptSequence(snapshot.SequenceNumber))
            {
                return;
            }

            await predecessor;
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessSnapshotAsync(snapshot, targetCategory, cancellationToken);
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    private async Task ProcessSnapshotAsync(
        ClipboardSnapshot snapshot,
        BoardCategory targetCategory,
        CancellationToken cancellationToken)
    {
        if (snapshot.ImageCandidates.Count > 0)
        {
            await ImportClipboardImageAsync(snapshot.ImageCandidates, targetCategory, cancellationToken);
            return;
        }

        if (snapshot.FilePaths.Count > 0)
        {
            var imageFiles = snapshot.FilePaths
                .Where(ImageFileSupport.IsSupported)
                .Where(File.Exists)
                .ToArray();
            foreach (var imageFile in imageFiles)
            {
                await ImportFileAsync(imageFile, targetCategory, cancellationToken);
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Text))
        {
            await ImportTextAsync(snapshot.Text, targetCategory, cancellationToken);
        }
    }

    private async Task<ClipboardSnapshot?> ReadWithRetryAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= _retryDelays.Count; attempt++)
        {
            try
            {
                return await _reader.ReadAsync(cancellationToken);
            }
            catch (ExternalException)
            {
                if (attempt == _retryDelays.Count)
                {
                    break;
                }

                await Task.Delay(_retryDelays[attempt], cancellationToken);
            }
        }

        _showStatus("本次剪贴板内容暂时无法读取，请重新复制。");
        return null;
    }

    private async Task ImportTextAsync(
        string text,
        BoardCategory targetCategory,
        CancellationToken cancellationToken)
    {
        await _operationGate.RunAsync(async () =>
        {
            var item = _board.AddText(text, targetCategory);
            try
            {
                await _store.SaveBoardAsync(_board.CreateSnapshot(), cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _board.Remove(item.Id);
                _showStatus("本次内容未保存，请重新复制。");
            }
            catch
            {
                _board.Remove(item.Id);
                throw;
            }

            return true;
        }, cancellationToken);
    }

    private async Task ImportClipboardImageAsync(
        IReadOnlyList<ClipboardImageCandidate> candidates,
        BoardCategory targetCategory,
        CancellationToken cancellationToken)
    {
        try
        {
            var stored = await _normalizer.NormalizeClipboardAsync(candidates, cancellationToken: cancellationToken);
            await CommitImageAsync(stored, targetCategory, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _showStatus("本次图片未保存，请重新复制。");
        }
    }

    private async Task ImportFileAsync(
        string path,
        BoardCategory targetCategory,
        CancellationToken cancellationToken)
    {
        try
        {
            var stored = await _normalizer.NormalizeFileAsync(path, cancellationToken: cancellationToken);
            await CommitImageAsync(stored, targetCategory, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _showStatus("本次图片未保存，请重新复制。");
        }
    }

    private async Task CommitImageAsync(
        StoredImage stored,
        BoardCategory targetCategory,
        CancellationToken cancellationToken)
    {
        try
        {
            await _operationGate.RunAsync(async () =>
            {
                var item = _board.AddImage(
                    stored.Id,
                    stored.RelativePath,
                    stored.AbsolutePath,
                    targetCategory);
                try
                {
                    await _store.SaveBoardAsync(_board.CreateSnapshot(), cancellationToken);
                }
                catch
                {
                    _board.Remove(item.Id);
                    throw;
                }

                return true;
            }, cancellationToken);
        }
        catch
        {
            _store.TryDeleteImage(stored.AbsolutePath);
            throw;
        }
    }
}
