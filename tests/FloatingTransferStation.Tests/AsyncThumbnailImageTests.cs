using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatingTransferStation.Controls;
using FloatingTransferStation.Converters;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class AsyncThumbnailImageTests
{
    [STATestMethod]
    public void TryGetCachedThumbnail_MissBeforeDecode_HitAfterConvert_StaleOnChange()
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

        Assert.IsTrue(converter.TryGetCachedThumbnail(path, 512, out var hit));
        Assert.AreSame(decoded, hit);
        Assert.IsTrue(hit!.IsFrozen);

        // 文件变化后缓存条目失效,必须回落到未命中,不得返回旧图。
        var timestamp = File.GetLastWriteTimeUtc(path);
        WritePng(path, width: 600, height: 1200);
        File.SetLastWriteTimeUtc(path, timestamp.AddMinutes(1));
        Assert.IsFalse(converter.TryGetCachedThumbnail(path, 512, out var stale));
        Assert.IsNull(stale);
    }

    [STATestMethod]
    public void TryGetCachedThumbnail_MissingFileReturnsFalseAndEvictsEntry()
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
        var control = new AsyncThumbnailImage();
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
    public void RapidPathChanges_OnlyLatestDecodedImageIsApplied()
    {
        using var directory = new TestDirectory();
        var wide = Path.Combine(directory.Root, "wide.png");
        var tall = Path.Combine(directory.Root, "tall.png");
        WritePng(wide, width: 1200, height: 600);
        WritePng(tall, width: 600, height: 1200);
        var control = new AsyncThumbnailImage();

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
