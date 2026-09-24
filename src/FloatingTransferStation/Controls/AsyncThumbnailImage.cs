using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Converters;

namespace FloatingTransferStation.Controls;

/// <summary>
/// 卡片缩略图控件：缓存命中时同步设置已解码缩略图（无 UI 线程解码开销）；
/// 未命中时先清空占位，在与 <see cref="ImageThumbnailConverter"/> 共享的缓存上
/// 后台解码，完成后回到 UI 线程设置冻结位图。解码失败或文件缺失保持占位。
/// 快速切换绑定路径时只应用最新一代结果。
/// </summary>
public sealed class AsyncThumbnailImage : Image
{
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
        new PropertyMetadata(512, OnSourceChanged));

    private int _decodeGeneration;

    public string SourcePath
    {
        get => (string)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public int DecodeWidth
    {
        get => (int)GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((AsyncThumbnailImage)d).RefreshThumbnail();

    private void RefreshThumbnail()
    {
        var path = SourcePath;
        var generation = ++_decodeGeneration;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            Source = null;
            return;
        }

        if (SharedConverter.TryGetCachedThumbnail(path, DecodeWidth, out var cached))
        {
            Source = cached;
            return;
        }

        // 未命中：立即清空占位，避免显示上一张的残留；解码完成后回到 UI 线程设置。
        Source = null;
        var dispatcher = Dispatcher;
        var decodeWidth = Math.Clamp(DecodeWidth, 1, int.MaxValue);
        _ = Task.Run(() =>
        {
            var decoded = SharedConverter.Convert(
                path,
                typeof(BitmapSource),
                decodeWidth,
                CultureInfo.InvariantCulture) as BitmapSource;
            dispatcher.BeginInvoke(() =>
            {
                if (generation != _decodeGeneration || SourcePath != path)
                {
                    return;
                }

                Source = decoded;
            });
        });
    }
}
