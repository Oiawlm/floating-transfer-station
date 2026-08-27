namespace FloatingTransferStation.Services;

public static class ImageFileSupport
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp"
    };

    public static bool IsSupported(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Extensions.Contains(Path.GetExtension(path));

    public static bool IsSupportedStaticImageFile(string path)
    {
        if (!IsSupported(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var information = SixLabors.ImageSharp.Image.Identify(path);
            // Some single-frame decoders omit per-frame metadata for the implicit root frame.
            var identifiedFrameCount = Math.Max(1, information.FrameMetadataCollection.Count);
            return identifiedFrameCount == 1;
        }
        catch
        {
            return false;
        }
    }
}
