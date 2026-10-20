namespace FloatingTransferStation.Services;

/// <summary>
/// favicon 图片格式的头部识别与内联尺寸读取（Core 纯字节解析，解码前的唯一尺寸真相源）：
/// PNG 读 IHDR、ICO 遍历目录项取最大帧、JPEG 扫 SOF 标记、BMP 读 BITMAPINFOHEADER。
/// SVG/WebP 等不支持的格式一律 Unknown（候选直接淘汰）。
/// </summary>
public enum FaviconImageFormat
{
    Unknown = 0,
    Png = 1,
    Ico = 2,
    Jpeg = 3,
    Bmp = 4
}

public static class FaviconImageInfo
{
    /// <summary>按文件签名识别格式（Content-Type 只作线索，大量站点 MIME 错标）。</summary>
    public static FaviconImageFormat DetectFormat(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8)
        {
            if (bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G')
            {
                return FaviconImageFormat.Png;
            }

            if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x00)
            {
                return FaviconImageFormat.Ico;
            }

            if (bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
            {
                return FaviconImageFormat.Bmp;
            }
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return FaviconImageFormat.Jpeg;
        }

        return FaviconImageFormat.Unknown;
    }

    /// <summary>读取内联尺寸（宽,高）；无法确定返回 null（候选按坏帧淘汰）。</summary>
    public static (int Width, int Height)? ReadIntrinsicSize(ReadOnlySpan<byte> bytes, FaviconImageFormat format) => format switch
    {
        FaviconImageFormat.Png => ReadPngSize(bytes),
        FaviconImageFormat.Ico => ReadIcoMaxFrameSize(bytes),
        FaviconImageFormat.Bmp => ReadBmpSize(bytes),
        FaviconImageFormat.Jpeg => ReadJpegSize(bytes),
        _ => null
    };

    private static (int, int)? ReadPngSize(ReadOnlySpan<byte> bytes)
    {
        // 签名(8) + IHDR 长度/类型(8)：宽 @16、高 @20（大端）。
        if (bytes.Length < 24)
        {
            return null;
        }

        var width = ReadInt32BigEndian(bytes[16..20]);
        var height = ReadInt32BigEndian(bytes[20..24]);
        return width > 0 && height > 0 ? (width, height) : null;
    }

    private static (int, int)? ReadIcoMaxFrameSize(ReadOnlySpan<byte> bytes)
    {
        // 头(6)：保留2+类型2+帧数2；每目录项 16 字节：宽@0、高@1（0 表示 256）。
        if (bytes.Length < 6)
        {
            return null;
        }

        var frameCount = BitConverter.ToUInt16(bytes[4..6]);
        if (frameCount == 0 || bytes.Length < 6 + frameCount * 16)
        {
            return null;
        }

        int maxWidth = 0, maxHeight = 0;
        for (var i = 0; i < frameCount && i < 16; i++)
        {
            var entry = bytes[(6 + i * 16)..];
            var width = entry[0] == 0 ? 256 : entry[0];
            var height = entry[1] == 0 ? 256 : entry[1];
            maxWidth = Math.Max(maxWidth, width);
            maxHeight = Math.Max(maxHeight, height);
        }

        return maxWidth > 0 && maxHeight > 0 ? (maxWidth, maxHeight) : null;
    }

    private static (int, int)? ReadBmpSize(ReadOnlySpan<byte> bytes)
    {
        // 文件头(14) + BITMAPINFOHEADER：宽 @18、高 @22（小端；高可为负=自上而下）。
        if (bytes.Length < 26)
        {
            return null;
        }

        var width = BitConverter.ToInt32(bytes[18..22]);
        var height = Math.Abs(BitConverter.ToInt32(bytes[22..26]));
        return width > 0 && height > 0 ? (width, height) : null;
    }

    private static (int, int)? ReadJpegSize(ReadOnlySpan<byte> bytes)
    {
        // 扫 SOF0/SOF1/SOF2..SOF15（跳过 D8/D9/D0-D7/01）：段长(2) + 精度(1) + 高(2) + 宽(2)。
        var offset = 2;
        while (offset + 9 < bytes.Length)
        {
            if (bytes[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            var marker = bytes[offset + 1];
            if (marker is 0xD8 or 0xD9 or 0x01 || (marker is >= 0xD0 and <= 0xD7))
            {
                offset += 2;
                continue;
            }

            var segmentLength = (bytes[offset + 2] << 8) | bytes[offset + 3];
            if (marker is >= 0xC0 and <= 0xCF && marker is not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (offset + 9 > bytes.Length)
                {
                    return null;
                }

                var height = (bytes[offset + 5] << 8) | bytes[offset + 6];
                var width = (bytes[offset + 7] << 8) | bytes[offset + 8];
                return width > 0 && height > 0 ? (width, height) : null;
            }

            offset += 2 + segmentLength;
        }

        return null;
    }

    private static int ReadInt32BigEndian(ReadOnlySpan<byte> bytes) =>
        (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
}
