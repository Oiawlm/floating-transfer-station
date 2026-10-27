using System.Buffers.Binary;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;

namespace FloatingTransferStation.Services;

public static class ImageFileSupport
{
    // 契约 #50 唯一事实源：一行 = 一种产品可见的静态图片输入格式（解码模块＋路由扩展名）。
    // 扩展名白名单与 ImageInputLimits 的受限解码 Configuration 都从本表派生——加/删一种
    // 格式两层必须同步变化，ImageInputVectorLockTests 双向互锁。TIFF 自 1.64.0 起整体移除
    // （GHSA-v76p-62qx-wwq2 解码越界堆写；ImageSharp 按内容魔数识别格式、不看扩展名，
    // 只删白名单条目挡不住改名的 TIFF 字节，必须连解码器一起收走）。
    private static readonly (IImageFormatConfigurationModule Module, string[] Extensions)[] AllowedInputFormats =
    [
        (new PngConfigurationModule(), [".png"]),
        (new JpegConfigurationModule(), [".jpg", ".jpeg"]),
        (new GifConfigurationModule(), [".gif"]),
        (new BmpConfigurationModule(), [".bmp"]),
        (new WebpConfigurationModule(), [".webp"]),
    ];

    private static readonly HashSet<string> Extensions = new(
        AllowedInputFormats.SelectMany(format => format.Extensions),
        StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyCollection<string> SupportedExtensions => Extensions;

    internal static Configuration CreateRestrictedConfiguration() =>
        new(AllowedInputFormats.Select(format => format.Module).ToArray());

    public static bool IsSupported(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Extensions.Contains(Path.GetExtension(path));

    public static bool IsSupportedStaticImageFile(string path)
        => IsSupportedStaticImageFile(path, ImageInputLimits.Default);

    public static bool IsSupportedStaticImageFile(string path, ImageInputLimits limits)
    {
        if (!IsSupported(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            limits.ValidateEncodedLength(source.Length);
            var information = SixLabors.ImageSharp.Image.Identify(limits.IdentifyStaticImage, source);
            limits.ValidateDimensions(information.Width, information.Height);
            return HasSingleFrame(information, source);
        }
        catch
        {
            return false;
        }
    }

    internal static bool HasSingleFrame(ImageInfo information, Stream source)
    {
        if (information.FrameMetadataCollection.Count > 1)
        {
            return false;
        }

        if (information.Metadata.DecodedImageFormat is not PngFormat)
        {
            return true;
        }

        // ImageSharp 3.1.12 Identify omits APNG frame metadata. Inspect chunk headers
        // without decoding a second full-resolution frame just to validate a drag.
        var originalPosition = source.Position;
        try
        {
            source.Position = 8; // PNG signature was validated by Identify.
            Span<byte> header = stackalloc byte[8];
            Span<byte> frameCount = stackalloc byte[4];
            var seenImageData = false;
            while (source.Position < source.Length)
            {
                source.ReadExactly(header);
                var length = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
                if ((long)length + 4 > source.Length - source.Position)
                {
                    return false;
                }

                var nextChunk = source.Position + length + 4;
                var type = header[4..];
                if (type.SequenceEqual("acTL"u8))
                {
                    if (length != 8)
                    {
                        return false;
                    }

                    source.ReadExactly(frameCount);
                    if (BinaryPrimitives.ReadUInt32BigEndian(frameCount) != 1)
                    {
                        return false;
                    }
                }
                else if (type.SequenceEqual("fdAT"u8) || (seenImageData && type.SequenceEqual("fcTL"u8)))
                {
                    return false;
                }
                else if (type.SequenceEqual("IDAT"u8))
                {
                    seenImageData = true;
                }
                else if (type.SequenceEqual("IEND"u8))
                {
                    return true;
                }

                source.Position = nextChunk;
            }

            return false;
        }
        finally
        {
            source.Position = originalPosition;
        }
    }
}
