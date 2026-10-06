using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatingTransferStation.Controls;
using FloatingTransferStation.Converters;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class AsyncThumbnailImageTests
{
    [STATestMethod]
    public void TryGetCachedThumbnail_MissBeforeDecode_HitAfterConvert_BackgroundRevalidatesChanges()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "cache-query.png");
        WritePng(path, width: 1200, height: 600);
        var converter = new ImageThumbnailConverter();

        Assert.IsFalse(converter.TryGetCachedThumbnail(path, 512, out var miss));
        Assert.IsNull(miss);

        var decoded = (BitmapSource?)converter.Convert(
            path,
            typeof(BitmapSource),
            512,
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsNotNull(decoded);

        // 命中是纯内存查询：即使文件被外部改写，也返回缓存位图（受管目录内
        // 的重写只应来自本应用并伴随 Invalidate）。
        Assert.IsTrue(converter.TryGetCachedThumbnail(path, 512, out var hit));
        Assert.AreSame(decoded, hit);
        Assert.IsTrue(hit!.IsFrozen);

        var timestamp = File.GetLastWriteTimeUtc(path);
        WritePng(path, width: 600, height: 1200);
        File.SetLastWriteTimeUtc(path, timestamp.AddMinutes(1));
        Assert.IsTrue(
            converter.TryGetCachedThumbnail(path, 512, out _),
            "外部改写不构成 UI 线程同步失效；后台解码路径负责兜底校验。");

        var refreshed = (BitmapSource?)converter.Convert(
            path,
            typeof(BitmapSource),
            512,
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsNotNull(refreshed);
        Assert.AreNotSame(decoded, refreshed, "后台路径发现文件变化后必须重新解码。");
        Assert.IsTrue(converter.TryGetCachedThumbnail(path, 512, out var updated));
        Assert.AreSame(refreshed, updated);
    }

    [STATestMethod]
    public void TryGetCachedThumbnail_MissingFileKeptInMemoryUntilBackgroundEviction()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "gone.png");
        WritePng(path, width: 64, height: 32);
        var converter = new ImageThumbnailConverter();
        Assert.IsNotNull(converter.Convert(
            path,
            typeof(BitmapSource),
            512,
            System.Globalization.CultureInfo.InvariantCulture));

        File.Delete(path);

        // 文件缺失同样先保留内存命中（条目移出面板与文件删除在本应用内同步发生）；
        // 后台解码路径发现缺失时逐出并返回 null。
        Assert.IsTrue(converter.TryGetCachedThumbnail(path, 512, out _));
        Assert.IsNull(converter.Convert(
            path,
            typeof(BitmapSource),
            512,
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsFalse(converter.TryGetCachedThumbnail(path, 512, out var image));
        Assert.IsNull(image);
    }

    [STATestMethod]
    public void TryGetCachedThumbnail_InvalidPathsReturnFalse()
    {
        var converter = new ImageThumbnailConverter();
        Assert.IsFalse(converter.TryGetCachedThumbnail(string.Empty, 512, out _));
        Assert.IsFalse(converter.TryGetCachedThumbnail("   ", 512, out _));
        Assert.IsFalse(converter.TryGetCachedThumbnail("relative.png", 512, out _));
    }

    [STATestMethod]
    public void Invalidate_EvictsAllDecodeWidthsForThePath()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "invalidated.png");
        WritePng(path, width: 1200, height: 600);
        var converter = new ImageThumbnailConverter();
        Assert.IsNotNull(converter.Convert(
            path, typeof(BitmapSource), 256, System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsNotNull(converter.Convert(
            path, typeof(BitmapSource), 512, System.Globalization.CultureInfo.InvariantCulture));

        converter.Invalidate(path);

        Assert.IsFalse(converter.TryGetCachedThumbnail(path, 256, out _));
        Assert.IsFalse(converter.TryGetCachedThumbnail(path, 512, out _));
    }

    [STATestMethod]
    public void CacheHit_SetsFrozenSourceSynchronously()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "warm.png");
        WritePng(path, width: 1200, height: 600);
        var warmed = (BitmapSource?)AsyncThumbnailImage.SharedConverter.Convert(
            path,
            typeof(BitmapSource),
            512,
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsNotNull(warmed);

        var control = new AsyncThumbnailImage { SourcePath = path, DecodeWidth = 512 };

        Assert.AreSame(warmed, control.Source);
        Assert.IsTrue(((BitmapSource)control.Source!).IsFrozen);
    }

    [STATestMethod]
    public void CacheMiss_ClearsPlaceholderThenSetsFrozenSourceFromBackground()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "cold.png");
        WritePng(path, width: 1200, height: 600);
        var control = new AsyncThumbnailImage { DecodeWidth = 512 };
        control.SourcePath = path;

        Assert.IsNull(control.Source, "未命中缓存时应先显示占位而不是旧图。");
        PumpUntil(() => control.Source is not null, control.Dispatcher);

        var source = (BitmapSource?)control.Source;
        Assert.IsNotNull(source);
        Assert.IsTrue(source.IsFrozen);
        Assert.AreEqual(512, source.PixelWidth);
        Assert.AreEqual(256, source.PixelHeight);
    }

    [STATestMethod]
    public void BucketUp_RoundsPixelsUpToHundredTwentyEightSteps()
    {
        Assert.AreEqual(128, AsyncThumbnailImage.BucketUp(1));
        Assert.AreEqual(128, AsyncThumbnailImage.BucketUp(128));
        Assert.AreEqual(256, AsyncThumbnailImage.BucketUp(129));
        Assert.AreEqual(896, AsyncThumbnailImage.BucketUp(775));
    }

    [STATestMethod]
    public void RapidPathChanges_OnlyLatestDecodedImageIsApplied()
    {
        using var directory = new TestDirectory();
        var wide = Path.Combine(directory.Root, "wide.png");
        var tall = Path.Combine(directory.Root, "tall.png");
        WritePng(wide, width: 1200, height: 600);
        WritePng(tall, width: 600, height: 1200);
        var control = new AsyncThumbnailImage { DecodeWidth = 512 };

        control.SourcePath = wide;
        control.SourcePath = tall;

        PumpUntil(() => control.Source is not null, control.Dispatcher);

        var source = (BitmapSource?)control.Source;
        Assert.IsNotNull(source);
        Assert.AreEqual(512, source.PixelWidth);
        Assert.AreEqual(1024, source.PixelHeight, "只应应用最新路径(高图)的解码结果。");
    }

    [STATestMethod]
    public void MissingFile_KeepsPlaceholderWithoutCrashing()
    {
        using var directory = new TestDirectory();
        var control = new AsyncThumbnailImage
        {
            SourcePath = Path.Combine(directory.Root, "missing.png")
        };

        Assert.IsNull(control.Source);
        PumpUntil(() => false, control.Dispatcher, maxWait: TimeSpan.FromMilliseconds(300));
        Assert.IsNull(control.Source, "文件缺失时保持占位,不抛异常。");
    }

    /// <summary>同步泵调度器直到条件成立(5 秒上限);后台解码的续延经 BeginInvoke 回到本线程。</summary>
    private static void PumpUntil(
        Func<bool> condition,
        Dispatcher dispatcher,
        TimeSpan? maxWait = null)
    {
        var deadline = DateTime.UtcNow + (maxWait ?? TimeSpan.FromSeconds(5));
        while (!condition() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    private static void WritePng(string path, int width, int height)
    {
        const int bytesPerPixel = 4;
        var stride = width * bytesPerPixel;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, (byte)0x7F);
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
            width,
            height,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
