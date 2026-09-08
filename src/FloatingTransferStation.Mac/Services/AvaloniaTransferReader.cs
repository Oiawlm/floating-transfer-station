using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Mac.Services;

public sealed class AvaloniaTransferReader(ImageInputLimits? limits = null)
{
    private readonly ImageInputLimits _limits = limits ?? ImageInputLimits.Default;
    public const int MaximumItems = 128;
    public const long MaximumSnapshotBytes = 128L * 1024 * 1024;

    public Task<TransferPayload?> ReadAsync(IAsyncDataTransfer data, CancellationToken cancellationToken = default) =>
        ReadCoreAsync(data.Items.Select(item => new SourceItem(item.Formats, item.TryGetRawAsync)).ToArray(), cancellationToken);

    public Task<TransferPayload?> ReadAsync(IDataTransfer data, CancellationToken cancellationToken = default) =>
        ReadCoreAsync(data.Items.Select(item => new SourceItem(item.Formats,
            format => Task.FromResult(item.TryGetRaw(format)))).ToArray(), cancellationToken);

    private async Task<TransferPayload?> ReadCoreAsync(IReadOnlyList<SourceItem> items, CancellationToken cancellationToken)
    {
        if (items.Count > MaximumItems)
        {
            throw new ImageInputLimitException("Too many items in one transfer.");
        }

        // A file transfer is authoritative. Never turn unreadable images into their path text.
        if (items.Any(item => item.Formats.Contains(DataFormat.File)))
        {
            var paths = new List<string>();
            foreach (var item in items.Where(item => item.Formats.Contains(DataFormat.File)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (await item.Read(DataFormat.File) is IStorageFile file &&
                        file.TryGetLocalPath() is { } path && ImageFileSupport.IsSupported(path))
                    {
                        paths.Add(path);
                    }
                }
                catch (Exception exception) when (IsRepresentationFailure(exception))
                {
                }
            }

            return paths.Count > 0 ? new TransferPayload.ImageFiles(paths) : null;
        }

        var images = new List<TransferPayload.ImageCandidates>();
        var sawImageFormat = false;
        long totalBytes = 0;
        ImageInputLimitException? limitFailure = null;
        foreach (var item in items)
        {
            var candidates = new List<ReadOnlyMemory<byte>>();
            foreach (var format in item.Formats.Where(IsImageFormat))
            {
                sawImageFormat = true;
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var raw = await item.Read(format);
                    ReadOnlyMemory<byte> bytes;
                    if (raw is byte[] encoded)
                    {
                        _limits.ValidateEncodedLength(encoded.Length);
                        bytes = encoded;
                    }
                    else if (raw is Bitmap bitmap)
                    {
                        _limits.ValidateDimensions(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                        using var buffer = new BoundedMemoryStream(_limits.MaxEncodedBytes);
                        bitmap.Save(buffer);
                        bytes = buffer.ToArray();
                    }
                    else
                    {
                        continue;
                    }

                    if (totalBytes + bytes.Length > MaximumSnapshotBytes)
                    {
                        throw new ImageInputLimitException("Transfer snapshot exceeds the memory budget.");
                    }

                    candidates.Add(bytes.ToArray());
                    totalBytes += bytes.Length;
                }
                catch (ImageInputLimitException exception)
                {
                    limitFailure ??= exception;
                }
                catch (Exception exception) when (IsRepresentationFailure(exception))
                {
                    // Reading one advertised format may fail while another remains valid.
                }
            }

            if (candidates.Count > 0)
            {
                images.Add(new TransferPayload.ImageCandidates(candidates));
            }
        }

        if (images.Count > 0)
        {
            return images.Count == 1 ? images[0] : new TransferPayload.ImageBatch(images);
        }

        if (limitFailure is not null)
        {
            throw limitFailure;
        }

        if (sawImageFormat)
        {
            return null;
        }

        foreach (var item in items.Where(item => item.Formats.Contains(DataFormat.Text)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await item.Read(DataFormat.Text) is string text && !string.IsNullOrWhiteSpace(text))
                {
                    return new TransferPayload.Text(text);
                }
            }
            catch (Exception exception) when (IsRepresentationFailure(exception))
            {
            }
        }

        return null;
    }

    public static bool IsImageFormat(DataFormat format) => format == DataFormat.Bitmap || IsEncodedImageFormat(format.Identifier);

    public static bool IsEncodedImageFormat(string identifier) => identifier is
        "public.png" or "public.tiff" or "public.jpeg" or "com.compuserve.gif" or "com.microsoft.bmp" or
        "org.webmproject.webp" or "image/png" or "image/tiff" or "image/jpeg" or "image/gif" or "image/bmp" or "image/webp";

    private static bool IsRepresentationFailure(Exception exception) => exception is not
        (OperationCanceledException or OutOfMemoryException or StackOverflowException);

    private sealed record SourceItem(IReadOnlyList<DataFormat> Formats, Func<DataFormat, Task<object?>> Read);

    private sealed class BoundedMemoryStream(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            Check(1);
            base.WriteByte(value);
        }

        private void Check(int count)
        {
            if (Position + count > maximum)
            {
                throw new ImageInputLimitException("Encoded bitmap exceeds the input budget.");
            }
        }
    }
}
