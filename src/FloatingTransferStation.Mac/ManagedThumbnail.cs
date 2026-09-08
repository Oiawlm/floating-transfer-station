using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Mac;

internal sealed class ManagedThumbnail(string imagesDirectory, string path) : Image
{
    private Bitmap? _bitmap;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Stretch = Stretch.Uniform;
        MaxHeight = 240;
        try
        {
            if (!ManagedImagePath.IsAllowed(imagesDirectory, path)) return;
            using var stream = File.OpenRead(path);
            var limits = ImageInputLimits.Default;
            limits.ValidateEncodedLength(stream.Length);
            var info = SixLabors.ImageSharp.Image.Identify(limits.IdentifyFirstFrame, stream);
            limits.ValidateDimensions(info.Width, info.Height);
            stream.Position = 0;
            _bitmap = info.Height > info.Width * 1.5
                ? Bitmap.DecodeToHeight(stream, Math.Min(480, info.Height))
                : Bitmap.DecodeToWidth(stream, Math.Min(320, info.Width));
            Source = _bitmap;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException)
        {
            ToolTip.SetTip(this, "图片暂时无法预览");
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        base.OnDetachedFromVisualTree(e);
    }
}
