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

    [JsonIgnore]
    public string? ImageAbsolutePath { get; set; }

    public static BoardItem CreateText(string text, Guid id, DateTimeOffset createdAt) => new()
    {
        Id = id,
        Kind = BoardItemKind.Text,
        Category = BoardCategory.Inbox,
        Order = 0,
        CreatedAt = createdAt,
        Text = text
    };

    public static BoardItem CreateImage(
        Guid id,
        string relativePath,
        string absolutePath,
        DateTimeOffset createdAt) => new()
        {
            Id = id,
            Kind = BoardItemKind.Image,
            Category = BoardCategory.Inbox,
            Order = 0,
            CreatedAt = createdAt,
            ImageRelativePath = relativePath,
            ImageAbsolutePath = absolutePath
        };

    public BoardItem CloneForSnapshot() => new()
    {
        Id = Id,
        Kind = Kind,
        Category = Category,
        Order = Order,
        CreatedAt = CreatedAt,
        Text = Text,
        ImageRelativePath = ImageRelativePath,
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
