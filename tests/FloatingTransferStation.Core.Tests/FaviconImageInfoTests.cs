using FloatingTransferStation.Services;
using System.Text;

namespace FloatingTransferStation.Tests;

/// <summary>
/// favicon 图片头部识别与内联尺寸读取（#44，Issue #206）：解码前的唯一尺寸真相源——
/// PNG IHDR、ICO 目录项最大帧（0=256）、JPEG SOF 扫描、BMP 负高；不支持格式与截断
/// 头部一律不可读（候选淘汰面）。
/// </summary>
[TestClass]
public sealed class FaviconImageInfoTests
{
    [TestMethod]
    public void DetectFormat_PngIcoJpegBmpAndUnknown()
    {
        Assert.AreEqual(FaviconImageFormat.Png, FaviconImageInfo.DetectFormat(RealPngHeader()));
        Assert.AreEqual(FaviconImageFormat.Ico, FaviconImageInfo.DetectFormat(IcoBytes(1, new[] { (byte)32, (byte)32 })));
        Assert.AreEqual(FaviconImageFormat.Jpeg, FaviconImageInfo.DetectFormat(JpegBytes(64, 48)));
        Assert.AreEqual(FaviconImageFormat.Bmp, FaviconImageInfo.DetectFormat(BmpBytes(20, 10)));
        Assert.AreEqual(FaviconImageFormat.Unknown, FaviconImageInfo.DetectFormat("<svg xmlns='x'/>"u8.ToArray()), "SVG 按签名淘汰。");
        Assert.AreEqual(FaviconImageFormat.Unknown, FaviconImageInfo.DetectFormat("RIFF____WEBPVP8 "u8.ToArray()), "WebP 按签名淘汰。");
    }

    [TestMethod]
    public void ReadIntrinsicSize_Png_IhdrBigEndian()
    {
        var (width, height) = FaviconImageInfo.ReadIntrinsicSize(RealPngHeader(), FaviconImageFormat.Png)!.Value;
        Assert.AreEqual(96, width);
        Assert.AreEqual(64, height);
    }

    [TestMethod]
    public void ReadIntrinsicSize_Ico_TakesMaxFrameAndZeroMeans256()
    {
        // 两帧：32×32 与 0×0（=256×256）——取最大帧 256。
        var bytes = IcoBytes(2, new byte[] { 32, 32, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        var (width, height) = FaviconImageInfo.ReadIntrinsicSize(bytes, FaviconImageFormat.Ico)!.Value;
        Assert.AreEqual(256, width);
        Assert.AreEqual(256, height);
    }

    [TestMethod]
    public void ReadIntrinsicSize_Jpeg_SofScan()
    {
        var (width, height) = FaviconImageInfo.ReadIntrinsicSize(JpegBytes(64, 48), FaviconImageFormat.Jpeg)!.Value;
        Assert.AreEqual(64, width);
        Assert.AreEqual(48, height);
    }

    [TestMethod]
    public void ReadIntrinsicSize_Bmp_NegativeHeightMeansTopDown()
    {
        var (width, height) = FaviconImageInfo.ReadIntrinsicSize(BmpBytes(20, -10), FaviconImageFormat.Bmp)!.Value;
        Assert.AreEqual(20, width);
        Assert.AreEqual(10, height, "负高（自上而下）取绝对值。");
    }

    [TestMethod]
    public void ReadIntrinsicSize_TruncatedHeaders_ReturnNull()
    {
        Assert.IsNull(FaviconImageInfo.ReadIntrinsicSize(RealPngHeader()[..20], FaviconImageFormat.Png), "截断 IHDR 不可读。");
        Assert.IsNull(FaviconImageInfo.ReadIntrinsicSize(new byte[] { 0, 0, 1, 0 }, FaviconImageFormat.Ico), "无目录项不可读。");
        Assert.IsNull(FaviconImageInfo.ReadIntrinsicSize(new byte[] { 0xFF, 0xD8, 0xFF }, FaviconImageFormat.Jpeg), "截断 SOF 不可读。");
    }

    private static byte[] RealPngHeader()
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 };
        bytes.AddRange(Encoding.ASCII.GetBytes("IHDR"));
        bytes.AddRange([(byte)(96 >> 24), (byte)(96 >> 16), (byte)(96 >> 8), 96, (byte)(64 >> 24), (byte)(64 >> 16), (byte)(64 >> 8), 64, 8, 6, 0, 0, 0]);
        return [.. bytes];
    }

    private static byte[] IcoBytes(byte frameCount, byte[] entries)
    {
        var bytes = new List<byte> { 0, 0, 1, 0, frameCount, 0 };
        bytes.AddRange(entries);
        return [.. bytes];
    }

    private static byte[] JpegBytes(int width, int height)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        bytes.AddRange([0xFF, 0xE0, 0, 4, 0x4A, 0x46, 0x49, 0x46, 0]);   // APP0 段
        bytes.AddRange([0xFF, 0xC0, 0, 17, 8]);                            // SOF0：段长 17、精度 8
        bytes.AddRange([(byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11]);
        return [.. bytes];
    }

    private static byte[] BmpBytes(int width, int height)
    {
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes("BM"));
        bytes.AddRange(new byte[12]);
        bytes.AddRange([40, 0, 0, 0]);
        bytes.AddRange([(byte)width, 0, 0, 0]);
        bytes.AddRange([(byte)height, 0, 0, 0]);
        if (height < 0)
        {
            // 负高按小端补码：高字节置 0xFF 链（简化：直接写 32 位小端）。
            var offset = bytes.Count - 4;
            var value = unchecked((uint)height);
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        bytes.AddRange([1, 0, 32, 0]);
        return [.. bytes];
    }
}
