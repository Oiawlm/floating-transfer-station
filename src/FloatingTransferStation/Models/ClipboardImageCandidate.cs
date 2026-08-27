using System.Windows.Media.Imaging;

namespace FloatingTransferStation.Models;

public sealed class ClipboardImageCandidate
{
    private ClipboardImageCandidate(
        string format,
        BitmapSource? bitmap,
        ReadOnlyMemory<byte> encodedBytes)
    {
        Format = format;
        Bitmap = bitmap;
        EncodedBytes = encodedBytes;
    }

    public string Format { get; }
    public BitmapSource? Bitmap { get; }
    public ReadOnlyMemory<byte> EncodedBytes { get; }
    public bool IsBitmap => Bitmap is not null;

    public static ClipboardImageCandidate FromBitmap(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (!bitmap.IsFrozen)
        {
            if (!bitmap.CanFreeze)
            {
                throw new ArgumentException("Clipboard bitmap must be freezable.", nameof(bitmap));
            }

            bitmap = bitmap.CloneCurrentValue();
            bitmap.Freeze();
        }

        return new ClipboardImageCandidate("Bitmap", bitmap, ReadOnlyMemory<byte>.Empty);
    }

    public static ClipboardImageCandidate FromEncoded(string format, ReadOnlySpan<byte> bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        if (bytes.IsEmpty)
        {
            throw new ArgumentException("Encoded clipboard image cannot be empty.", nameof(bytes));
        }

        return new ClipboardImageCandidate(format, null, bytes.ToArray());
    }
}
