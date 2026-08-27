using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace FloatingTransferStation.Converters;

public sealed class ImageThumbnailConverter : IValueConverter
{
    private const int DefaultDecodeWidth = 512;
    private const int MaxDecodeWidth = 2048;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path ||
            string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) ||
            !File.Exists(path))
        {
            return null;
        }

        var decodeWidth = GetDecodeWidth(parameter);

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = decodeWidth;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
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
        catch (UriFormatException)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

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
