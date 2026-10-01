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

    /// <summary>
    /// 构造复制到剪贴板的交付负载:单条与拖出负载同构;多条按板内顺序（置顶区
    /// 在前、各自排序,由调用方保证）合并——多条文字用换行连接为一段文本,纯图片
    /// 多选按来源顺序给出文件组,文字+图片混合则合并文本与图片文件组并存。
    /// 负载始终携带内部条目标记,供自动采集识别应用自身的复制并跳过。
    /// </summary>
    public DataObject BuildClipboardPayload(IReadOnlyList<BoardItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0 ||
            items.Any(item => item is null) ||
            items.Select(item => item.Id).Distinct().Count() != items.Count)
        {
            throw new ArgumentException(
                "A clipboard payload must contain unique items.",
                nameof(items));
        }

        if (items.Count == 1)
        {
            return Build(items[0]);
        }

        var textItems = items.Where(item => item.Kind == BoardItemKind.Text).ToArray();
        if (textItems.Length == 0)
        {
            // 纯图片多选与拖出负载同构:按来源顺序文件组 + 内部批量标记。
            return BuildInternalBatch(items);
        }

        var imagePaths = new List<string>();
        foreach (var item in items)
        {
            if (item.Kind != BoardItemKind.Image)
            {
                continue;
            }

            var path = item.ImageAbsolutePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException("The managed image file is missing.", path);
            }

            imagePaths.Add(path);
        }

        var data = new DataObject();
        data.SetData(
            InternalItemIdsFormat,
            items.Select(item => item.Id.ToString("D")).ToArray());
        var mergedText = string.Join(
            Environment.NewLine,
            textItems.Select(item => item.Text ?? string.Empty));
        data.SetData(DataFormats.UnicodeText, mergedText, autoConvert: true);
        data.SetData(DataFormats.Text, mergedText, autoConvert: true);
        if (imagePaths.Count > 0)
        {
            var files = new StringCollection();
            files.AddRange(imagePaths.ToArray());
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
