using System.Windows.Media.Imaging;

namespace FloatingTransferStation.Models;

public sealed record ClipboardSnapshot(
    uint SequenceNumber,
    BitmapSource? Image,
    IReadOnlyList<string> FilePaths,
    string? Text,
    IReadOnlyList<ClipboardImageCandidate>? EncodedImages = null)
{
    public IReadOnlyList<ClipboardImageCandidate> ImageCandidates
    {
        get
        {
            if (Image is null)
            {
                return EncodedImages ?? [];
            }

            if (EncodedImages is null || EncodedImages.Count == 0)
            {
                return [ClipboardImageCandidate.FromBitmap(Image)];
            }

            return [ClipboardImageCandidate.FromBitmap(Image), .. EncodedImages];
        }
    }
}
