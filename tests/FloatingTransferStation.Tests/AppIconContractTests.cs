using System.Buffers.Binary;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 应用图标产物契约（2026-10-14 冻结）：帧集、ICO 结构与 256 母版的几何/配色。
/// 几何与配色的唯一事实源是 src/FloatingTransferStation/Assets/AppIcon/app-icon.json，
/// 产物由 src/FloatingTransferStation.IconTool 确定性生成；本文件锁定的是产物必须满足的语义：
/// 右侧竖线（#44356F 胶囊）+ 贴边直角长方形（#9C8CFF，竖版，宽 &lt; 竖线高，右长边完整贴合）。
/// </summary>
[TestClass]
public sealed class AppIconContractTests
{
    private static readonly int[] ExpectedFrameSizes = { 16, 20, 24, 32, 48, 64, 128, 256 };

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly Rgba32 RectColor = new(0x9C, 0x8C, 0xFF, 0xFF);
    private static readonly Rgba32 LineColor = new(0x44, 0x35, 0x6F, 0xFF);

    [TestMethod]
    public void Icon_FrameInventory_MatchesFrozenFrameSet()
    {
        var icon = File.ReadAllBytes(GetIconPath());
        Assert.AreEqual(0, BinaryPrimitives.ReadUInt16LittleEndian(icon), "ICONDIR reserved");
        Assert.AreEqual(1, BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(2)), "ICONDIR type = icon");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(4));
        Assert.AreEqual(ExpectedFrameSizes.Length, count, "ICONDIR frame count");

        var sizes = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = icon.AsSpan(6 + 16 * i, 16);
            var size = entry[0] == 0 ? 256 : entry[0];
            Assert.AreEqual(size, entry[1] == 0 ? 256 : entry[1], $"frame {size} must be square");
            sizes.Add(size);
            Assert.AreEqual(0, entry[2], $"frame {size} colorCount");
            Assert.AreEqual(0, entry[3], $"frame {size} reserved");
            Assert.AreEqual(1, BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]), $"frame {size} planes");
            Assert.AreEqual(32, BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]), $"frame {size} bitCount");

            var bytesInRes = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var imageOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            var data = icon.AsSpan(checked((int)imageOffset), checked((int)bytesInRes)).ToArray();
            Assert.IsTrue(imageOffset + bytesInRes <= (uint)icon.Length, $"frame {size} within file");

            if (size == 256)
            {
                Assert.IsTrue(data.AsSpan(0, 8).SequenceEqual(PngSignature), "the 256 frame must be PNG-encoded");
            }
            else
            {
                Assert.AreEqual(40, BinaryPrimitives.ReadInt32LittleEndian(data), $"frame {size} BITMAPINFOHEADER size");
                Assert.AreEqual(
                    2 * size,
                    BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8)),
                    $"frame {size} biHeight must cover XOR plus AND masks");
                var andStride = (size + 31) / 32 * 4;
                Assert.AreEqual(
                    (uint)(40 + size * size * 4 + andStride * size),
                    bytesInRes,
                    $"frame {size} bytesInRes must cover header, XOR and AND data");
            }
        }

        CollectionAssert.AreEqual(ExpectedFrameSizes, sizes, "frames must be ascending and exactly the frozen set");
    }

    [TestMethod]
    public void Icon_Master256Frame_HoldsGeometryPaletteAndSeamContract()
    {
        using var image = Image.Load<Rgba32>(ExtractFrame(File.ReadAllBytes(GetIconPath()), 256));
        Assert.AreEqual(256, image.Width, "master canvas width");
        Assert.AreEqual(256, image.Height, "master canvas height");

        var rect = FindBounds(image, RectColor);
        var line = FindBounds(image, LineColor);

        Assert.IsTrue(rect.Height > rect.Width, "the panel rectangle must be portrait (height > width)");
        Assert.IsTrue(rect.Width < line.Height, "the panel width must stay below the rail height");
        Assert.AreEqual(line.Left, rect.Right + 1, "panel right long edge must sit flush on the rail left edge");
        Assert.IsTrue(Math.Abs(2 * rect.Top + rect.Height - 256) <= 2, "panel must be vertically centered within 1px");
        Assert.IsTrue(Math.Abs(2 * line.Top + line.Height - 256) <= 2, "rail must be vertically centered within 1px");

        Assert.AreEqual(RectColor, image[120, 128], "panel interior sample (120,128)");
        Assert.AreEqual(LineColor, image[184, 128], "rail interior sample (184,128)");
        Assert.AreEqual(0, image[10, 10].A, "corner (10,10) must stay transparent");
    }

    [TestMethod]
    public void Icon_Dib16Frame_EncodesExactPixelsAndEmptyAndMask()
    {
        var dib = ExtractFrame(File.ReadAllBytes(GetIconPath()), 16);

        // 16px override（规格唯一事实源 app-icon.json）：line=(11,2,1,12,0)、rect=(4,3,7,10,0)。
        // 整数对齐直边零走样：采样点必须是精确纯色；透明像素必须全零。
        CollectionAssert.AreEqual(new byte[] { 0xFF, 0x8C, 0x9C, 0xFF }, ReadDibPixel(dib, 16, 4, 3), "rect (4,3) BGRA");
        CollectionAssert.AreEqual(new byte[] { 0xFF, 0x8C, 0x9C, 0xFF }, ReadDibPixel(dib, 16, 10, 12), "rect bottom-right (10,12) BGRA");
        CollectionAssert.AreEqual(new byte[] { 0x6F, 0x35, 0x44, 0xFF }, ReadDibPixel(dib, 16, 11, 2), "rail top (11,2) BGRA");
        CollectionAssert.AreEqual(new byte[] { 0x6F, 0x35, 0x44, 0xFF }, ReadDibPixel(dib, 16, 11, 13), "rail bottom (11,13) BGRA");
        CollectionAssert.AreEqual(new byte[] { 0x00, 0x00, 0x00, 0x00 }, ReadDibPixel(dib, 16, 0, 0), "corner (0,0) must stay transparent");

        for (var i = 40 + 16 * 16 * 4; i < dib.Length; i++)
        {
            Assert.AreEqual(0x00, dib[i], $"AND mask byte {i} must be zero");
        }
    }

    private static byte[] ReadDibPixel(byte[] dib, int size, int x, int y)
    {
        var offset = 40 + ((size - 1 - y) * size * 4) + (x * 4);
        return [dib[offset], dib[offset + 1], dib[offset + 2], dib[offset + 3]];
    }

    private static string GetIconPath()
    {
        return Path.Combine(FindRepositoryRoot(), "src", "FloatingTransferStation", "Assets", "AppIcon.ico");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FloatingTransferStation.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found from the test output.");
    }

    private static byte[] ExtractFrame(byte[] icon, int size)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(4));
        for (var i = 0; i < count; i++)
        {
            var entry = icon.AsSpan(6 + 16 * i, 16);
            var entrySize = entry[0] == 0 ? 256 : entry[0];
            if (entrySize != size)
            {
                continue;
            }

            var bytesInRes = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var imageOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            return icon.AsSpan(checked((int)imageOffset), checked((int)bytesInRes)).ToArray();
        }

        Assert.Fail($"Frame {size} was not found in the icon directory.");
        return [];
    }

    private static PixelBounds FindBounds(Image<Rgba32> image, Rgba32 color)
    {
        var left = int.MaxValue;
        var right = int.MinValue;
        var top = int.MaxValue;
        var bottom = int.MinValue;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (image[x, y] != color)
                {
                    continue;
                }

                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        Assert.AreNotEqual(int.MaxValue, left, $"expected color {color} to appear in the icon");
        return new PixelBounds(left, right, top, bottom);
    }

    private readonly record struct PixelBounds(int Left, int Right, int Top, int Bottom)
    {
        public int Width => Right - Left + 1;

        public int Height => Bottom - Top + 1;
    }
}
