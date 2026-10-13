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
/// 线程设置冻结位图。解码尺寸以显示高为锚（卡片模板恒 120 DIP 高、Uniform
/// 显示高不超过该值）：解码高 = ceil(显示高 × DPI)，宽随文件纵横比得出，
/// 与面板宽无关——一次解码到位，不存在按宽取桶的「基线先行＋布局后升档」
/// 二次解码。新容器绑定当刻显示高未知（模板属性未及生效）时推迟到首次布局
/// 携带真实显示高再解码，不按猜测尺寸提前解码；跨屏 DPI 升高时按解码高
/// 增大重解一次，降档沿用已解码位图由 GPU 缩放。解码失败或文件缺失保持
/// 占位；快速切换绑定路径时只应用最新一代结果。
/// </summary>
public sealed class AsyncThumbnailImage : Image
{
    /// <summary>解码并发上限：无限并发的裸解码会打满线程池与内存带宽，反而拖慢 UI 续延调度。</summary>
    private static readonly SemaphoreSlim DecodeLimiter = new(
        Math.Clamp(Environment.ProcessorCount / 2, 2, 6));

    private const int MinDecodeHeight = 64;
    private const int MaxDecodeHeight = 2048;

    internal static readonly ImageThumbnailConverter SharedConverter = new();

    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath),
        typeof(string),
        typeof(AsyncThumbnailImage),
        new PropertyMetadata(string.Empty, OnSourceChanged));

    public static readonly DependencyProperty DecodeHeightProperty = DependencyProperty.Register(
        nameof(DecodeHeight),
        typeof(int),
        typeof(AsyncThumbnailImage),
        new PropertyMetadata(0, OnSourceChanged));

    private int _decodeGeneration;
    private int _appliedDecodeHeight;

    public string SourcePath
    {
        get => (string)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    /// <summary>显式解码高（测试与特殊场景）；默认 0 表示按显示尺寸自动取高。</summary>
    public int DecodeHeight
    {
        get => (int)GetValue(DecodeHeightProperty);
        set => SetValue(DecodeHeightProperty, value);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((AsyncThumbnailImage)d).RefreshThumbnail();

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // 新容器的绑定可能先于模板 Height 属性生效（彼时显示高未知、解码被
        // 推迟）：显示高一旦到位立即补上唯一一次解码，不依赖元素被排列。
        if (e.Property == HeightProperty &&
            (double.IsNaN((double)e.OldValue) || (double)e.OldValue <= 0) &&
            EffectiveDecodeHeight() > 0)
        {
            RefreshThumbnail();
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.NewSize.Height > 0 && EffectiveDecodeHeight() > _appliedDecodeHeight)
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

        var decodeHeight = EffectiveDecodeHeight();
        if (decodeHeight <= 0)
        {
            // 显示高未知（模板属性未及生效的新容器）：先清占位，等待首次布局的
            // OnRenderSizeChanged 携带真实显示高触发唯一一次解码，不按猜测尺寸
            // 提前解码（那是「先猜后补」双解码的来源）。
            _appliedDecodeHeight = 0;
            Source = null;
            return;
        }

        _appliedDecodeHeight = decodeHeight;
        if (SharedConverter.TryGetCachedThumbnail(path, decodeHeight, out var cached))
        {
            Source = cached;
            return;
        }

        // 未命中：立即清空占位，避免显示上一张的残留；解码完成后回到 UI 线程设置。
        Source = null;
        _ = DecodeAsync(path, decodeHeight, generation);
    }

    private async Task DecodeAsync(string path, int decodeHeight, int generation)
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
                decodeHeight,
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

    private int EffectiveDecodeHeight()
    {
        if (DecodeHeight > 0)
        {
            return Math.Clamp(DecodeHeight, MinDecodeHeight, MaxDecodeHeight);
        }

        var displayHeight = Height;
        if (double.IsNaN(displayHeight) || displayHeight <= 0)
        {
            displayHeight = ActualHeight;
        }

        if (displayHeight <= 0)
        {
            // 显示高未知：交由调用方推迟到首次布局后解码。
            return 0;
        }

        var pixels = (int)Math.Ceiling(displayHeight * Math.Max(1d, VisualTreeHelper.GetDpi(this).PixelsPerDip));
        return Math.Clamp(pixels, MinDecodeHeight, MaxDecodeHeight);
    }
}
