using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public sealed class DragPayloadService
{
    public const string InternalItemIdFormat = "悬浮中转站/BoardItemId";
    public const string InternalItemIdsFormat = "悬浮中转站/BoardItemIds";

    public DataObject Build(BoardItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var data = new DataObject();
        data.SetData(InternalItemIdFormat, item.Id.ToString("D"));

        if (item.Kind == BoardItemKind.Text)
        {
            var text = item.Text ?? throw new InvalidDataException("Text item has no text.");
            data.SetData(DataFormats.UnicodeText, text, autoConvert: true);
            data.SetData(DataFormats.Text, text, autoConvert: true);
            return data;
        }

        var path = item.ImageAbsolutePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("The managed image file is missing.", path);
        }

        data.SetFileDropList(new StringCollection { path });
        data.SetImage(LoadBitmap(path));
        return data;
    }

    public DataObject BuildInternalBatch(IReadOnlyList<BoardItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count < 2 ||
            items.Any(item => item is null) ||
            items.Select(item => item.Id).Distinct().Count() != items.Count)
        {
            throw new ArgumentException(
                "An internal batch must contain at least two unique items.",
                nameof(items));
        }

        string[]? imagePaths = null;
        if (items.All(item => item.Kind == BoardItemKind.Image))
        {
            imagePaths = new string[items.Count];
            for (var index = 0; index < items.Count; index++)
            {
                var path = items[index].ImageAbsolutePath;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    throw new FileNotFoundException("The managed image file is missing.", path);
                }

                imagePaths[index] = path;
            }
        }

        var data = new DataObject();
        data.SetData(
            InternalItemIdsFormat,
            items.Select(item => item.Id.ToString("D")).ToArray());
        if (imagePaths is not null)
        {
            var files = new StringCollection();
            files.AddRange(imagePaths);
            data.SetFileDropList(files);
        }

        return data;
    }

    public Guid? GetInternalItemId(IDataObject data)
    {
        if (!data.GetDataPresent(InternalItemIdFormat) ||
            data.GetData(InternalItemIdFormat) is not string value ||
            !Guid.TryParse(value, out var id))
        {
            return null;
        }

        return id;
    }

    public IReadOnlyList<Guid>? GetInternalItemIds(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.GetDataPresent(InternalItemIdsFormat))
        {
            if (data.GetData(InternalItemIdsFormat) is not string[] values ||
                values.Length < 2)
            {
                return null;
            }

            var ids = new Guid[values.Length];
            for (var index = 0; index < values.Length; index++)
            {
                if (!Guid.TryParse(values[index], out ids[index]))
                {
                    return null;
                }
            }

            return ids.Distinct().Count() == ids.Length ? ids : null;
        }

        return GetInternalItemId(data) is { } id ? [id] : null;
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
