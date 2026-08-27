using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public sealed class ExternalDropImportService
{
    private readonly IImageNormalizer _normalizer;
    private readonly BoardService _board;
    private readonly IBoardStore _store;
    private readonly Action<string> _showStatus;
    private readonly BoardOperationGate _operationGate;

    public ExternalDropImportService(
        IImageNormalizer normalizer,
        BoardService board,
        IBoardStore store,
        Action<string> showStatus,
        BoardOperationGate? operationGate = null)
    {
        _normalizer = normalizer;
        _board = board;
        _store = store;
        _showStatus = showStatus;
        _operationGate = operationGate ?? new BoardOperationGate();
    }

    public async Task<bool> ImportAsync(
        ExternalDropPayload payload,
        BoardCategory targetCategory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!BoardCategoryCatalog.IsDefined(targetCategory))
        {
            throw new ArgumentOutOfRangeException(nameof(targetCategory));
        }

        var storedImages = new List<StoredImage>();
        var addedItemIds = new List<Guid>();
        try
        {
            await NormalizeAsync(payload, storedImages, cancellationToken);
            return await _operationGate.RunAsync(async () =>
            {
                try
                {
                    AddAtTopInSourceOrder(payload, storedImages, targetCategory, addedItemIds);
                    await _store.SaveBoardAsync(_board.CreateSnapshot(), cancellationToken);
                    return true;
                }
                catch
                {
                    foreach (var itemId in addedItemIds.AsEnumerable().Reverse())
                    {
                        _board.Remove(itemId);
                    }

                    throw;
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Cleanup(storedImages);
            throw;
        }
        catch
        {
            var cleanupSucceeded = Cleanup(storedImages);
            _showStatus(cleanupSucceeded
                ? "拖入内容未保存，请重试。"
                : "拖入内容未保存，且部分图片副本暂时无法删除。");
            return false;
        }
    }

    private async Task NormalizeAsync(
        ExternalDropPayload payload,
        List<StoredImage> storedImages,
        CancellationToken cancellationToken)
    {
        switch (payload)
        {
            case ExternalDropPayload.Text:
                return;

            case ExternalDropPayload.ImageCandidates imageCandidates:
                storedImages.Add(await _normalizer.NormalizeClipboardAsync(
                    imageCandidates.Candidates,
                    cancellationToken: cancellationToken));
                return;

            case ExternalDropPayload.ImageFiles imageFiles:
                foreach (var path in imageFiles.Paths)
                {
                    storedImages.Add(await _normalizer.NormalizeStaticFileAsync(
                        path,
                        cancellationToken: cancellationToken));
                }

                return;

            default:
                throw new InvalidOperationException("Unsupported external drop payload.");
        }
    }

    private void AddAtTopInSourceOrder(
        ExternalDropPayload payload,
        IReadOnlyList<StoredImage> storedImages,
        BoardCategory targetCategory,
        List<Guid> addedItemIds)
    {
        if (payload is ExternalDropPayload.Text text)
        {
            addedItemIds.Add(_board.AddText(text.Value, targetCategory).Id);
            return;
        }

        for (var index = storedImages.Count - 1; index >= 0; index--)
        {
            var stored = storedImages[index];
            addedItemIds.Add(_board.AddImage(
                stored.Id,
                stored.RelativePath,
                stored.AbsolutePath,
                targetCategory).Id);
        }
    }

    private bool Cleanup(IEnumerable<StoredImage> storedImages)
    {
        var cleanupSucceeded = true;
        foreach (var stored in storedImages)
        {
            try
            {
                if (!_store.TryDeleteImage(stored.AbsolutePath))
                {
                    cleanupSucceeded = false;
                }
            }
            catch
            {
                cleanupSucceeded = false;
            }
        }

        return cleanupSucceeded;
    }
}
