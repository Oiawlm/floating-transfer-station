using System.Windows.Media.Imaging;
using FloatingTransferStation.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace FloatingTransferStation.Services;

public sealed class ImageNormalizer : IImageNormalizer
{
    private readonly string _imagesDirectory;
    private readonly ImageInputLimits _limits;

    public ImageNormalizer(string imagesDirectory, ImageInputLimits? limits = null)
    {
        _imagesDirectory = imagesDirectory;
        _limits = limits ?? ImageInputLimits.Default;
    }

    public Task<StoredImage> NormalizeFileAsync(
        string sourcePath,
        Guid? id = null,
        CancellationToken cancellationToken = default) =>
        NormalizeFileCoreAsync(sourcePath, id, rejectMultipleFrames: false, cancellationToken);

    public Task<StoredImage> NormalizeStaticFileAsync(
        string sourcePath,
        Guid? id = null,
        CancellationToken cancellationToken = default) =>
        NormalizeFileCoreAsync(sourcePath, id, rejectMultipleFrames: true, cancellationToken);

    private async Task<StoredImage> NormalizeFileCoreAsync(
        string sourcePath,
        Guid? id,
        bool rejectMultipleFrames,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var stored = CreateDestination(id ?? Guid.NewGuid());
        var temporaryPath = stored.AbsolutePath + ".tmp";
        Directory.CreateDirectory(_imagesDirectory);

        try
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _limits.ValidateEncodedLength(source.Length);
            var information = await Image.IdentifyAsync(
                rejectMultipleFrames ? _limits.IdentifyStaticImage : _limits.IdentifyFirstFrame,
                source,
                cancellationToken);
            _limits.ValidateDimensions(information.Width, information.Height);
            if (rejectMultipleFrames && !ImageFileSupport.HasSingleFrame(information, source))
            {
                throw new InvalidDataException("External image files must contain exactly one frame.");
            }

            source.Position = 0;
            using var image = await Image.LoadAsync(_limits.DecodeFirstFrame, source, cancellationToken);
            _limits.ValidateDimensions(image.Width, image.Height);

            image.Mutate(operation => operation.AutoOrient());
            EnsureManagedImagePath(temporaryPath);
            await image.SaveAsPngAsync(temporaryPath, cancellationToken);
            MoveManagedFile(temporaryPath, stored.AbsolutePath, overwrite: false);
            return stored;
        }
        catch (Exception exception) when (ImageInputLimits.IsAllocationLimitFailure(exception))
        {
            throw _limits.AllocationFailure(exception);
        }
        finally
        {
            DeleteManagedTemporaryFile(temporaryPath);
        }
    }

    public Task<StoredImage> NormalizeBitmapAsync(
        BitmapSource bitmap,
        Guid? id = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        cancellationToken.ThrowIfCancellationRequested();
        _limits.ValidateDimensions(bitmap.PixelWidth, bitmap.PixelHeight);
        var frozenBitmap = FreezeForBackgroundUse(bitmap);
        return Task.Run(
            () => NormalizeBitmapCore(frozenBitmap, id, cancellationToken),
            cancellationToken);
    }

    public Task<StoredImage> NormalizeClipboardAsync(
        IReadOnlyList<ClipboardImageCandidate> candidates,
        Guid? id = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            throw new ArgumentException("At least one clipboard image candidate is required.", nameof(candidates));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = candidates.ToArray();
        return Task.Run(
            () => NormalizeClipboardCore(snapshot, id, cancellationToken),
            cancellationToken);
    }

    public async Task RepairStoredImagesOnceAsync(
        IEnumerable<string> imagePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imagePaths);
        var markerPath = Path.Combine(_imagesDirectory, ".zero-alpha-repair-v1");
        if (!IsManagedImagePath(markerPath) || File.Exists(markerPath))
        {
            return;
        }

        Directory.CreateDirectory(_imagesDirectory);
        var completed = true;
        foreach (var imagePath in imagePaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsManagedImagePath(imagePath) || !File.Exists(imagePath))
            {
                continue;
            }

            try
            {
                await RepairStoredImageAsync(imagePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                completed = false;
            }
        }

        if (!completed)
        {
            return;
        }

        var temporaryMarker = markerPath + ".tmp";
        try
        {
            EnsureManagedImagePath(temporaryMarker);
            await File.WriteAllTextAsync(temporaryMarker, "completed", cancellationToken).ConfigureAwait(false);
            MoveManagedFile(temporaryMarker, markerPath, overwrite: true);
        }
        finally
        {
            DeleteManagedTemporaryFile(temporaryMarker);
        }
    }

    private StoredImage NormalizeBitmapCore(
        BitmapSource bitmap,
        Guid? id,
        CancellationToken cancellationToken)
        => SaveBitmap(PrepareBitmap(bitmap).Bitmap, id, cancellationToken);

    private StoredImage NormalizeClipboardCore(
        IReadOnlyList<ClipboardImageCandidate> candidates,
        Guid? id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selections = new List<ClipboardSelection>();
        ImageInputLimitException? limitFailure = null;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Bitmap is not null)
            {
                try
                {
                    var prepared = PrepareBitmap(candidate.Bitmap);
                    if (prepared.HasPixelData)
                    {
                        selections.Add(new ClipboardSelection(
                            candidate,
                            checked((long)prepared.Bitmap.PixelWidth * prepared.Bitmap.PixelHeight),
                            prepared.Bitmap));
                    }
                }
                catch (ImageInputLimitException exception)
                {
                    limitFailure ??= exception;
                }

                continue;
            }

            try
            {
                _limits.ValidateEncodedLength(candidate.EncodedBytes.Length);
                var information = SixLabors.ImageSharp.Image.Identify(
                    _limits.IdentifyFirstFrame, candidate.EncodedBytes.Span);
                _limits.ValidateDimensions(information.Width, information.Height);
                if (information.Width > 0 && information.Height > 0)
                {
                    selections.Add(new ClipboardSelection(
                        candidate,
                        checked((long)information.Width * information.Height),
                        null));
                }
            }
            catch (ImageInputLimitException exception)
            {
                limitFailure ??= exception;
            }
            catch (Exception exception) when (ImageInputLimits.IsAllocationLimitFailure(exception))
            {
                limitFailure ??= _limits.AllocationFailure(exception);
            }
            catch (Exception exception) when (exception is UnknownImageFormatException or InvalidImageContentException)
            {
                // Another clipboard representation may still contain the same image.
            }
        }

        var orderedSelections = selections
            .OrderByDescending(selection => selection.PixelArea)
            .ThenByDescending(selection => selection.PreparedBitmap is not null);
        foreach (var selected in orderedSelections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selected.PreparedBitmap is not null)
            {
                return SaveBitmap(selected.PreparedBitmap, id, cancellationToken);
            }

            try
            {
                using var image = TryDecodeClipboardImage(selected.Candidate.EncodedBytes);
                if (image is not null)
                {
                    return SaveDecodedImage(image, id, cancellationToken);
                }
            }
            catch (Exception exception) when (ImageInputLimits.IsAllocationLimitFailure(exception))
            {
                limitFailure ??= _limits.AllocationFailure(exception);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (limitFailure is not null)
        {
            throw limitFailure;
        }

        throw new InvalidDataException("Clipboard does not contain a usable image representation.");
    }

    private SixLabors.ImageSharp.Image? TryDecodeClipboardImage(ReadOnlyMemory<byte> encodedBytes)
    {
        try
        {
            return SixLabors.ImageSharp.Image.Load(_limits.DecodeFirstFrame, encodedBytes.Span);
        }
        catch (Exception exception) when (
            exception is UnknownImageFormatException or InvalidImageContentException &&
            !ImageInputLimits.IsAllocationLimitFailure(exception))
        {
            return null;
        }
    }

    private StoredImage SaveBitmap(
        BitmapSource bitmap,
        Guid? id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stored = CreateDestination(id ?? Guid.NewGuid());
        var temporaryPath = stored.AbsolutePath + ".tmp";
        Directory.CreateDirectory(_imagesDirectory);

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            EnsureManagedImagePath(temporaryPath);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                encoder.Save(stream);
                stream.Flush(flushToDisk: true);
            }

            MoveManagedFile(temporaryPath, stored.AbsolutePath, overwrite: false);
            return stored;
        }
        finally
        {
            DeleteManagedTemporaryFile(temporaryPath);
        }
    }

    private StoredImage SaveDecodedImage(
        SixLabors.ImageSharp.Image image,
        Guid? id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stored = CreateDestination(id ?? Guid.NewGuid());
        var temporaryPath = stored.AbsolutePath + ".tmp";
        Directory.CreateDirectory(_imagesDirectory);

        try
        {
            image.Mutate(operation => operation.AutoOrient());
            EnsureManagedImagePath(temporaryPath);
            image.SaveAsPng(temporaryPath);
            MoveManagedFile(temporaryPath, stored.AbsolutePath, overwrite: false);
            return stored;
        }
        finally
        {
            DeleteManagedTemporaryFile(temporaryPath);
        }
    }

    private async Task RepairStoredImageAsync(string imagePath, CancellationToken cancellationToken)
    {
        EnsureManagedImagePath(imagePath);
        using var image = await SixLabors.ImageSharp.Image.LoadAsync<
            SixLabors.ImageSharp.PixelFormats.Rgba32>(imagePath, cancellationToken).ConfigureAwait(false);
        var allAlphaZero = true;
        var hasNonZeroRgb = false;
        image.ProcessPixelRows(accessor =>
        {
            for (var rowIndex = 0; rowIndex < accessor.Height; rowIndex++)
            {
                var row = accessor.GetRowSpan(rowIndex);
                foreach (var pixel in row)
                {
                    allAlphaZero &= pixel.A == 0;
                    hasNonZeroRgb |= pixel.R != 0 || pixel.G != 0 || pixel.B != 0;
                }
            }
        });

        if (!allAlphaZero || !hasNonZeroRgb)
        {
            return;
        }

        image.ProcessPixelRows(accessor =>
        {
            for (var rowIndex = 0; rowIndex < accessor.Height; rowIndex++)
            {
                var row = accessor.GetRowSpan(rowIndex);
                for (var columnIndex = 0; columnIndex < row.Length; columnIndex++)
                {
                    row[columnIndex].A = byte.MaxValue;
                }
            }
        });

        var temporaryPath = imagePath + ".repair.tmp";
        try
        {
            EnsureManagedImagePath(temporaryPath);
            await image.SaveAsPngAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            MoveManagedFile(temporaryPath, imagePath, overwrite: true);
        }
        finally
        {
            DeleteManagedTemporaryFile(temporaryPath);
        }
    }

    private static BitmapSource FreezeForBackgroundUse(BitmapSource bitmap)
    {
        if (bitmap.IsFrozen)
        {
            return bitmap;
        }

        if (!bitmap.CanFreeze)
        {
            throw new InvalidOperationException("Clipboard bitmap cannot be frozen for background processing.");
        }

        var clone = bitmap.CloneCurrentValue();
        clone.Freeze();
        return clone;
    }

    private PreparedBitmap PrepareBitmap(BitmapSource bitmap)
    {
        _limits.ValidateDimensions(bitmap.PixelWidth, bitmap.PixelHeight);
        BitmapSource converted = bitmap;
        if (bitmap.Format != System.Windows.Media.PixelFormats.Bgra32)
        {
            converted = new FormatConvertedBitmap(
                bitmap,
                System.Windows.Media.PixelFormats.Bgra32,
                null,
                0);
            converted.Freeze();
        }

        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);

        var allAlphaZero = true;
        var hasNonZeroRgb = false;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            hasNonZeroRgb |= pixels[offset] != 0 || pixels[offset + 1] != 0 || pixels[offset + 2] != 0;
            allAlphaZero &= pixels[offset + 3] == 0;
        }

        if (allAlphaZero && hasNonZeroRgb)
        {
            for (var offset = 3; offset < pixels.Length; offset += 4)
            {
                pixels[offset] = byte.MaxValue;
            }
        }

        var result = BitmapSource.Create(
            converted.PixelWidth,
            converted.PixelHeight,
            converted.DpiX,
            converted.DpiY,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        result.Freeze();
        return new PreparedBitmap(result, !allAlphaZero || hasNonZeroRgb);
    }

    private bool IsManagedImagePath(string path) => ManagedImagePath.IsAllowed(_imagesDirectory, path);

    private void EnsureManagedImagePath(string path)
    {
        if (!IsManagedImagePath(path))
        {
            throw new InvalidDataException("Image path is outside the managed directory or traverses a reparse point.");
        }
    }

    private void MoveManagedFile(string source, string destination, bool overwrite)
    {
        EnsureManagedImagePath(source);
        EnsureManagedImagePath(destination);
        File.Move(source, destination, overwrite);
    }

    private void DeleteManagedTemporaryFile(string path)
    {
        if (IsManagedImagePath(path) && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed record ClipboardSelection(
        ClipboardImageCandidate Candidate,
        long PixelArea,
        BitmapSource? PreparedBitmap);

    private readonly record struct PreparedBitmap(BitmapSource Bitmap, bool HasPixelData);

    private StoredImage CreateDestination(Guid id)
    {
        var fileName = $"{id:N}.png";
        var absolutePath = Path.Combine(_imagesDirectory, fileName);
        EnsureManagedImagePath(absolutePath);
        EnsureManagedImagePath(absolutePath + ".tmp");
        return new StoredImage(
            id,
            $"images/{fileName}",
            absolutePath);
    }
}
