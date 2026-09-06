using System.Windows;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public sealed class WindowsDataImageReader
{
    private static readonly string[] EncodedImageFormats = ["PNG", "image/png", "JFIF", "image/jpeg"];
    private readonly ImageInputLimits _limits;

    public WindowsDataImageReader(ImageInputLimits? limits = null)
    {
        _limits = limits ?? ImageInputLimits.Default;
    }

    public bool CanRead(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return EncodedImageFormats.Any(format => HasData(data, format, autoConvert: false)) ||
            HasData(data, DataFormats.Bitmap, autoConvert: true);
    }

    public IReadOnlyList<ClipboardImageCandidate> ReadCandidates(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return ReadCandidatesCore(data);
    }

    private IReadOnlyList<ClipboardImageCandidate> ReadCandidatesCore(IDataObject data)
    {
        var candidates = new List<ClipboardImageCandidate>();
        ImageInputLimitException? limitFailure = null;
        foreach (var format in EncodedImageFormats)
        {
            try
            {
                if (!data.GetDataPresent(format, autoConvert: false))
                {
                    continue;
                }

                var bytes = CopyEncodedBytes(data.GetData(format, autoConvert: false));
                if (bytes.Length == 0 ||
                    candidates.Any(candidate =>
                        !candidate.IsBitmap && candidate.EncodedBytes.Span.SequenceEqual(bytes)))
                {
                    continue;
                }

                candidates.Add(ClipboardImageCandidate.FromEncoded(format, bytes));
            }
            catch (ImageInputLimitException exception)
            {
                limitFailure ??= exception;
            }
            catch
            {
                // A foreign provider can fail one representation while another remains usable.
            }
        }

        try
        {
            if (data.GetDataPresent(DataFormats.Bitmap, autoConvert: true) &&
                data.GetData(DataFormats.Bitmap, autoConvert: true) is BitmapSource bitmap)
            {
                _limits.ValidateDimensions(bitmap.PixelWidth, bitmap.PixelHeight);
                candidates.Add(ClipboardImageCandidate.FromBitmap(SnapshotBitmap(bitmap)));
            }
        }
        catch (ImageInputLimitException exception)
        {
            limitFailure ??= exception;
        }
        catch
        {
            // Preserve successfully copied encoded representations when bitmap access fails.
        }

        if (candidates.Count == 0 && limitFailure is not null)
        {
            throw limitFailure;
        }

        return candidates;
    }

    private static bool HasData(IDataObject data, string format, bool autoConvert)
    {
        try
        {
            return data.GetDataPresent(format, autoConvert);
        }
        catch
        {
            return false;
        }
    }

    private static BitmapSource SnapshotBitmap(BitmapSource bitmap)
    {
        var snapshot = bitmap.CloneCurrentValue();
        snapshot.Freeze();
        return snapshot;
    }

    private byte[] CopyEncodedBytes(object? data) => data switch
    {
        byte[] bytes => CopyBytes(bytes),
        Stream stream => CopyStream(stream),
        _ => []
    };

    private byte[] CopyBytes(byte[] bytes)
    {
        _limits.ValidateEncodedLength(bytes.LongLength);
        return bytes.ToArray();
    }

    private byte[] CopyStream(Stream source)
    {
        var originalPosition = source.CanSeek ? source.Position : 0;
        try
        {
            if (source.CanSeek)
            {
                _limits.ValidateEncodedLength(source.Length);
                source.Position = 0;
            }

            using var destination = new MemoryStream();
            var buffer = new byte[Math.Min(81920, _limits.MaxEncodedBytes)];
            while (true)
            {
                var remainingWithProbe = (long)_limits.MaxEncodedBytes - destination.Length + 1;
                var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remainingWithProbe));
                if (read == 0)
                {
                    break;
                }

                _limits.ValidateEncodedLength(destination.Length + read);
                destination.Write(buffer, 0, read);
            }

            return destination.ToArray();
        }
        finally
        {
            if (source.CanSeek)
            {
                source.Position = originalPosition;
            }
        }
    }
}
