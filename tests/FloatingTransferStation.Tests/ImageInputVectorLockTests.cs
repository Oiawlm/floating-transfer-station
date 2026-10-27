using System.Windows;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 契约 #50（图片输入双层允许表同源、TIFF 永不解码）锁定测试。
/// 双层＝扩展名白名单（ImageFileSupport）＋解码器允许表（ImageInputLimits 的受限
/// Configuration）；两层从同一格式注册表派生——改一层漏一层这里即失败。
/// </summary>
[TestClass]
[TestCategory("Adversarial")]
public sealed class ImageInputVectorLockTests
{
    // ImageSharp 默认配置可解、但产品未登记的一切内置格式（采样自 3.1.12 模块全集，
    // 新版本引入新模块时在此补行——补行行为＝改名即拒绝，与契约同向）。
    private static readonly string[] UnregisteredFormatExtensions = ["tiff", "tga", "pbm", "qoi"];

    [TestMethod]
    public void TiffBytesMasqueradedAsPngAreRejectedByBothLayers()
    {
        using var directory = new TestDirectory();
        var masqueraded = Path.Combine(directory.Root, "probe-masqueraded.png");
        File.Copy(WriteDefaultConfigurationSample(Path.Combine(directory.Root, "probe-real.tiff")), masqueraded);
        var header = File.ReadAllBytes(masqueraded)[..4];
        Assert.IsTrue(
            (header is [0x49, 0x49, 0x2A, 0x00]) || (header is [0x4D, 0x4D, 0x00, 0x2A]),
            "The fixture must carry a real TIFF magic number, not fabricated bytes.");

        // 第一层：扩展名门（.png 在白名单内，内容识别必须仍然失败）。
        Assert.IsFalse(ImageFileSupport.IsSupportedStaticImageFile(masqueraded));

        // 第二层：解码器允许表——受限配置按内容魔数命中即抛，绝不进 TIFF 解码器。
        using (var stream = File.OpenRead(masqueraded))
        {
            Assert.ThrowsExactly<UnknownImageFormatException>(
                () => Image.Identify(ImageInputLimits.Default.IdentifyStaticImage, stream));
        }

        using (var stream = File.OpenRead(masqueraded))
        {
            Assert.ThrowsExactly<UnknownImageFormatException>(
                () => Image.Load(ImageInputLimits.Default.DecodeFirstFrame, stream));
        }

        // 指纹路径同样受限（自管 PNG 之外不可解）。
        Assert.IsNull(CapturedContentFingerprint.ForImageFile(masqueraded));
    }

    [TestMethod]
    public async Task TiffFileAndClipboardVectorsAreRejectedAtDecode()
    {
        using var directory = new TestDirectory();
        var source = WriteDefaultConfigurationSample(Path.Combine(directory.Root, "probe.tif"));
        var normalizer = new ImageNormalizer(AppPaths.ForTests(directory.Root).ImagesDirectory);

        await Assert.ThrowsExactlyAsync<UnknownImageFormatException>(() => normalizer.NormalizeFileAsync(source));

        var candidate = ClipboardImageCandidate.FromEncoded("PNG", await File.ReadAllBytesAsync(source));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => normalizer.NormalizeClipboardAsync([candidate]));
    }

    [TestMethod]
    public void WhitelistExtensionsAndDecoderAllowListShareOneSource()
    {
        // 冻结集：加/删格式是契约 #50 变更，必须显式改格式注册表（两层同变）。
        CollectionAssert.AreEqual(
            new[] { ".bmp", ".gif", ".jpeg", ".jpg", ".png", ".webp" },
            ImageFileSupport.SupportedExtensions.OrderBy(extension => extension, StringComparer.Ordinal).ToArray());

        using var directory = new TestDirectory();
        // 每个白名单扩展名都能经生产门完整识别（扩展名在、解码器缺 → fail）。
        foreach (var extension in ImageFileSupport.SupportedExtensions)
        {
            var path = WriteDefaultConfigurationSample(Path.Combine(directory.Root, $"sample{extension}"));
            Assert.IsTrue(
                ImageFileSupport.IsSupportedStaticImageFile(path),
                $"Whitelisted extension {extension} must stay decodable through the restricted configuration.");
        }

        // 反向：未登记格式即使改名白名单扩展名也不可解码（解码器在、扩展名缺 → fail）。
        foreach (var extension in UnregisteredFormatExtensions)
        {
            var sample = WriteDefaultConfigurationSample(Path.Combine(directory.Root, $"sample.{extension}"));
            var masqueraded = Path.Combine(directory.Root, $"masqueraded-{extension}.png");
            File.Copy(sample, masqueraded);
            Assert.IsFalse(
                ImageFileSupport.IsSupportedStaticImageFile(masqueraded),
                $"Unregistered format {extension} must not decode under a whitelisted extension.");
            using var stream = File.OpenRead(masqueraded);
            Assert.ThrowsExactly<UnknownImageFormatException>(
                () => Image.Identify(ImageInputLimits.Default.IdentifyStaticImage, stream));
        }
    }

    [STATestMethod]
    [DataRow(".tif")]
    [DataRow(".tiff")]
    public void TiffFileDropRoutesToRawFileListNotImages(string extension)
    {
        using var directory = new TestDirectory();
        var path = WriteDefaultConfigurationSample(Path.Combine(directory.Root, $"static{extension}"));
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { path });
        data.SetData(DataFormats.UnicodeText, "must not fall back");
        var reader = new ExternalDropPayloadReader();

        var payload = reader.Read(data);

        Assert.IsInstanceOfType<ExternalDropPayload.FileList>(payload);
        CollectionAssert.AreEqual(
            new[] { path },
            ((ExternalDropPayload.FileList)payload!).Paths.ToArray());
    }

    // 测试侧造样专用：默认配置按扩展名编码（生产解码入口绝不使用默认配置）。
    private static string WriteDefaultConfigurationSample(string path)
    {
        using var image = new Image<Rgba32>(2, 2, Color.Red);
        image.Save(path);
        return path;
    }
}
