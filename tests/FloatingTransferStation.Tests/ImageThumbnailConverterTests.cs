using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Converters;

namespace FloatingTransferStation.Tests;

[TestClass]
[TestCategory("Adversarial")]
public sealed class ImageThumbnailConverterTests
{
    [STATestMethod]
    public void Convert_ExplicitTextWidthDecodesProportionallyAndReleasesSourceFile()
    {
        Assert.AreEqual(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "large.png");
        WritePng(path, width: 1200, height: 600);
        var converter = new ImageThumbnailConverter();

        var result = (BitmapSource?)converter.Convert(
            path,
            typeof(BitmapSource),
            "512",
            CultureInfo.InvariantCulture);

        Assert.IsNotNull(result);
        Assert.AreEqual(512, result.PixelWidth);
        Assert.AreEqual(256, result.PixelHeight);
        Assert.IsTrue(result.IsFrozen);

        File.Delete(path);
        Assert.IsFalse(File.Exists(path));
    }

    [STATestMethod]
    public void Convert_NoWidthUsesDefault()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "default.png");
        WritePng(path, width: 1200, height: 600);
        var converter = new ImageThumbnailConverter();

        var result = (BitmapSource?)converter.Convert(
            path,
            typeof(BitmapSource),
            null,
            CultureInfo.InvariantCulture);

        Assert.IsNotNull(result);
        Assert.AreEqual(512, result.PixelWidth);
        Assert.AreEqual(256, result.PixelHeight);
    }

    [STATestMethod]
    public void Convert_NumericWidthIsSupported()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "numeric.png");
        WritePng(path, width: 1200, height: 600);
        var converter = new ImageThumbnailConverter();

        var result = (BitmapSource?)converter.Convert(
            path,
            typeof(BitmapSource),
            300,
            CultureInfo.InvariantCulture);

        Assert.IsNotNull(result);
        Assert.AreEqual(300, result.PixelWidth);
        Assert.AreEqual(150, result.PixelHeight);
    }

    [STATestMethod]
    public void Convert_InvalidWidthUsesDefault()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "invalid-width.png");
        WritePng(path, width: 1200, height: 600);
        var converter = new ImageThumbnailConverter();

        var result = (BitmapSource?)converter.Convert(
            path,
            typeof(BitmapSource),
            "not-a-width",
            CultureInfo.InvariantCulture);

        Assert.IsNotNull(result);
        Assert.AreEqual(512, result.PixelWidth);
    }

    [STATestMethod]
    public void Convert_NonPositiveWidthIsClampedToOne()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "minimum-width.png");
        WritePng(path, width: 1200, height: 600);
        var converter = new ImageThumbnailConverter();

        var result = (BitmapSource?)converter.Convert(
            path,
            typeof(BitmapSource),
            0,
            CultureInfo.InvariantCulture);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.PixelWidth);
    }

    [STATestMethod]
    public void Convert_NumericWidthAbovePreviewMaximumIsClamped()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "maximum-numeric-width.png");
        WritePng(path, width: 4096, height: 2);
        var converter = new ImageThumbnailConverter();

        var result = (BitmapSource?)converter.Convert(
            path,
            typeof(BitmapSource),
            int.MaxValue,
            CultureInfo.InvariantCulture);

        Assert.IsNotNull(result);
        Assert.AreEqual(2048, result.PixelWidth);
    }

    [STATestMethod]
    public void Convert_TextWidthAbovePreviewMaximumIsClamped()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "maximum-text-width.png");
        WritePng(path, width: 4096, height: 2);
        var converter = new ImageThumbnailConverter();

        var result = (BitmapSource?)converter.Convert(
            path,
            typeof(BitmapSource),
            int.MaxValue.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture);

        Assert.IsNotNull(result);
        Assert.AreEqual(2048, result.PixelWidth);
    }

    [STATestMethod]
    [DataRow(64, 8192, 512, 16)]
    [DataRow(1, 8192, 1, 1)]
    public void Convert_TallImageCapsDecodedHeightAndPreservesAspectRatio(int width, int height, int decodeWidth, int expectedWidth)
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "tall.png");
        WritePng(path, width, height);
        var converter = new ImageThumbnailConverter();

        var result = (BitmapSource?)converter.Convert(
            path,
            typeof(BitmapSource),
            decodeWidth,
            CultureInfo.InvariantCulture);

        Assert.IsNotNull(result);
        Assert.AreEqual(2048, result.PixelHeight);
        Assert.AreEqual(expectedWidth, result.PixelWidth);
        Assert.IsTrue(result.IsFrozen);
        File.Delete(path);
        Assert.IsFalse(File.Exists(path));
    }

    [STATestMethod]
    public void Convert_SmallImageRetainsRequestedWidth()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "small.png");
        WritePng(path, width: 16, height: 8);
        var converter = new ImageThumbnailConverter();

        var result = (BitmapSource?)converter.Convert(path, typeof(BitmapSource), 512, CultureInfo.InvariantCulture);

        Assert.IsNotNull(result);
        Assert.AreEqual(512, result.PixelWidth);
        Assert.AreEqual(256, result.PixelHeight);
    }

    [STATestMethod]
    public void Convert_UnchangedFileReusesFrozenPreviewForEquivalentWidths()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "reused.png");
        WritePng(path, width: 1200, height: 600);
        var converter = new ImageThumbnailConverter();

        var first = (BitmapSource?)converter.Convert(path, typeof(BitmapSource), 512, CultureInfo.InvariantCulture);
        var second = converter.Convert(path, typeof(BitmapSource), "512", CultureInfo.InvariantCulture);

        Assert.IsNotNull(first);
        Assert.IsTrue(first.IsFrozen);
        Assert.AreSame(first, second);
        File.Delete(path);
        Assert.IsNull(converter.Convert(path, typeof(BitmapSource), 512, CultureInfo.InvariantCulture));
    }

    [STATestMethod]
    public void Convert_DifferentWidthsKeepIndependentPreviews()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "widths.png");
        WritePng(path, width: 1200, height: 600);
        var converter = new ImageThumbnailConverter();

        var first = converter.Convert(path, typeof(BitmapSource), 512, CultureInfo.InvariantCulture);
        var second = (BitmapSource?)converter.Convert(path, typeof(BitmapSource), 300, CultureInfo.InvariantCulture);

        Assert.IsNotNull(second);
        Assert.AreEqual(300, second.PixelWidth);
        Assert.AreNotSame(first, second);
        Assert.AreSame(first, converter.Convert(path, typeof(BitmapSource), 512, CultureInfo.InvariantCulture));
    }

    [STATestMethod]
    public void Convert_ReplacedSourceRefreshesPreview()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "replaced.png");
        WritePng(path, width: 1200, height: 600);
        var converter = new ImageThumbnailConverter();
        var first = converter.Convert(path, typeof(BitmapSource), 512, CultureInfo.InvariantCulture);
        var timestamp = File.GetLastWriteTimeUtc(path);

        WritePng(path, width: 600, height: 1200);
        File.SetLastWriteTimeUtc(path, timestamp.AddMinutes(1));
        var second = (BitmapSource?)converter.Convert(path, typeof(BitmapSource), 512, CultureInfo.InvariantCulture);

        Assert.IsNotNull(second);
        Assert.AreNotSame(first, second);
        Assert.AreEqual(512, second.PixelWidth);
        Assert.AreEqual(1024, second.PixelHeight);
    }

    [STATestMethod]
    public void Convert_TimestampChangeRefreshesSameLengthSource()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "same-length-replacement.png");
        WritePng(path, width: 16, height: 8);
        var converter = new ImageThumbnailConverter();
        var first = converter.Convert(path, typeof(BitmapSource), 512, CultureInfo.InvariantCulture);
        var length = new FileInfo(path).Length;
        var timestamp = File.GetLastWriteTimeUtc(path);

        WritePng(path, width: 16, height: 8);
        File.SetLastWriteTimeUtc(path, timestamp.AddMinutes(1));
        var second = converter.Convert(path, typeof(BitmapSource), 512, CultureInfo.InvariantCulture);

        Assert.AreEqual(length, new FileInfo(path).Length);
        Assert.IsNotNull(second);
        Assert.AreNotSame(first, second);
    }

    [STATestMethod]
    public void Convert_CorruptReplacementReturnsNullThenRecovers()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "corrupt-replacement.png");
        WritePng(path, width: 16, height: 8);
        var converter = new ImageThumbnailConverter();
        var first = converter.Convert(path, typeof(BitmapSource), 512, CultureInfo.InvariantCulture);
        Assert.IsNotNull(first);
        var timestamp = File.GetLastWriteTimeUtc(path);

        File.WriteAllText(path, "corrupt replacement");
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.IsNull(converter.Convert(path, typeof(BitmapSource), 512, CultureInfo.InvariantCulture));

        WritePng(path, width: 16, height: 8);
        File.SetLastWriteTimeUtc(path, timestamp);
        var recovered = converter.Convert(path, typeof(BitmapSource), 512, CultureInfo.InvariantCulture);
        Assert.IsNotNull(recovered);
        Assert.AreNotSame(first, recovered);
    }

    [STATestMethod]
    public void Convert_ConcurrentRequestsShareFrozenPreviewsWithoutCacheRaces()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "concurrent.png");
        WritePng(path, width: 1200, height: 600);
        var converter = new ImageThumbnailConverter();
        var images = new BitmapSource?[32];

        Parallel.For(0, images.Length, index =>
        {
            images[index] = (BitmapSource?)converter.Convert(
                path,
                typeof(BitmapSource),
                index % 2 == 0 ? 512 : 300,
                CultureInfo.InvariantCulture);
        });

        for (var index = 0; index < images.Length; index++)
        {
            var image = images[index];
            Assert.IsNotNull(image);
            Assert.IsTrue(image.IsFrozen);
            Assert.AreEqual(index % 2 == 0 ? 512 : 300, image.PixelWidth);
            Assert.AreSame(images[index % 2], image);
        }

        File.Delete(path);
        Assert.IsFalse(File.Exists(path));
    }

    [STATestMethod]
    public void Convert_CacheEvictsLeastRecentlyUsedPreviewAfterSixtyFourEntries()
    {
        using var directory = new TestDirectory();
        var converter = new ImageThumbnailConverter();
        var paths = Enumerable.Range(0, 65)
            .Select(index => Path.Combine(directory.Root, $"cached-{index}.png"))
            .ToArray();
        foreach (var path in paths)
        {
            WritePng(path, width: 16, height: 8);
        }

        var first = converter.Convert(paths[0], typeof(BitmapSource), 32, CultureInfo.InvariantCulture);
        var second = converter.Convert(paths[1], typeof(BitmapSource), 32, CultureInfo.InvariantCulture);
        foreach (var path in paths.Skip(2).Take(62))
        {
            Assert.IsNotNull(converter.Convert(path, typeof(BitmapSource), 32, CultureInfo.InvariantCulture));
        }

        Assert.AreSame(first, converter.Convert(paths[0], typeof(BitmapSource), 32, CultureInfo.InvariantCulture));
        Assert.IsNotNull(converter.Convert(paths[64], typeof(BitmapSource), 32, CultureInfo.InvariantCulture));

        Assert.AreSame(first, converter.Convert(paths[0], typeof(BitmapSource), 32, CultureInfo.InvariantCulture));
        Assert.AreNotSame(second, converter.Convert(paths[1], typeof(BitmapSource), 32, CultureInfo.InvariantCulture));
    }

    [STATestMethod]
    public void Convert_CacheEvictsPreviewsAboveThirtyTwoMebibytes()
    {
        using var directory = new TestDirectory();
        var paths = Enumerable.Range(0, 3)
            .Select(index => Path.Combine(directory.Root, $"large-cached-{index}.png"))
            .ToArray();
        foreach (var path in paths)
        {
            WritePng(path, width: 2048, height: 2048);
        }

        var converter = new ImageThumbnailConverter();
        var first = converter.Convert(paths[0], typeof(BitmapSource), 2048, CultureInfo.InvariantCulture);
        var second = converter.Convert(paths[1], typeof(BitmapSource), 2048, CultureInfo.InvariantCulture);
        Assert.AreSame(first, converter.Convert(paths[0], typeof(BitmapSource), 2048, CultureInfo.InvariantCulture));
        Assert.IsNotNull(converter.Convert(paths[2], typeof(BitmapSource), 2048, CultureInfo.InvariantCulture));

        Assert.AreSame(first, converter.Convert(paths[0], typeof(BitmapSource), 2048, CultureInfo.InvariantCulture));
        Assert.AreNotSame(second, converter.Convert(paths[1], typeof(BitmapSource), 2048, CultureInfo.InvariantCulture));
    }

    [STATestMethod]
    public void Convert_InvalidPathValuesReturnNull()
    {
        using var directory = new TestDirectory();
        var missingPath = Path.Combine(directory.Root, "missing.png");
        var converter = new ImageThumbnailConverter();

        Assert.IsNull(converter.Convert(null, typeof(BitmapSource), "512", CultureInfo.InvariantCulture));
        Assert.IsNull(converter.Convert(42, typeof(BitmapSource), "512", CultureInfo.InvariantCulture));
        Assert.IsNull(converter.Convert(string.Empty, typeof(BitmapSource), "512", CultureInfo.InvariantCulture));
        Assert.IsNull(converter.Convert(missingPath, typeof(BitmapSource), "512", CultureInfo.InvariantCulture));
    }

    [STATestMethod]
    public void Convert_ExistingNonImageFileReturnsNull()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "not-an-image.png");
        File.WriteAllText(path, "not an image");
        var converter = new ImageThumbnailConverter();

        var result = converter.Convert(
            path,
            typeof(BitmapSource),
            "512",
            CultureInfo.InvariantCulture);

        Assert.IsNull(result);
    }

    [STATestMethod]
    public void Convert_TruncatedPngReturnsNull()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "truncated.png");
        WritePng(path, width: 16, height: 8);
        var encoded = File.ReadAllBytes(path);
        File.WriteAllBytes(path, encoded[..(encoded.Length / 2)]);
        var converter = new ImageThumbnailConverter();

        var result = converter.Convert(
            path,
            typeof(BitmapSource),
            "512",
            CultureInfo.InvariantCulture);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void ConvertBack_ReturnsBindingDoNothing()
    {
        var converter = new ImageThumbnailConverter();

        var result = converter.ConvertBack(
            new object(),
            typeof(string),
            new object(),
            CultureInfo.InvariantCulture);

        Assert.AreSame(Binding.DoNothing, result);
    }

    private static void WritePng(string path, int width, int height)
    {
        const int bytesPerPixel = 4;
        var stride = width * bytesPerPixel;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, (byte)0x7F);
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
