using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace FloatingTransferStation.Converters;

public sealed class ImageThumbnailConverter : IValueConverter
{
    private const int DefaultDecodeWidth = 512;
    private const int MaxDecodeWidth = 2048;
    private const int MaxDecodeHeight = 2048;
    private const int MaxCachedThumbnails = 64;
    private const long MaxCachedPixelBytes = 32L * 1024 * 1024;
    private readonly object _cacheLock = new();
    private readonly Dictionary<(string Path, int Width), LinkedListNode<CacheEntry>> _cache = [];
    private readonly LinkedList<CacheEntry> _recency = [];
    private long _cachedPixelBytes;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path ||
            string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        var decodeWidth = GetDecodeWidth(parameter);

        try
        {
            lock (_cacheLock)
            {
                var key = (Path: Path.GetFullPath(path), Width: decodeWidth);
                var file = new FileInfo(key.Path);
                _cache.TryGetValue(key, out var cached);
                if (!file.Exists)
                {
                    if (cached is not null)
                    {
                        RemoveCachedThumbnail(cached);
                    }

                    return null;
                }

                var length = file.Length;
                var lastWriteTime = file.LastWriteTimeUtc;
                if (cached is not null)
                {
                    if (cached.Value.FileLength == length && cached.Value.LastWriteTimeUtc == lastWriteTime)
                    {
                        _recency.Remove(cached);
                        _recency.AddLast(cached);
                        return cached.Value.Image;
                    }

                    RemoveCachedThumbnail(cached);
                }

                var image = LoadThumbnail(key.Path, decodeWidth);
                // Reserve at least 32 bits per pixel, including formats WPF expands when rendering.
                var pixelBytes = ((long)image.PixelWidth * Math.Max(32, image.Format.BitsPerPixel) + 7) / 8 * image.PixelHeight;
                if (pixelBytes <= MaxCachedPixelBytes)
                {
                    while (_recency.First is { } oldest &&
                           (_cache.Count >= MaxCachedThumbnails || _cachedPixelBytes + pixelBytes > MaxCachedPixelBytes))
                    {
                        RemoveCachedThumbnail(oldest);
                    }

                    var entry = new CacheEntry(key, length, lastWriteTime, image, pixelBytes);
                    _cache.Add(key, _recency.AddLast(entry));
                    _cachedPixelBytes += pixelBytes;
                }

                return image;
            }
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

    private sealed record CacheEntry(
        (string Path, int Width) Key,
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
