using System.Windows;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public sealed class WindowsDataImageReader
{
    private static readonly string[] EncodedImageFormats = ["PNG", "image/png", "JFIF", "image/jpeg"];

    public bool CanRead(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            return EncodedImageFormats.Any(format => data.GetDataPresent(format, autoConvert: false)) ||
                data.GetDataPresent(DataFormats.Bitmap, autoConvert: true);
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<ClipboardImageCandidate> ReadCandidates(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            return ReadCandidatesCore(data);
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<ClipboardImageCandidate> ReadCandidatesCore(IDataObject data)
    {
        var candidates = new List<ClipboardImageCandidate>();
        foreach (var format in EncodedImageFormats)
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

        if (data.GetDataPresent(DataFormats.Bitmap, autoConvert: true) &&
            data.GetData(DataFormats.Bitmap, autoConvert: true) is BitmapSource bitmap)
        {
            candidates.Add(ClipboardImageCandidate.FromBitmap(SnapshotBitmap(bitmap)));
        }

        return candidates;
    }

    private static BitmapSource SnapshotBitmap(BitmapSource bitmap)
    {
        var snapshot = bitmap.CloneCurrentValue();
        snapshot.Freeze();
        return snapshot;
    }

    private static byte[] CopyEncodedBytes(object? data) => data switch
    {
        byte[] bytes => bytes.ToArray(),
        Stream stream => CopyStream(stream),
        _ => []
    };

    private static byte[] CopyStream(Stream source)
    {
        var originalPosition = source.CanSeek ? source.Position : 0;
        try
        {
            if (source.CanSeek)
            {
                source.Position = 0;
            }

            using var destination = new MemoryStream();
            source.CopyTo(destination);
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
