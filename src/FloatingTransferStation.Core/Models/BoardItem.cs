using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace FloatingTransferStation.Models;

public sealed class BoardItem : INotifyPropertyChanged
{
    /// <summary>
    /// 卡片正文排版预览的字符上限：远超任何面板宽度下 5 行(MaxHeight=100)
    /// 可见的文本量，显示层绑定 <see cref="PreviewText"/> 即可保证视觉不变，
    /// 又避免 WPF TextFormatter 对超长全文的近线性排版成本(滚动卡顿主因之一)。
    /// 编辑、复制、拖出与全文搜索仍使用 <see cref="Text"/> 全文。
    /// </summary>
    private const int PreviewTextLength = 600;

    private bool _isPinned;
    private bool _startsNormalRegion;
    private bool _startsSubgroupRegion;
    private string? _subgroupHeader;
    private int? _subgroup;
    private bool _isEditing;
    private string? _draftText;
    private bool _isEditingNote;
    private string? _draftNote;
    private bool _isAiOrganizing;
    private bool _isAiNaming;
    private string? _note;
    private string? _imageTitle;
    private string? _text;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required Guid Id { get; init; }
    public required BoardItemKind Kind { get; init; }
    public required BoardCategory Category { get; set; }
    public required int Order { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>文字内容。1.13.1 起允许就地更新（用户卡片编辑）；持久化与创建语义不变。</summary>
    public string? Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewText)));
            }
        }
    }

    /// <summary>显示层排版用的有界预览；全文语义（搜索/编辑/复制/拖出）一律读 <see cref="Text"/>。</summary>
    [JsonIgnore]
    public string? PreviewText => Text is { Length: > PreviewTextLength }
        ? Text[..SurrogateSafeLength()]
        : Text;

    /// <summary>截断点不得落在代理对中间（emoji 等增补平面字符按 UTF-16 存为两个码元）。</summary>
    private int SurrogateSafeLength()
    {
        return char.IsHighSurrogate(Text![PreviewTextLength - 1])
            ? PreviewTextLength - 1
            : PreviewTextLength;
    }

    public string? ImageRelativePath { get; init; }

    public bool IsPinned
    {
        get => _isPinned;
        set => SetProperty(ref _isPinned, value);
    }

    [JsonIgnore]
    public bool StartsNormalRegion
    {
        get => _startsNormalRegion;
        internal set => SetProperty(ref _startsNormalRegion, value);
    }

    /// <summary>
    /// 分类内子分组（实验性，契约 #57）：槽号 0–3，null=未指定。持久化字段
    /// （board.json，免迁移照 <see cref="Note"/> 口径——旧 JSON 缺字段读为 null，
    /// 越界值由装载端一次回落 null，SchemaVersion 保持 1）。单一事实源：归属
    /// 唯一存本字段，分组边界/组头全部由 Reindex 派生（不持久化派生值）。
    /// </summary>
    public int? Subgroup
    {
        get => _subgroup;
        set => SetProperty(ref _subgroup, SubgroupCatalog.IsValidSlot(value) ? value : null);
    }

    /// <summary>子分组组边界派生标记（契约 #57，照 <see cref="StartsNormalRegion"/>
    /// 惯例）：普通区内与前邻卡槽不同且非置顶转换点；仅 GroupedView 模式由
    /// BoardService.Reindex 置位，Off/ChipRail 恒 false（渲染与现状一致）。
    /// 会话派生态，不持久化。</summary>
    [JsonIgnore]
    public bool StartsSubgroupRegion
    {
        get => _startsSubgroupRegion;
        internal set => SetProperty(ref _startsSubgroupRegion, value);
    }

    /// <summary>子分组组头名（契约 #57）：Reindex 按 BoardService 持有的有效名
    /// 清单快照解析（settings 仍是持久化单一事实源，服务侧仅派生缓存）；仅在
    /// 命名组的边界卡上非 null（未指定组无组头），非边界卡恒 null。会话派生态，
    /// 不持久化。</summary>
    [JsonIgnore]
    public string? SubgroupHeader
    {
        get => _subgroupHeader;
        internal set => SetProperty(ref _subgroupHeader, value);
    }

    [JsonIgnore]
    public string? ImageAbsolutePath { get; set; }

    /// <summary>
    /// AI 识图命名的图片标题（仅图片卡元数据）：null=未命名；拖出交付与
    /// ToolTip 的单一事实源；不参与搜索。持久化字段（board.json，免迁移照
    /// <see cref="Note"/> 口径——旧 JSON 缺字段读为 null，SchemaVersion 不动），
    /// 标题只经落板管线写入（创建工厂不加参），空串一律归一为 null。
    /// </summary>
    public string? ImageTitle
    {
        get => _imageTitle;
        set => SetProperty(ref _imageTitle, string.IsNullOrEmpty(value) ? null : value);
    }

    /// <summary>
    /// 就地编辑会话标志（1.22.0 起编辑器移入卡片 DataTemplate，由条目状态驱动
    /// 显示/编辑原位切换；挂载在条目而非容器上，虚拟化回收不错乱）。会话状态
    /// 不持久化，编辑草稿以 <see cref="DraftText"/> 承载，提交前绝不进入
    /// <see cref="Text"/>（显示与持久化只认 Text/PreviewText）。
    /// </summary>
    [JsonIgnore]
    public bool IsEditing
    {
        get => _isEditing;
        private set => SetProperty(ref _isEditing, value);
    }

    /// <summary>编辑中的全文草稿（编辑器双向绑定源）。非编辑态恒为 null。</summary>
    [JsonIgnore]
    public string? DraftText
    {
        get => _draftText;
        set => SetProperty(ref _draftText, value);
    }

    /// <summary>
    /// 进入编辑：草稿种子为全文（与显示用的有界预览无关）。
    /// </summary>
    public void BeginTextEdit()
    {
        DraftText = Text ?? string.Empty;
        IsEditing = true;
    }

    /// <summary>
    /// 结束编辑（提交与取消共用的状态清理）：草稿由调用方在调用前读取，
    /// 此处一并丢弃，非编辑态不残留草稿。
    /// </summary>
    public void EndTextEdit()
    {
        IsEditing = false;
        DraftText = null;
    }

    /// <summary>
    /// 用户手写的短备注（1.28.0，仅文字卡的元数据）：帮助一眼识别长卡内容，
    /// 不参与搜索、复制、拖出，卡片内容语义零变化。null=无备注；空串一律
    /// 归一为 null（「空白提交=删除已有备注」的模型侧落点）。
    /// </summary>
    public string? Note
    {
        get => _note;
        set => SetProperty(ref _note, string.IsNullOrEmpty(value) ? null : value);
    }

    /// <summary>
    /// 备注就地编辑会话标志（与 <see cref="IsEditing"/> 同族：状态挂条目，
    /// 虚拟化回收不错乱；与正文编辑双向互斥，单会话语义由窗口层保证）。
    /// 会话状态不持久化，草稿以 <see cref="DraftNote"/> 承载，提交前绝不
    /// 进入 <see cref="Note"/>。
    /// </summary>
    [JsonIgnore]
    public bool IsEditingNote
    {
        get => _isEditingNote;
        private set => SetProperty(ref _isEditingNote, value);
    }

    /// <summary>备注编辑中的草稿（编辑器双向绑定源）。非编辑态恒为 null。</summary>
    [JsonIgnore]
    public string? DraftNote
    {
        get => _draftNote;
        set => SetProperty(ref _draftNote, value);
    }

    /// <summary>进入备注编辑：草稿种子为现备注（预填防「失焦即误删」）。</summary>
    public void BeginNoteEdit()
    {
        DraftNote = Note ?? string.Empty;
        IsEditingNote = true;
    }

    /// <summary>结束备注编辑（提交与取消共用的状态清理），非编辑态不残留草稿。</summary>
    public void EndNoteEdit()
    {
        IsEditingNote = false;
        DraftNote = null;
    }

    /// <summary>
    /// AI 整理在途标志（会话状态，照 <see cref="IsEditingNote"/> 惯例：挂条目、
    /// 虚拟化回收不错乱、不持久化）。驱动卡片操作钮的忙态 glyph 与点击防抖；
    /// 每卡单飞（重入由入口守卫丢弃），跨卡允许并发。
    /// </summary>
    [JsonIgnore]
    public bool IsAiOrganizing
    {
        get => _isAiOrganizing;
        private set => SetProperty(ref _isAiOrganizing, value);
    }

    /// <summary>进入 AI 整理（原文由调用方在调用前捕获）。</summary>
    public void BeginAiOrganize() => IsAiOrganizing = true;

    /// <summary>结束 AI 整理（成功、失败与并发放弃共用的状态清理）。</summary>
    public void EndAiOrganize() => IsAiOrganizing = false;

    /// <summary>
    /// AI 识图命名在途标志（会话状态，照 <see cref="IsAiOrganizing"/> 惯例：
    /// 挂条目、虚拟化回收不错乱、不持久化）。驱动命名任务的每卡单飞防抖
    /// （重入由调度器入口守卫丢弃），跨卡允许排队。
    /// </summary>
    [JsonIgnore]
    public bool IsAiNaming
    {
        get => _isAiNaming;
        private set => SetProperty(ref _isAiNaming, value);
    }

    /// <summary>进入 AI 识图命名。</summary>
    public void BeginAiNaming() => IsAiNaming = true;

    /// <summary>结束 AI 识图命名（成功、失败与放弃共用的状态清理）。</summary>
    public void EndAiNaming() => IsAiNaming = false;

    public static BoardItem CreateText(
        string text,
        Guid id,
        DateTimeOffset createdAt,
        int? subgroup = null) => new()
        {
            Id = id,
            Kind = BoardItemKind.Text,
            Category = BoardCategory.Inbox,
            Order = 0,
            CreatedAt = createdAt,
            Text = text,
            Subgroup = subgroup
        };

    public static BoardItem CreateImage(
        Guid id,
        string relativePath,
        string absolutePath,
        DateTimeOffset createdAt,
        int? subgroup = null) => new()
        {
            Id = id,
            Kind = BoardItemKind.Image,
            Category = BoardCategory.Inbox,
            Order = 0,
            CreatedAt = createdAt,
            ImageRelativePath = relativePath,
            ImageAbsolutePath = absolutePath,
            Subgroup = subgroup
        };

    public BoardItem CloneForSnapshot() => new()
    {
        Id = Id,
        Kind = Kind,
        Category = Category,
        Order = Order,
        CreatedAt = CreatedAt,
        Text = Text,
        Note = Note,
        Subgroup = Subgroup,
        ImageRelativePath = ImageRelativePath,
        ImageTitle = ImageTitle,
        IsPinned = IsPinned,
        ImageAbsolutePath = ImageAbsolutePath
    };

    private bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
