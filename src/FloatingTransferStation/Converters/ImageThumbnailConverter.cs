using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace FloatingTransferStation.Converters;

public sealed class ImageThumbnailConverter : IValueConverter
{
    private const int DefaultDecodeWidth = 512;
    private const int MaxDecodeWidth = 2048;
    private const int MaxDecodeHeight = 2048;
    // 容量按解码字节计费：典型列表缩略图（≤512 宽桶）约 0.3-1.2 MiB/张，
    // 256 条/64 MiB 覆盖百余张近期图片，来回滚动不反复重解码。
    private const int MaxCachedThumbnails = 256;
    private const long MaxCachedPixelBytes = 64L * 1024 * 1024;
    private readonly object _cacheLock = new();
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _cache = new(new CacheKeyComparer());
    private readonly LinkedList<CacheEntry> _recency = [];
    private long _cachedPixelBytes;
    private long _invalidationVersion;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path ||
            string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        return ConvertPath(path, GetDecodeWidth(parameter));
    }

    /// <summary>
    /// 只查缓存不解码：纯内存命中，供 UI 线程同步显示。受管图片目录的文件由
    /// 本应用唯一写入（新 GUID 文件名一次性写入、删除伴随条目移出面板、原地
    /// 重写仅启动修复且修复后经 <see cref="Invalidate"/> 主动驱逐），因此命中
    /// 判定不需要逐次磁盘 stat——那会让每次滚动进入视口的容器实现都把 UI
    /// 线程阻塞在文件系统调用上（数据目录位于云盘/网络盘时毫秒级/次）。
    /// 文件真实有效性由后台解码路径 <see cref="Convert"/> 兜底校验。
    /// </summary>
    public bool TryGetCachedThumbnail(string path, int decodeWidth, out BitmapSource? image)
    {
        image = null;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            var key = new CacheKey(path, Math.Clamp(decodeWidth, 1, MaxDecodeWidth));
            lock (_cacheLock)
            {
                if (!_cache.TryGetValue(key, out var cached))
                {
                    return false;
                }

                _recency.Remove(cached);
                _recency.AddLast(cached);
                image = cached.Value.Image;
                return true;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 应用侧主动失效：驱逐该路径全部解码宽度的缓存条目。原地重写受管图片
    /// （目前仅启动时的零透明度修复）后必须调用，并使在途解码结果不再入缓存，
    /// 避免按旧内容读出的位图在驱逐后回写复活。
    /// </summary>
    public void Invalidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var normalized = Path.GetFullPath(path);
            lock (_cacheLock)
            {
                _invalidationVersion++;
                foreach (var node in _cache.Values
                             .Where(node => string.Equals(
                                 node.Value.Key.Path,
                                 normalized,
                                 StringComparison.OrdinalIgnoreCase))
                             .ToArray())
                {
                    RemoveCachedThumbnail(node);
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
        }
    }

    private BitmapSource? ConvertPath(string path, int decodeWidth)
    {
        // 此方法只应从后台线程调用（并发由调用方限流）：文件有效性与解码都在
        // UI 线程之外，锁只覆盖缓存簿记，不覆盖解码本身。
        try
        {
            var key = new CacheKey(path, Math.Clamp(decodeWidth, 1, MaxDecodeWidth));
            long versionAtRead;
            var file = new FileInfo(key.Path);
            lock (_cacheLock)
            {
                versionAtRead = _invalidationVersion;
                _cache.TryGetValue(key, out var cached);
                if (!file.Exists)
                {
                    if (cached is not null)
                    {
                        RemoveCachedThumbnail(cached);
                    }

                    return null;
                }

                if (cached is not null &&
                    cached.Value.FileLength == file.Length &&
                    cached.Value.LastWriteTimeUtc == file.LastWriteTimeUtc)
                {
                    _recency.Remove(cached);
                    _recency.AddLast(cached);
                    return cached.Value.Image;
                }

                if (cached is not null)
                {
                    RemoveCachedThumbnail(cached);
                }
            }

            var length = file.Length;
            var lastWriteTime = file.LastWriteTimeUtc;
            var image = LoadThumbnail(key.Path, key.Width);
            // Reserve at least 32 bits per pixel, including formats WPF expands when rendering.
            var pixelBytes = ((long)image.PixelWidth * Math.Max(32, image.Format.BitsPerPixel) + 7) / 8 * image.PixelHeight;
            if (pixelBytes <= MaxCachedPixelBytes)
            {
                lock (_cacheLock)
                {
                    // 解码期间发生过主动失效就放弃入缓存：本次按已解码结果显示，
                    // 下次容器实现会重新解码出最新文件内容。并发未命中各自解码时，
                    // 后完成者替换先完成者的等价条目，不抛重复键。
                    if (versionAtRead == _invalidationVersion)
                    {
                        while (_recency.First is { } oldest &&
                               (_cache.Count >= MaxCachedThumbnails || _cachedPixelBytes + pixelBytes > MaxCachedPixelBytes))
                        {
                            RemoveCachedThumbnail(oldest);
                        }

                        if (_cache.TryGetValue(key, out var existing))
                        {
                            RemoveCachedThumbnail(existing);
                        }

                        var entry = new CacheEntry(key, length, lastWriteTime, image, pixelBytes);
                        _cache.Add(key, _recency.AddLast(entry));
                        _cachedPixelBytes += pixelBytes;
                    }
                }
            }

            return image;
        }
        catch (IOException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (FileFormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private void RemoveCachedThumbnail(LinkedListNode<CacheEntry> node)
    {
        _cache.Remove(node.Value.Key);
        _recency.Remove(node);
        _cachedPixelBytes -= node.Value.PixelBytes;
    }

    private static BitmapSource LoadThumbnail(string path, int decodeWidth)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.DelayCreation,
            BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        var limitHeight = (long)frame.PixelHeight * decodeWidth > (long)frame.PixelWidth * MaxDecodeHeight;
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (limitHeight)
        {
            image.DecodePixelHeight = MaxDecodeHeight;
        }
        else
        {
            image.DecodePixelWidth = decodeWidth;
        }

        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private readonly record struct CacheKey(string Path, int Width);

    private sealed class CacheKeyComparer : IEqualityComparer<CacheKey>
    {
        // NTFS 路径大小写不敏感；不同大小写的同一文件不应占据两份缓存。
        public bool Equals(CacheKey x, CacheKey y) =>
            x.Width == y.Width && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(CacheKey obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path), obj.Width);
    }

    private readonly record struct CacheEntry(
        CacheKey Key,
        long FileLength,
        DateTime LastWriteTimeUtc,
        BitmapSource Image,
        long PixelBytes);

    private static int GetDecodeWidth(object? parameter)
    {
        if (parameter is int numericWidth)
        {
            return Math.Clamp(numericWidth, 1, MaxDecodeWidth);
        }

        return parameter is string text &&
               int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedWidth)
            ? Math.Clamp(parsedWidth, 1, MaxDecodeWidth)
            : DefaultDecodeWidth;
    }
}
