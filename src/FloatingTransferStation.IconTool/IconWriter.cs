using System.IO;
using System.Text;

namespace FloatingTransferStation.IconTool;

internal sealed class IconWriter
{
    internal static byte[] Encode(IReadOnlyList<RenderedIconFrame> frames)
    {
        if (frames.Count == 0 || frames.Count > ushort.MaxValue)
        {
            throw new ArgumentException(
                "ICO 帧数必须在 1 至 65535 之间。",
                nameof(frames));
        }

        RenderedIconFrame[] ordered = frames
            .OrderBy(frame => frame.Size)
            .ToArray();

        EncodedIconFrame[] encoded = new EncodedIconFrame[ordered.Length];

        for (int index = 0; index < ordered.Length; index++)
        {
            RenderedIconFrame frame = ordered[index];

            if (frame.Size < 1 || frame.Size > 256)
            {
                throw new ArgumentException(
                    "ICO 帧尺寸必须在 1 至 256 之间。",
                    nameof(frames));
            }

            if (index > 0 && ordered[index - 1].Size == frame.Size)
            {
                throw new ArgumentException(
                    "ICO 帧尺寸不得重复。",
                    nameof(frames));
            }

            if (frame.Bitmap.PixelWidth != frame.Size ||
                frame.Bitmap.PixelHeight != frame.Size)
            {
                throw new ArgumentException(
                    "位图像素尺寸与 ICO 帧尺寸不一致。",
                    nameof(frames));
            }

            byte[] payload = frame.Size == 256
                ? IconRenderer.EncodePng(frame.Bitmap)
                : EncodeDib(frame);

            encoded[index] = new EncodedIconFrame(frame.Size, payload);
        }

        uint firstImageOffset = checked((uint)(6 + encoded.Length * 16));
        uint totalLength = firstImageOffset;

        foreach (EncodedIconFrame frame in encoded)
        {
            totalLength = checked(totalLength + (uint)frame.Payload.Length);
        }

        using MemoryStream stream = new(checked((int)totalLength));

        // BinaryWriter 的所有整数写入均为小端；这里不写字符串。
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            // ICONDIR：6 字节。
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write(checked((ushort)encoded.Length));

            uint imageOffset = firstImageOffset;

            foreach (EncodedIconFrame frame in encoded)
            {
                byte dimension = frame.Size == 256
                    ? (byte)0
                    : checked((byte)frame.Size);

                // ICONDIRENTRY：每项恰好 16 字节。
                writer.Write(dimension);
                writer.Write(dimension);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(checked((uint)frame.Payload.Length));
                writer.Write(imageOffset);

                imageOffset = checked(
                    imageOffset + (uint)frame.Payload.Length);
            }

            foreach (EncodedIconFrame frame in encoded)
            {
                writer.Write(frame.Payload);
            }

            writer.Flush();
        }

        if (stream.Length != totalLength)
        {
            throw new InvalidOperationException("ICO 实际长度与目录计算长度不一致。");
        }

        return stream.ToArray();
    }

    private static byte[] EncodeDib(RenderedIconFrame frame)
    {
        int size = frame.Size;
        int xorStride = checked(size * 4);
        int xorLength = checked(xorStride * size);

        if (frame.Bgra32Pixels.Length != xorLength)
        {
            throw new ArgumentException(
                "直通 BGRA 像素缓冲长度与帧尺寸不一致。",
                nameof(frame));
        }

        // 1bpp AND 掩码每行补齐到 32 位，即 4 字节边界。
        int andStride = checked(((size + 31) / 32) * 4);
        int andLength = checked(andStride * size);
        int payloadLength = checked(40 + xorLength + andLength);

        using MemoryStream stream = new(payloadLength);

        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            // BITMAPINFOHEADER：40 字节。
            writer.Write(40u);                   // biSize
            writer.Write(size);                  // biWidth
            writer.Write(checked(size * 2));     // biHeight：XOR + AND
            writer.Write((ushort)1);             // biPlanes
            writer.Write((ushort)32);            // biBitCount
            writer.Write(0u);                    // biCompression：BI_RGB
            writer.Write(0u);                    // biSizeImage
            writer.Write(0);                     // biXPelsPerMeter
            writer.Write(0);                     // biYPelsPerMeter
            writer.Write(0u);                    // biClrUsed
            writer.Write(0u);                    // biClrImportant

            // CopyPixels 缓冲为自顶向下；DIB 的正高度要求自底向上。
            // 32bpp 的每行 size * 4 字节天然满足 4 字节对齐。
            for (int y = size - 1; y >= 0; y--)
            {
                writer.Write(
                    frame.Bgra32Pixels,
                    checked(y * xorStride),
                    xorStride);
            }

            // AND 位及行尾补位全部为 0；alpha 由 32bpp XOR 像素提供。
            byte[] emptyMaskRow = new byte[andStride];

            for (int y = size - 1; y >= 0; y--)
            {
                writer.Write(emptyMaskRow);
            }

            writer.Flush();
        }

        if (stream.Length != payloadLength)
        {
            throw new InvalidOperationException("DIB 实际长度与格式计算长度不一致。");
        }

        return stream.ToArray();
    }

    private sealed record EncodedIconFrame(int Size, byte[] Payload);
}
