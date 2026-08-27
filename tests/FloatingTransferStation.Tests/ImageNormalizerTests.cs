using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class ImageNormalizerTests
{
    [TestMethod]
    public async Task NormalizeFile_JpegBecomesManagedPng()
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Root, "source.jpg");
        using (var image = new Image<Rgba32>(3, 2, SixLabors.ImageSharp.Color.CornflowerBlue))
        {
            await image.SaveAsJpegAsync(source);
        }
        var normalizer = new ImageNormalizer(AppPaths.ForTests(directory.Root).ImagesDirectory);

        var stored = await normalizer.NormalizeFileAsync(
            source,
            Guid.Parse("00000000-0000-0000-0000-000000000101"));

        Assert.AreEqual("images/00000000000000000000000000000101.png", stored.RelativePath);
        Assert.IsTrue(File.Exists(stored.AbsolutePath));
        CollectionAssert.AreEqual(
            new byte[] { 0x89, 0x50, 0x4E, 0x47 },
            (await File.ReadAllBytesAsync(stored.AbsolutePath))[..4]);
    }

    [TestMethod]
    [DataRow("png")]
    [DataRow("bmp")]
    [DataRow("tiff")]
    [DataRow("webp")]
    public async Task NormalizeFile_CommonStaticFormatBecomesManagedPng(string extension)
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Root, $"source.{extension}");
        using (var image = new Image<Rgba32>(2, 2, SixLabors.ImageSharp.Color.CornflowerBlue))
        {
            await image.SaveAsync(source);
        }
        var normalizer = new ImageNormalizer(AppPaths.ForTests(directory.Root).ImagesDirectory);

        var stored = await normalizer.NormalizeFileAsync(source);

        CollectionAssert.AreEqual(
            new byte[] { 0x89, 0x50, 0x4E, 0x47 },
            (await File.ReadAllBytesAsync(stored.AbsolutePath))[..4]);
    }

    [TestMethod]
    public async Task NormalizeFile_AnimatedGifKeepsOneStaticFrame()
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Root, "animated.gif");
        using (var image = new Image<Rgba32>(2, 2, SixLabors.ImageSharp.Color.Red))
        {
            image.Frames.AddFrame(image.Frames.RootFrame);
            await image.SaveAsGifAsync(source);
        }
        var normalizer = new ImageNormalizer(AppPaths.ForTests(directory.Root).ImagesDirectory);

        var stored = await normalizer.NormalizeFileAsync(source);

        using var loaded = await Image.LoadAsync(stored.AbsolutePath);
        Assert.AreEqual(1, loaded.Frames.Count);
    }

    [STATestMethod]
    public async Task NormalizeBitmap_WpfBitmapBecomesManagedPng()
    {
        Assert.AreEqual(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
        using var directory = new TestDirectory();
        var pixels = new byte[] { 0, 0, 255, 255 };
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
        bitmap.Freeze();
        var normalizer = new ImageNormalizer(AppPaths.ForTests(directory.Root).ImagesDirectory);

        var stored = await normalizer.NormalizeBitmapAsync(bitmap);

        Assert.IsTrue(File.Exists(stored.AbsolutePath));
    }

    [STATestMethod]
    public async Task NormalizeBitmap_AllZeroAlphaWithRgbRepairsToOpaque()
    {
        using var directory = new TestDirectory();
        var bitmap = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[] { 30, 20, 10, 0 },
            4);
        bitmap.Freeze();
        var normalizer = new ImageNormalizer(AppPaths.ForTests(directory.Root).ImagesDirectory);

        var stored = await normalizer.NormalizeBitmapAsync(bitmap);

        using var loaded = await Image.LoadAsync<Rgba32>(stored.AbsolutePath);
        Assert.AreEqual(new Rgba32(10, 20, 30, 255), loaded[0, 0]);
    }

    [STATestMethod]
    public async Task NormalizeBitmap_AnyVisibleAlphaPreservesTransparency()
    {
        using var directory = new TestDirectory();
        var bitmap = BitmapSource.Create(
            2,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[]
            {
                30, 20, 10, 0,
                60, 50, 40, 128
            },
            8);
        bitmap.Freeze();
        var normalizer = new ImageNormalizer(AppPaths.ForTests(directory.Root).ImagesDirectory);

        var stored = await normalizer.NormalizeBitmapAsync(bitmap);

        using var loaded = await Image.LoadAsync<Rgba32>(stored.AbsolutePath);
        Assert.AreEqual(new Rgba32(10, 20, 30, 0), loaded[0, 0]);
        Assert.AreEqual(new Rgba32(40, 50, 60, 128), loaded[1, 0]);
    }

    [STATestMethod]
    public async Task NormalizeClipboard_PrefersLargestRecoverableBitmapCandidate()
    {
        using var directory = new TestDirectory();
        var bitmap = BitmapSource.Create(
            2,
            2,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            Enumerable.Repeat(new byte[] { 30, 20, 10, 0 }, 4).SelectMany(pixel => pixel).ToArray(),
            8);
        bitmap.Freeze();
        var encoded = await CreatePngBytesAsync(1, 1, new Rgba32(200, 100, 50, 255));
        var normalizer = new ImageNormalizer(AppPaths.ForTests(directory.Root).ImagesDirectory);

        var stored = await normalizer.NormalizeClipboardAsync(
        [
            ClipboardImageCandidate.FromEncoded("image/png", encoded),
            ClipboardImageCandidate.FromBitmap(bitmap)
        ]);

        using var loaded = await Image.LoadAsync<Rgba32>(stored.AbsolutePath);
        Assert.AreEqual(2, loaded.Width);
        Assert.AreEqual(2, loaded.Height);
        Assert.AreEqual(new Rgba32(10, 20, 30, 255), loaded[0, 0]);
    }

    [STATestMethod]
    public async Task NormalizeClipboard_UsesEncodedCandidateWhenBitmapHasNoPixelData()
    {
        using var directory = new TestDirectory();
        var bitmap = BitmapSource.Create(
            2,
            2,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[16],
            8);
        bitmap.Freeze();
        var encoded = await CreatePngBytesAsync(1, 1, new Rgba32(200, 100, 50, 255));
        var normalizer = new ImageNormalizer(AppPaths.ForTests(directory.Root).ImagesDirectory);

        var stored = await normalizer.NormalizeClipboardAsync(
        [
            ClipboardImageCandidate.FromBitmap(bitmap),
            ClipboardImageCandidate.FromEncoded("image/png", encoded)
        ]);

        using var loaded = await Image.LoadAsync<Rgba32>(stored.AbsolutePath);
        Assert.AreEqual(1, loaded.Width);
        Assert.AreEqual(1, loaded.Height);
        Assert.AreEqual(new Rgba32(200, 100, 50, 255), loaded[0, 0]);
    }

    [TestMethod]
    public async Task RepairStoredImagesOnce_RepairsOnlyInvalidZeroAlphaImagesAndWritesMarker()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        Directory.CreateDirectory(paths.ImagesDirectory);
        var invalidPath = Path.Combine(paths.ImagesDirectory, "invalid.png");
        var transparentPath = Path.Combine(paths.ImagesDirectory, "transparent.png");
        using (var invalid = new Image<Rgba32>(1, 1, new Rgba32(10, 20, 30, 0)))
        {
            await invalid.SaveAsPngAsync(invalidPath);
        }
        using (var transparent = new Image<Rgba32>(1, 1, new Rgba32(40, 50, 60, 128)))
        {
            await transparent.SaveAsPngAsync(transparentPath);
        }
        var normalizer = new ImageNormalizer(paths.ImagesDirectory);

        await normalizer.RepairStoredImagesOnceAsync([invalidPath, transparentPath]);

        using var repaired = await Image.LoadAsync<Rgba32>(invalidPath);
        using var preserved = await Image.LoadAsync<Rgba32>(transparentPath);
        Assert.AreEqual(new Rgba32(10, 20, 30, 255), repaired[0, 0]);
        Assert.AreEqual(new Rgba32(40, 50, 60, 128), preserved[0, 0]);
        Assert.IsTrue(File.Exists(Path.Combine(paths.ImagesDirectory, ".zero-alpha-repair-v1")));
        Assert.IsFalse(Directory.EnumerateFiles(paths.ImagesDirectory, "*.repair.tmp").Any());
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task NormalizeFile_CorruptImageLeavesNoPartialFile()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var source = Path.Combine(directory.Root, "broken.webp");
        await File.WriteAllTextAsync(source, "not an image");
        var normalizer = new ImageNormalizer(paths.ImagesDirectory);

        await Assert.ThrowsExactlyAsync<UnknownImageFormatException>(
            () => normalizer.NormalizeFileAsync(source));

        Assert.IsFalse(Directory.Exists(paths.ImagesDirectory) &&
                       Directory.EnumerateFiles(paths.ImagesDirectory).Any());
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task NormalizeFile_ExistingDestinationIsPreservedAndTemporaryFileIsRemoved()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        Directory.CreateDirectory(paths.ImagesDirectory);
        var source = Path.Combine(directory.Root, "source.png");
        using (var image = new Image<Rgba32>(1, 1, SixLabors.ImageSharp.Color.Red))
        {
            await image.SaveAsPngAsync(source);
        }
        var id = Guid.Parse("00000000-0000-0000-0000-000000000102");
        var destination = Path.Combine(paths.ImagesDirectory, $"{id:N}.png");
        var original = new byte[] { 0x01, 0x02, 0x03 };
        await File.WriteAllBytesAsync(destination, original);
        var normalizer = new ImageNormalizer(paths.ImagesDirectory);

        await Assert.ThrowsExactlyAsync<IOException>(
            () => normalizer.NormalizeFileAsync(source, id));

        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(destination));
        Assert.IsFalse(File.Exists(destination + ".tmp"));
    }

    private static async Task<byte[]> CreatePngBytesAsync(int width, int height, Rgba32 color)
    {
        using var image = new Image<Rgba32>(width, height, color);
        await using var stream = new MemoryStream();
        await image.SaveAsPngAsync(stream);
        return stream.ToArray();
    }
}
