using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public sealed class DragPayloadService
{
    public const string InternalItemIdFormat = "悬浮中转站/BoardItemId";
    public const string InternalItemIdsFormat = "悬浮中转站/BoardItemIds";
    public const string PreferredDropEffectFormat = "Preferred DropEffect";

    /// <summary>
    /// 图片拖出为文件实验开关（契约 #58）：false（默认）＝拖出负载剥离外部交付
    /// 形态（FileDropList/Image），只保留内部格式与文本格式——窗内拖放与剪贴板
    /// 复制零变化；true＝维持既有文件拖出能力并附加 Preferred DropEffect(Copy)
    /// 声明。开关判断唯一存在于本服务内部（门控单点），UI 事件层零感知。
    /// </summary>
    public bool ExternalFilePayloadsEnabled { get; set; }

    public DataObject Build(BoardItem item) =>
        BuildCore(item, externalFilePayloads: true, declareCopyOnly: false);

    /// <summary>
    /// 构造拖出负载（契约 #58 门控入口）：文本卡两态不变；图片卡 off＝仅内部 ID
    /// （缺图校验是文件交付前置，剥离交付即剥离校验），on＝内部 ID＋FileDropList
    /// ＋位图＋Preferred DropEffect(Copy)。
    /// </summary>
    public DataObject BuildDrag(BoardItem item)
    {
        var external = ExternalFilePayloadsEnabled;
        return BuildCore(item, externalFilePayloads: external, declareCopyOnly: external);
    }

    /// <summary>
    /// 构造拖出批量负载（契约 #58 门控入口）：纯图片多选 off＝仅内部 ID，on＝内部
    /// ID＋按板内顺序的 FileDropList＋Preferred DropEffect(Copy)；混合批量两态均
    /// 仅内部 ID（现状即无外部格式）。
    /// </summary>
    public DataObject BuildDragBatch(IReadOnlyList<BoardItem> items)
    {
        var external = ExternalFilePayloadsEnabled;
        return BuildBatchCore(items, externalFilePayloads: external, declareCopyOnly: external);
    }

    /// <summary>
    /// 单条负载共享核心（<paramref name="externalFilePayloads"/>：是否携带外部
    /// 交付形态——false 时图片卡不再校验缺图，内部拖放可继续；<paramref
    /// name="declareCopyOnly"/>：是否附加 Preferred DropEffect(Copy) 声明）。
    /// </summary>
    private DataObject BuildCore(BoardItem item, bool externalFilePayloads, bool declareCopyOnly)
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

        if (!externalFilePayloads)
        {
            return data;
        }

        var path = item.ImageAbsolutePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("The managed image file is missing.", path);
        }

        data.SetFileDropList(new StringCollection { path });
        data.SetImage(LoadBitmap(path));
        DeclarePreferredCopyEffect(data, declareCopyOnly);
        return data;
    }

    /// <summary>
    /// 批量负载共享核心（参数语义同 <see cref="BuildCore"/>；至少两条唯一条目，
    /// 纯图片多选按板内顺序交付文件组）。
    /// </summary>
    private DataObject BuildBatchCore(
        IReadOnlyList<BoardItem> items,
        bool externalFilePayloads,
        bool declareCopyOnly)
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

        var data = new DataObject();
        data.SetData(
            InternalItemIdsFormat,
            items.Select(item => item.Id.ToString("D")).ToArray());
        if (!externalFilePayloads || items.Any(item => item.Kind != BoardItemKind.Image))
        {
            return data;
        }

        var files = new StringCollection();
        foreach (var item in items)
        {
            var path = item.ImageAbsolutePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException("The managed image file is missing.", path);
            }

            files.Add(path);
        }

        data.SetFileDropList(files);
        DeclarePreferredCopyEffect(data, declareCopyOnly);
        return data;
    }

    /// <summary>CFSTR_PREFERREDDROPEFFECT：4 字节 DROPEFFECT_COPY 流（Shell 语义）
    /// 声明「只复制」，防外部目标按 Move 处理而删除受管源文件；autoConvert:false
    /// 保持原始流格式。</summary>
    private static void DeclarePreferredCopyEffect(DataObject data, bool declare)
    {
        if (!declare)
        {
            return;
        }

        data.SetData(
            PreferredDropEffectFormat,
            new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)),
            autoConvert: false);
    }

    /// <summary>构造内部批量负载（剪贴板纯图片多选路径沿用的既有入口，行为零变化：
    /// 携带外部文件组、无效果声明）。</summary>
    public DataObject BuildInternalBatch(IReadOnlyList<BoardItem> items) =>
        BuildBatchCore(items, externalFilePayloads: true, declareCopyOnly: false);

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
