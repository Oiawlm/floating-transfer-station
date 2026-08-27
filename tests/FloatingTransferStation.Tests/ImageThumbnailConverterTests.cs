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
