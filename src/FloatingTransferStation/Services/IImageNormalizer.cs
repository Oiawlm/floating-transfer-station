using System.Windows.Media.Imaging;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public readonly record struct StoredImage(Guid Id, string RelativePath, string AbsolutePath);

public interface IImageNormalizer
{
    Task<StoredImage> NormalizeFileAsync(
        string sourcePath,
        Guid? id = null,
        CancellationToken cancellationToken = default);

    Task<StoredImage> NormalizeStaticFileAsync(
        string sourcePath,
        Guid? id = null,
        CancellationToken cancellationToken = default);

    Task<StoredImage> NormalizeBitmapAsync(
        BitmapSource bitmap,
        Guid? id = null,
        CancellationToken cancellationToken = default);

    Task<StoredImage> NormalizeClipboardAsync(
        IReadOnlyList<ClipboardImageCandidate> candidates,
        Guid? id = null,
        CancellationToken cancellationToken = default);
}
