using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class ImageInputLimitsTests
{
    [STATestMethod]
    public void ReadCandidates_OverByteBudgetFallsBackToAnotherRepresentation()
    {
        var data = new DataObject();
        data.SetData("PNG", new byte[] { 1, 2, 3 });
        data.SetData("JFIF", new byte[] { 4, 5 });

        var candidates = new WindowsDataImageReader(new ImageInputLimits(MaxEncodedBytes: 2)).ReadCandidates(data);

        Assert.AreEqual("JFIF", candidates.Single().Format);
    }

    [STATestMethod]
    public void ReadCandidates_AllRepresentationsOverBudgetReportsLimitFailure()
    {
        var data = new DataObject();
        data.SetData("PNG", new byte[] { 1, 2, 3 });

        Assert.ThrowsExactly<ImageInputLimitException>(() =>
            new WindowsDataImageReader(new ImageInputLimits(MaxEncodedBytes: 2)).ReadCandidates(data));
    }

    [STATestMethod]
    public void ReadCandidates_OverBudgetSeekableStreamIsNotReadAndPositionIsUnchanged()
    {
        using var source = new CountingInputStream(100, canSeek: true) { Position = 7 };
        var data = new DataObject();
        data.SetData("PNG", source);
        data.SetData("JFIF", new byte[] { 4 });

        var candidates = new WindowsDataImageReader(new ImageInputLimits(MaxEncodedBytes: 4)).ReadCandidates(data);

        Assert.AreEqual("JFIF", candidates.Single().Format);
        Assert.AreEqual(0, source.BytesRead);
        Assert.AreEqual(7L, source.Position);
    }

    [STATestMethod]
    public void ReadCandidates_NonSeekableStreamStopsAfterBudgetProbeAndFallsBack()
    {
        using var source = new CountingInputStream(100, canSeek: false);
        var data = new DataObject();
        data.SetData("PNG", source);
        data.SetData("JFIF", new byte[] { 4 });

        var candidates = new WindowsDataImageReader(new ImageInputLimits(MaxEncodedBytes: 4)).ReadCandidates(data);

        Assert.AreEqual("JFIF", candidates.Single().Format);
        Assert.IsLessThanOrEqualTo(5, source.BytesRead);
    }

    [STATestMethod]
    public void ReadCandidates_OverPixelBudgetBitmapKeepsEncodedFallback()
    {
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        var data = new DataObject();
        data.SetData(DataFormats.Bitmap, bitmap);
        data.SetData("PNG", new byte[] { 1 });

        var candidates = new WindowsDataImageReader(new ImageInputLimits(MaxPixels: 3)).ReadCandidates(data);

        Assert.IsFalse(candidates.Single().IsBitmap);
    }

    [TestMethod]
    public async Task NormalizeFile_RejectsPixelBudgetBeforeDecodingDamagedPixels()
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Root, "oversized.png");
        await File.WriteAllBytesAsync(source, (await PngBytes(3, 3))[..33]);
        var paths = AppPaths.ForTests(directory.Root);
        var normalizer = new ImageNormalizer(paths.ImagesDirectory, new ImageInputLimits(MaxPixels: 4));

        await Assert.ThrowsExactlyAsync<ImageInputLimitException>(() => normalizer.NormalizeFileAsync(source));

        AssertNoImages(paths.ImagesDirectory);
    }

    [TestMethod]
    public async Task NormalizeFile_RejectsEncodedByteBudgetWithoutWriting()
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Root, "source.png");
        await File.WriteAllBytesAsync(source, await PngBytes(2, 2));
        var paths = AppPaths.ForTests(directory.Root);
        var normalizer = new ImageNormalizer(paths.ImagesDirectory, new ImageInputLimits(MaxEncodedBytes: 16));

        await Assert.ThrowsExactlyAsync<ImageInputLimitException>(() => normalizer.NormalizeFileAsync(source));

        AssertNoImages(paths.ImagesDirectory);
    }

    [TestMethod]
    public async Task NormalizeClipboard_SkipsOverPixelBudgetAndPreservesFallbackResolution()
    {
        using var directory = new TestDirectory();
        var candidates = new[]
        {
            ClipboardImageCandidate.FromEncoded("PNG", await PngBytes(3, 3)),
            ClipboardImageCandidate.FromEncoded("image/png", await PngBytes(2, 2))
        };
        var normalizer = new ImageNormalizer(AppPaths.ForTests(directory.Root).ImagesDirectory, new ImageInputLimits(MaxPixels: 4));

        var stored = await normalizer.NormalizeClipboardAsync(candidates);

        using var loaded = await Image.LoadAsync(stored.AbsolutePath);
        Assert.AreEqual(2, loaded.Width);
        Assert.AreEqual(2, loaded.Height);
    }

    [TestMethod]
    public async Task NormalizeClipboard_SkipsOverByteBudgetAndAcceptsExactBoundary()
    {
        using var directory = new TestDirectory();
        var png = await PngBytes(2, 2);
        var candidates = new[]
        {
            ClipboardImageCandidate.FromEncoded("PNG", (await PngBytes(3, 3)).Concat(new byte[100]).ToArray()),
            ClipboardImageCandidate.FromEncoded("image/png", png)
        };
        var normalizer = new ImageNormalizer(AppPaths.ForTests(directory.Root).ImagesDirectory, new ImageInputLimits(MaxEncodedBytes: png.Length));

        var stored = await normalizer.NormalizeClipboardAsync(candidates);

        using var loaded = await Image.LoadAsync(stored.AbsolutePath);
        Assert.AreEqual(2, loaded.Width);
        Assert.AreEqual(2, loaded.Height);
    }

    [TestMethod]
    public async Task NormalizeClipboard_AllCandidatesOverBudgetReportsLimitFailure()
    {
        using var directory = new TestDirectory();
        var normalizer = new ImageNormalizer(AppPaths.ForTests(directory.Root).ImagesDirectory, new ImageInputLimits(MaxPixels: 3));
        var candidate = ClipboardImageCandidate.FromEncoded("PNG", await PngBytes(2, 2));

        await Assert.ThrowsExactlyAsync<ImageInputLimitException>(() => normalizer.NormalizeClipboardAsync([candidate]));
    }

    [TestMethod]
    public async Task NormalizeFile_ReportsDecodedAllocationBudgetBeforeWriting()
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Root, "source.png");
        await File.WriteAllBytesAsync(source, await PngBytes(600, 600));
        var normalizer = new ImageNormalizer(AppPaths.ForTests(directory.Root).ImagesDirectory,
            new ImageInputLimits(MaxDecodedBufferMegabytes: 1));

        await Assert.ThrowsExactlyAsync<ImageInputLimitException>(() => normalizer.NormalizeFileAsync(source));
    }

    [TestMethod]
    public async Task NormalizeClipboard_DecodedAllocationBudgetFallsBackToSmallerCandidate()
    {
        using var directory = new TestDirectory();
        var normalizer = new ImageNormalizer(AppPaths.ForTests(directory.Root).ImagesDirectory,
            new ImageInputLimits(MaxDecodedBufferMegabytes: 1));
        var large = ClipboardImageCandidate.FromEncoded("PNG", await PngBytes(600, 600));
        var small = ClipboardImageCandidate.FromEncoded("image/png", await PngBytes(2, 2));

        var stored = await normalizer.NormalizeClipboardAsync([large, small]);

        using var loaded = await Image.LoadAsync(stored.AbsolutePath);
        Assert.AreEqual(2, loaded.Width);
        Assert.AreEqual(2, loaded.Height);
    }

    [STATestMethod]
    public async Task NormalizeBitmap_RejectsPixelBudgetBeforeWriting()
    {
        using var directory = new TestDirectory();
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        var paths = AppPaths.ForTests(directory.Root);
        var normalizer = new ImageNormalizer(paths.ImagesDirectory, new ImageInputLimits(MaxPixels: 3));

        await Assert.ThrowsExactlyAsync<ImageInputLimitException>(() => normalizer.NormalizeBitmapAsync(bitmap));

        AssertNoImages(paths.ImagesDirectory);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NormalizeAnimatedInput_DoesNotDecodeCorruptLaterFrame(bool clipboard)
    {
        using var directory = new TestDirectory();
        using var firstFrame = new Image<Rgba32>(2, 2, SixLabors.ImageSharp.Color.Red);
        using var gif = new MemoryStream();
        await firstFrame.SaveAsGifAsync(gif);
        // The next descriptor has a zero width, so decoding it must fail.
        byte[] damagedFrame = [0x2C, 0, 0, 0, 0, 0, 0, 2, 0, 0];
        var bytes = gif.ToArray()[..^1].Concat(damagedFrame).ToArray();
        Assert.ThrowsExactly<InvalidImageContentException>(() => Image.Load(bytes));
        var normalizer = new ImageNormalizer(AppPaths.ForTests(directory.Root).ImagesDirectory);
        StoredImage stored;
        if (clipboard)
        {
            stored = await normalizer.NormalizeClipboardAsync([ClipboardImageCandidate.FromEncoded("PNG", bytes)]);
        }
        else
        {
            var source = Path.Combine(directory.Root, "animated.gif");
            await File.WriteAllBytesAsync(source, bytes);
            stored = await normalizer.NormalizeFileAsync(source);
        }

        using var loaded = await Image.LoadAsync<Rgba32>(stored.AbsolutePath);
        Assert.AreEqual(1, loaded.Frames.Count);
        Assert.AreEqual(new Rgba32(255, 0, 0, 255), loaded[0, 0]);
    }

    [TestMethod]
    public async Task StaticImagePreflightRejectsByteAndPixelBudgets()
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Root, "source.png");
        var bytes = await PngBytes(2, 2);
        await File.WriteAllBytesAsync(source, bytes);

        Assert.IsFalse(ImageFileSupport.IsSupportedStaticImageFile(source, new ImageInputLimits(MaxPixels: 3)));
        Assert.IsFalse(ImageFileSupport.IsSupportedStaticImageFile(source, new ImageInputLimits(MaxEncodedBytes: bytes.Length - 1)));
        Assert.IsTrue(ImageFileSupport.IsSupportedStaticImageFile(source, new ImageInputLimits(MaxEncodedBytes: bytes.Length, MaxPixels: 4)));
    }

    [TestMethod]
    [DataRow("gif")]
    [DataRow("png")]
    [DataRow("webp")]
    [DataRow("tiff")]
    public async Task NormalizeStaticFile_RejectsAnimationBeforeSingleFrameDecode(string extension)
    {
        using var directory = new TestDirectory();
        var source = Path.Combine(directory.Root, $"animated.{extension}");
        using (var image = new Image<Rgba32>(2, 2, SixLabors.ImageSharp.Color.Red))
        {
            image.Frames.AddFrame(image.Frames.RootFrame);
            await image.SaveAsync(source);
        }
        var paths = AppPaths.ForTests(directory.Root);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new ImageNormalizer(paths.ImagesDirectory).NormalizeStaticFileAsync(source));

        AssertNoImages(paths.ImagesDirectory);
    }

    [STATestMethod]
    public void ExternalDrop_AllImagesOverBudgetRejectsWithoutTextFallback()
    {
        var data = new DataObject();
        data.SetData("PNG", new byte[] { 1, 2, 3 });
        data.SetText("must not become fallback content");
        var reader = new ExternalDropPayloadReader(new WindowsDataImageReader(new ImageInputLimits(MaxEncodedBytes: 2)));

        Assert.IsNull(reader.Read(data));
    }

    private static async Task<byte[]> PngBytes(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, SixLabors.ImageSharp.Color.Red);
        using var stream = new MemoryStream();
        await image.SaveAsPngAsync(stream);
        return stream.ToArray();
    }

    private static void AssertNoImages(string directory) =>
        Assert.IsFalse(Directory.Exists(directory) && Directory.EnumerateFiles(directory).Any());

    private sealed class CountingInputStream(int length, bool canSeek) : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => canSeek;
        public override bool CanWrite => false;
        public override long Length => canSeek ? length : throw new NotSupportedException();
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(length - Position, count);
            Array.Fill(buffer, (byte)1, offset, read);
            Position += read;
            BytesRead += read;
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
