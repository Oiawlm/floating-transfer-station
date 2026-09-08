using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using SixLabors.ImageSharp;

namespace FloatingTransferStation.Mac.Services;

public sealed class TransferImportService(
    IMacImageNormalizer normalizer,
    BoardService board,
    IBoardStore store,
    Action<string> showStatus,
    BoardOperationGate operationGate)
{
    public Task<bool> ImportAsync(TransferPayload payload, BoardCategory targetCategory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!BoardCategoryCatalog.IsDefined(targetCategory))
        {
            throw new ArgumentOutOfRangeException(nameof(targetCategory));
        }

        // Register before normalization so shutdown drains the entire import, including generated files.
        return operationGate.RunAsync(async () =>
        {
            var stored = new List<StoredImage>();
            var added = new List<Guid>();
            var skipped = 0;
            var committed = false;
            try
            {
                switch (payload)
                {
                    case TransferPayload.Text:
                        break;
                    case TransferPayload.ImageFiles files:
                        foreach (var path in files.Paths)
                        {
                            try
                            {
                                stored.Add(await normalizer.NormalizeStaticFileAsync(path, cancellationToken));
                            }
                            catch (Exception exception) when (IsInvalidSource(exception))
                            {
                                skipped++;
                            }
                        }

                        break;
                    case TransferPayload.ImageCandidates image:
                        stored.Add(await normalizer.NormalizeCandidatesAsync(image.Candidates, cancellationToken));
                        break;
                    case TransferPayload.ImageBatch batch:
                        foreach (var image in batch.Images)
                        {
                            try
                            {
                                stored.Add(await normalizer.NormalizeCandidatesAsync(image.Candidates, cancellationToken));
                            }
                            catch (Exception exception) when (IsInvalidSource(exception))
                            {
                                skipped++;
                            }
                        }

                        break;
                    default:
                        throw new InvalidDataException("Unsupported transfer payload.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (payload is TransferPayload.Text text)
                {
                    added.Add(board.AddText(text.Value, targetCategory).Id);
                }
                else
                {
                    if (stored.Count == 0)
                    {
                        showStatus("没有可导入的静态图片；请检查文件格式和大小。");
                        return false;
                    }

                    for (var index = stored.Count - 1; index >= 0; index--)
                    {
                        var image = stored[index];
                        added.Add(board.AddImage(image.Id, image.RelativePath, image.AbsolutePath, targetCategory).Id);
                    }
                }

                await store.SaveBoardAsync(board.CreateSnapshot(), cancellationToken);
                committed = true;
                if (skipped > 0)
                {
                    showStatus($"已导入 {stored.Count} 张图片，跳过 {skipped} 个不可用文件。");
                }

                return true;
            }
            catch (Exception exception)
            {
                if (committed)
                {
                    // A UI notification failure must never delete images referenced by the durable board.
                    return true;
                }

                foreach (var id in added.AsEnumerable().Reverse())
                {
                    board.Remove(id);
                }

                var cleaned = true;
                foreach (var image in stored)
                {
                    try
                    {
                        cleaned &= store.TryDeleteImage(image.AbsolutePath);
                    }
                    catch
                    {
                        cleaned = false;
                    }
                }

                if (exception is OperationCanceledException)
                {
                    throw;
                }

                showStatus(cleaned ? "内容未保存，请重试。" : "内容未保存，部分图片副本暂时无法删除。");
                return false;
            }
        }, cancellationToken);
    }

    private static bool IsInvalidSource(Exception exception) => exception is
        InvalidDataException or ImageInputLimitException or UnknownImageFormatException or
        InvalidImageContentException or FileNotFoundException or DirectoryNotFoundException;
}
