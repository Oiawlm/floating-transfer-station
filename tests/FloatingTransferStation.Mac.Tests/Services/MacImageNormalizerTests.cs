using FloatingTransferStation.Mac.Services;
using FloatingTransferStation.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace FloatingTransferStation.Mac.Tests.Services;

[TestClass]
public sealed class MacImageNormalizerTests
{
    [TestMethod]
    public async Task Candidates_DamagedLargestFallsBackToLargestDecodableRepresentation()
    {
        using var directory = new ServiceTestDirectory();
        var damaged = (await PngAsync(8, 8, Color.Red))[..33];
        var best = await PngAsync(3, 2, Color.Blue);
        var small = await PngAsync(1, 1, Color.Green);
        Assert.AreEqual(8, Image.Identify(damaged).Width);

        var stored = await new MacImageNormalizer(directory.Images).NormalizeCandidatesAsync([small, damaged, best]);

        using var actual = await Image.LoadAsync<Rgba32>(stored.AbsolutePath);
        Assert.AreEqual(3, actual.Width);
        Assert.AreEqual(2, actual.Height);
        Assert.AreEqual(Color.Blue.ToPixel<Rgba32>(), actual[0, 0]);
        Assert.AreEqual(1, Directory.GetFiles(directory.Images).Length);
    }

    [TestMethod]
    public async Task Candidates_ApplyMirroredExif()
    {
        using var directory = new ServiceTestDirectory();
        using var source = new Image<Rgba32>(2, 1);
        source[0, 0] = Color.Red.ToPixel<Rgba32>();
        source[1, 0] = Color.Blue.ToPixel<Rgba32>();
        source.Metadata.ExifProfile = new ExifProfile();
        source.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)2);
        using var buffer = new MemoryStream();
        await source.SaveAsPngAsync(buffer);

        var stored = await new MacImageNormalizer(directory.Images).NormalizeCandidatesAsync([buffer.ToArray()]);

        using var actual = await Image.LoadAsync<Rgba32>(stored.AbsolutePath);
        Assert.AreEqual(Color.Blue.ToPixel<Rgba32>(), actual[0, 0]);
        Assert.AreEqual(Color.Red.ToPixel<Rgba32>(), actual[1, 0]);
    }

    [TestMethod]
    public async Task Files_RejectAnimationWhileClipboardKeepsFirstFrame()
    {
        using var directory = new ServiceTestDirectory();
        using var source = new Image<Rgba32>(2, 2, Color.Red);
        using var second = new Image<Rgba32>(2, 2, Color.Blue);
        source.Frames.AddFrame(second.Frames.RootFrame);
        var path = Path.Combine(directory.Root, "animation.gif");
        await source.SaveAsGifAsync(path);
        var normalizer = new MacImageNormalizer(directory.Images);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => normalizer.NormalizeStaticFileAsync(path));
        var stored = await normalizer.NormalizeCandidatesAsync([await File.ReadAllBytesAsync(path)]);

        using var actual = await Image.LoadAsync<Rgba32>(stored.AbsolutePath);
        Assert.AreEqual(1, actual.Frames.Count);
        Assert.AreEqual(Color.Red.ToPixel<Rgba32>(), actual[0, 0]);
    }

    [TestMethod]
    public async Task OversizedPixelCandidate_FallsBackWithoutResizing()
    {
        using var directory = new ServiceTestDirectory();
        var large = await PngAsync(8, 8, Color.Red);
        var small = await PngAsync(2, 2, Color.Blue);
        var normalizer = new MacImageNormalizer(directory.Images, new ImageInputLimits(MaxPixels: 16));

        var stored = await normalizer.NormalizeCandidatesAsync([large, small]);
        using var actual = await Image.LoadAsync(stored.AbsolutePath);
        Assert.AreEqual(2, actual.Width);
        await Assert.ThrowsExactlyAsync<ImageInputLimitException>(() => normalizer.NormalizeCandidatesAsync([large]));
    }

    [TestMethod]
    public async Task EncodedLimitAndDecodeFailure_LeaveNoTemporaryFiles()
    {
        using var directory = new ServiceTestDirectory();
        var normalizer = new MacImageNormalizer(directory.Images, new ImageInputLimits(MaxEncodedBytes: 8));
        await Assert.ThrowsExactlyAsync<ImageInputLimitException>(() => normalizer.NormalizeCandidatesAsync([new byte[9]]));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => normalizer.NormalizeCandidatesAsync([new byte[3]]));
        Assert.IsFalse(Directory.Exists(directory.Images));
    }

    internal static async Task<byte[]> PngAsync(int width, int height, Color color)
    {
        using var image = new Image<Rgba32>(width, height, color);
        using var buffer = new MemoryStream();
        await image.SaveAsPngAsync(buffer);
        return buffer.ToArray();
    }
}

internal sealed class ServiceTestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "fts-mac-tests", Guid.NewGuid().ToString("N"));
    public string Images => Path.Combine(Root, "images");
    public ServiceTestDirectory() => Directory.CreateDirectory(Root);
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
