using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatingTransferStation.Converters;

namespace FloatingTransferStation.Controls;

/// <summary>
/// 卡片缩略图控件。缓存命中时同步设置已解码缩略图——命中判定是纯内存查询，
/// 不做磁盘 stat；未命中时先清空占位，经并发限流后台解码，完成后回到 UI
/// 线程设置冻结位图。解码宽度取「基线宽」与「显示宽度×DPI 的 128px 上取桶」
/// 的较大者：常规面板直接按基线解码（单一缓存键，回收复用不重解），高 DPI
/// 或宽面板在布局后按更高桶补一次清晰度；降桶沿用已解码位图由 GPU 缩放，
/// 桶边界振荡不会触发重复解码。解码失败或文件缺失保持占位；快速切换绑定
/// 路径时只应用最新一代结果。
/// </summary>
public sealed class AsyncThumbnailImage : Image
{
    /// <summary>解码并发上限：无限并发的裸解码会打满线程池与内存带宽，反而拖慢 UI 续延调度。</summary>
    private static readonly SemaphoreSlim DecodeLimiter = new(
        Math.Clamp(Environment.ProcessorCount / 2, 2, 6));

    private const int BaselineDecodeWidth = 512;
    private const int DecodeWidthStep = 128;
    private const int MaxDisplayDecodeWidth = 1024;

    internal static readonly ImageThumbnailConverter SharedConverter = new();

    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath),
        typeof(string),
        typeof(AsyncThumbnailImage),
        new PropertyMetadata(string.Empty, OnSourceChanged));

    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.Register(
        nameof(DecodeWidth),
        typeof(int),
        typeof(AsyncThumbnailImage),
        new PropertyMetadata(0, OnSourceChanged));

    private int _decodeGeneration;
    private int _appliedDecodeWidth;

    public string SourcePath
    {
        get => (string)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    /// <summary>显式解码宽度（测试与特殊场景）；默认 0 表示按显示尺寸自动取桶。</summary>
    public int DecodeWidth
    {
        get => (int)GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((AsyncThumbnailImage)d).RefreshThumbnail();

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.NewSize.Width > 0 && EffectiveDecodeWidth() > _appliedDecodeWidth)
        {
            RefreshThumbnail();
        }
    }

    private void RefreshThumbnail()
    {
        var path = SourcePath;
        var generation = ++_decodeGeneration;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            Source = null;
            return;
        }

        var decodeWidth = EffectiveDecodeWidth();
        _appliedDecodeWidth = decodeWidth;
        if (SharedConverter.TryGetCachedThumbnail(path, decodeWidth, out var cached))
        {
            Source = cached;
            return;
        }

        // 未命中：立即清空占位，避免显示上一张的残留；解码完成后回到 UI 线程设置。
        Source = null;
        _ = DecodeAsync(path, decodeWidth, generation);
    }

    private async Task DecodeAsync(string path, int decodeWidth, int generation)
    {
        // 先异步取解码额度再排队：排队中的等待者不占用线程池线程。
        await DecodeLimiter.WaitAsync();
        try
        {
            // 排队期间绑定路径可能已被回收复用换掉，过期请求不必占用解码带宽。
            if (generation != _decodeGeneration || SourcePath != path)
            {
                return;
            }

            var decoded = await Task.Run(() => SharedConverter.Convert(
                path,
                typeof(BitmapSource),
                decodeWidth,
                CultureInfo.InvariantCulture) as BitmapSource);
            await Dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                () =>
                {
                    if (generation != _decodeGeneration || SourcePath != path)
                    {
                        return;
                    }

                    Source = decoded;
                });
        }
        finally
        {
            DecodeLimiter.Release();
        }
    }

    private int EffectiveDecodeWidth()
    {
        if (DecodeWidth > 0)
        {
            return DecodeWidth;
        }

        if (ActualWidth <= 0)
        {
            return BaselineDecodeWidth;
        }

        var pixels = (int)Math.Ceiling(ActualWidth * Math.Max(1d, VisualTreeHelper.GetDpi(this).PixelsPerDip));
        return Math.Clamp(BucketUp(pixels), BaselineDecodeWidth, MaxDisplayDecodeWidth);
    }

    internal static int BucketUp(int pixels) =>
        ((pixels + DecodeWidthStep - 1) / DecodeWidthStep) * DecodeWidthStep;
}
