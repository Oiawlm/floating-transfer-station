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
    private readonly PluginCatalog? _pluginCatalog;
    private readonly CaptureDeduplicationGate? _deduplicationGate;
    private readonly object _queueLock = new();
    private readonly HashSet<uint> _pendingSequences = [];
    private readonly int _maximumPendingCaptures;
    private readonly long _maximumPendingBytes;
    private Task _processingTail = Task.CompletedTask;
    private uint? _lastSequence;
    private int _pendingCaptureCount;
    private long _pendingBytes;
    private bool _capacityReported;

    public ClipboardCaptureService(
        IClipboardReader reader,
        IImageNormalizer normalizer,
        BoardService board,
        IBoardStore store,
        Action<string> showStatus,
        IReadOnlyList<TimeSpan>? retryDelays = null,
        BoardOperationGate? operationGate = null,
        DefaultCaptureCategoryState? defaultCaptureCategory = null,
        PluginCatalog? pluginCatalog = null,
        CaptureDeduplicationGate? deduplicationGate = null,
        int maximumPendingCaptures = 16,
        long maximumPendingBytes = 256L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPendingCaptures);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPendingBytes);
        _reader = reader;
        _normalizer = normalizer;
        _board = board;
        _store = store;
        _showStatus = showStatus;
        _retryDelays = retryDelays ??
            new[] { TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(150) };
        _operationGate = operationGate ?? new BoardOperationGate();
        _defaultCaptureCategory = defaultCaptureCategory ?? new DefaultCaptureCategoryState();
        _pluginCatalog = pluginCatalog;
        _deduplicationGate = deduplicationGate;
        _maximumPendingCaptures = maximumPendingCaptures;
        _maximumPendingBytes = maximumPendingBytes;
    }

    public Task HandleClipboardUpdateAsync(CancellationToken cancellationToken = default)
    {
        var targetCategory = _defaultCaptureCategory.Current;
        var sequenceHint = _reader.GetSequenceNumber();
        Task predecessor;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = false;
        lock (_queueLock)
        {
            predecessor = _processingTail;
            if (sequenceHint is { } sequence &&
                (_pendingSequences.Contains(sequence) || _lastSequence == sequence))
            {
                return predecessor.WaitAsync(cancellationToken);
            }

            if (_pendingCaptureCount < _maximumPendingCaptures && _pendingBytes < _maximumPendingBytes)
            {
                _pendingCaptureCount++;
                if (sequenceHint is { } pendingSequence)
                {
                    _pendingSequences.Add(pendingSequence);
                }

                _processingTail = completion.Task;
                admitted = true;
            }
        }

        if (!admitted)
        {
            ReportCapacityExceeded();
            return predecessor.WaitAsync(cancellationToken);
        }

        return HandleReservedUpdateAsync(
            targetCategory,
            predecessor,
            completion,
            sequenceHint,
            cancellationToken);
    }

    private void ReportCapacityExceeded()
    {
        lock (_queueLock)
        {
            if (_capacityReported)
            {
                return;
            }

            _capacityReported = true;
        }

        _showStatus("待处理剪贴板内容过多或过大，本次未收集，请稍后重新复制。");
    }

    private bool TryAcceptSequence(uint sequenceNumber)
    {
        if (sequenceNumber == 0)
        {
            // GetClipboardSequenceNumber() reports failure as 0; such snapshots
            // carry no comparable identity and must never suppress later captures.
            return true;
        }

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
        uint? sequenceHint,
        CancellationToken cancellationToken)
    {
        long reservedBytes = 0;
        try
        {
            var snapshot = await ReadWithRetryAsync(cancellationToken);
            if (snapshot is not null)
            {
                var payloadBytes = GetPayloadBytes(snapshot);
                lock (_queueLock)
                {
                    if (payloadBytes <= _maximumPendingBytes - _pendingBytes)
                    {
                        reservedBytes = payloadBytes;
                        _pendingBytes += reservedBytes;
                    }
                    else
                    {
                        snapshot = null;
                    }
                }

                if (snapshot is null)
                {
                    ReportCapacityExceeded();
                }
            }

            await predecessor;
            if (snapshot is null || !TryAcceptSequence(snapshot.SequenceNumber))
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await ProcessSnapshotAsync(snapshot, targetCategory, cancellationToken);
        }
        finally
        {
            // Failed or canceled reads must still keep their place behind earlier captures.
            await predecessor;
            lock (_queueLock)
            {
                _pendingBytes -= reservedBytes;
                _pendingCaptureCount--;
                if (sequenceHint is { } sequence)
                {
                    _pendingSequences.Remove(sequence);
                }

                if (_pendingCaptureCount == 0)
                {
                    _capacityReported = false;
                }
            }

            completion.TrySetResult();
        }
    }

    private static long GetPayloadBytes(ClipboardSnapshot snapshot)
    {
        try
        {
            checked
            {
                var bytes = (long)(snapshot.Text?.Length ?? 0) * sizeof(char);
                foreach (var path in snapshot.FilePaths)
                {
                    bytes += (long)path.Length * sizeof(char);
                }

                foreach (var candidate in snapshot.ImageCandidates)
                {
                    bytes += candidate.Bitmap is { } bitmap
                        ? (long)bitmap.PixelWidth * bitmap.PixelHeight * Math.Max(4, (bitmap.Format.BitsPerPixel + 7) / 8)
                        : candidate.EncodedBytes.Length;
                }

                return bytes;
            }
        }
        catch (OverflowException)
        {
            return long.MaxValue;
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
            await ImportFilesAsync(imageFiles, targetCategory, cancellationToken);

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
        // 启用的文本整理插件在入库前应用；每次采集取最新管线快照，
        // 设置中切换插件立即生效。插件绝不允许把内容清成空白。
        var cleaned = _pluginCatalog?.TextCleaner.Apply(text) ?? text;
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return;
        }

        var fingerprint = CapturedContentFingerprint.ForText(cleaned);
        await _operationGate.RunAsync(async () =>
        {
            if (_deduplicationGate is { } gate && gate.IsRecentDuplicate(fingerprint))
            {
                _showStatus("与最近收集的内容相同，未重复收集。");
                return true;
            }

            var item = _board.AddText(cleaned, targetCategory);
            try
            {
                await _store.SaveBoardAsync(_board.CreateSnapshot(), cancellationToken);
                _deduplicationGate?.RecordAccepted(fingerprint);
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
            var fingerprint = CapturedContentFingerprint.ForImageFile(stored.AbsolutePath);
            await CommitImageAsync(stored, targetCategory, fingerprint, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _showStatus("本次图片未保存，请重新复制。");
        }
    }

    private async Task ImportFilesAsync(
        IReadOnlyList<string> paths,
        BoardCategory targetCategory,
        CancellationToken cancellationToken)
    {
        var storedImages = new List<StoredImage>();
        var hadFailure = false;
        try
        {
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    storedImages.Add(await _normalizer.NormalizeFileAsync(
                        path, cancellationToken: cancellationToken));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A damaged file must not discard the other usable files in the copy.
                    hadFailure = true;
                }
            }
        }
        catch
        {
            foreach (var stored in storedImages)
            {
                _store.TryDeleteImage(stored.AbsolutePath);
            }

            throw;
        }

        try
        {
            if (storedImages.Count > 0)
            {
                CapturedContentFingerprint? fingerprint = null;
                if (storedImages.Count == 1)
                {
                    fingerprint = CapturedContentFingerprint.ForImageFile(storedImages[0].AbsolutePath);
                }

                await CommitImagesAsync(storedImages, targetCategory, fingerprint, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            hadFailure = true;
        }

        if (hadFailure)
        {
            _showStatus("本次图片未保存，请重新复制。");
        }
    }

    private Task CommitImageAsync(
        StoredImage stored,
        BoardCategory targetCategory,
        CapturedContentFingerprint? fingerprint,
        CancellationToken cancellationToken) =>
        CommitImagesAsync([stored], targetCategory, fingerprint, cancellationToken);

    private async Task CommitImagesAsync(
        IReadOnlyList<StoredImage> storedImages,
        BoardCategory targetCategory,
        CapturedContentFingerprint? fingerprint,
        CancellationToken cancellationToken)
    {
        var suppressed = false;
        try
        {
            await _operationGate.RunAsync(async () =>
            {
                if (_deduplicationGate is { } gate &&
                    fingerprint is { } candidate &&
                    storedImages.Count == 1 &&
                    gate.IsRecentDuplicate(candidate))
                {
                    suppressed = true;
                    return false;
                }

                var addedItemIds = new List<Guid>();
                try
                {
                    for (var index = storedImages.Count - 1; index >= 0; index--)
                    {
                        var stored = storedImages[index];
                        addedItemIds.Add(_board.AddImage(
                            stored.Id, stored.RelativePath, stored.AbsolutePath, targetCategory).Id);
                    }

                    await _store.SaveBoardAsync(_board.CreateSnapshot(), cancellationToken);
                    if (_deduplicationGate is { } recordingGate && fingerprint is { } accepted)
                    {
                        recordingGate.RecordAccepted(accepted);
                    }
                }
                catch
                {
                    foreach (var itemId in addedItemIds.AsEnumerable().Reverse())
                    {
                        _board.Remove(itemId);
                    }

                    throw;
                }

                return true;
            }, cancellationToken);
        }
        catch
        {
            foreach (var stored in storedImages)
            {
                _store.TryDeleteImage(stored.AbsolutePath);
            }

            throw;
        }

        if (suppressed)
        {
            foreach (var stored in storedImages)
            {
                _store.TryDeleteImage(stored.AbsolutePath);
            }

            _showStatus("与最近收集的内容相同，未重复收集。");
        }
    }
}
