using System.Windows.Media.Imaging;
using FloatingTransferStation.Models;
using SixLabors.ImageSharp;

namespace FloatingTransferStation.Services;

public sealed class ImageNormalizer : IImageNormalizer
{
    private readonly string _imagesDirectory;

    public ImageNormalizer(string imagesDirectory)
    {
        _imagesDirectory = imagesDirectory;
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
            using var image = await Image.LoadAsync(sourcePath, cancellationToken);
            if (rejectMultipleFrames && image.Frames.Count != 1)
            {
                throw new InvalidDataException("External image files must contain exactly one frame.");
            }

            while (image.Frames.Count > 1)
            {
                image.Frames.RemoveFrame(1);
            }

            await image.SaveAsPngAsync(temporaryPath, cancellationToken);
            File.Move(temporaryPath, stored.AbsolutePath, overwrite: false);
            return stored;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task<StoredImage> NormalizeBitmapAsync(
        BitmapSource bitmap,
        Guid? id = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        cancellationToken.ThrowIfCancellationRequested();
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
        if (File.Exists(markerPath))
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
            await File.WriteAllTextAsync(temporaryMarker, "completed", cancellationToken).ConfigureAwait(false);
            File.Move(temporaryMarker, markerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryMarker))
            {
                File.Delete(temporaryMarker);
            }
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
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Bitmap is not null)
            {
                var prepared = PrepareBitmap(candidate.Bitmap);
                if (prepared.HasPixelData)
                {
                    selections.Add(new ClipboardSelection(
                        candidate,
                        checked((long)prepared.Bitmap.PixelWidth * prepared.Bitmap.PixelHeight),
                        prepared.Bitmap));
                }

                continue;
            }

            try
            {
                var information = SixLabors.ImageSharp.Image.Identify(candidate.EncodedBytes.Span);
                if (information.Width > 0 && information.Height > 0)
                {
                    selections.Add(new ClipboardSelection(
                        candidate,
                        checked((long)information.Width * information.Height),
                        null));
                }
            }
            catch (Exception exception) when (exception is UnknownImageFormatException or InvalidImageContentException)
            {
                // Another clipboard representation may still contain the same image.
            }
        }

        var selected = selections
            .OrderByDescending(selection => selection.PixelArea)
            .ThenByDescending(selection => selection.PreparedBitmap is not null)
            .FirstOrDefault()
            ?? throw new InvalidDataException("Clipboard does not contain a usable image representation.");

        return selected.PreparedBitmap is not null
            ? SaveBitmap(selected.PreparedBitmap, id, cancellationToken)
            : SaveEncodedImage(selected.Candidate.EncodedBytes, id, cancellationToken);
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
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                encoder.Save(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, stored.AbsolutePath, overwrite: false);
            return stored;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private StoredImage SaveEncodedImage(
        ReadOnlyMemory<byte> encodedBytes,
        Guid? id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stored = CreateDestination(id ?? Guid.NewGuid());
        var temporaryPath = stored.AbsolutePath + ".tmp";
        Directory.CreateDirectory(_imagesDirectory);

        try
        {
            using var image = SixLabors.ImageSharp.Image.Load(encodedBytes.Span);
            while (image.Frames.Count > 1)
            {
                image.Frames.RemoveFrame(1);
            }

            image.SaveAsPng(temporaryPath);
            File.Move(temporaryPath, stored.AbsolutePath, overwrite: false);
            return stored;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task RepairStoredImageAsync(string imagePath, CancellationToken cancellationToken)
    {
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
            await image.SaveAsPngAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, imagePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
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

    private static PreparedBitmap PrepareBitmap(BitmapSource bitmap)
    {
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

    private bool IsManagedImagePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var allowedRoot = Path.GetFullPath(_imagesDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ClipboardSelection(
        ClipboardImageCandidate Candidate,
        long PixelArea,
        BitmapSource? PreparedBitmap);

    private readonly record struct PreparedBitmap(BitmapSource Bitmap, bool HasPixelData);

    private StoredImage CreateDestination(Guid id)
    {
        var fileName = $"{id:N}.png";
        return new StoredImage(
            id,
            $"images/{fileName}",
            Path.Combine(_imagesDirectory, fileName));
    }
}
