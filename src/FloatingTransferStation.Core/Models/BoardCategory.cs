using System.Globalization;

namespace FloatingTransferStation.Models;

public enum BoardCategory
{
    CustomerOriginal = 0,
    Reference = 1,
    /// <summary>Prompt 槽位 = 快捷启动（历史上是文本桶，#30 让位迁移）；成员名与
    /// 数值冻结不变，序列化身份兼容（仓库先例：复盘复用成员名 Reference）。</summary>
    Prompt = 2,
    Inbox = 3
}

/// <summary>标签的功能角色（#30，Issue #162）：内容桶（接收剪贴板采集与内容拖入）、
/// 复盘（记录面）与快捷启动（应用启动面）。角色挂靠分类目录、穷尽映射无兜底臂
/// （新增成员漏映射在质量门 -warnaserror 下即 CS8509 编译失败），是默认接收、
/// 拖放、搜索、清扫与表面切换的唯一权威分派依据。</summary>
public enum CategoryRole
{
    ContentBucket,
    Review,
    Launcher
}

public static class BoardCategoryCatalog
{
    public const int MaxDisplayNameLength = 6;

    public static IReadOnlyList<BoardCategory> Ordered { get; } =
        Array.AsReadOnly(new[]
        {
            BoardCategory.CustomerOriginal,
            BoardCategory.Reference,
            BoardCategory.Prompt,
            BoardCategory.Inbox
        });

    public static string DisplayName(BoardCategory category) => category switch
    {
        BoardCategory.CustomerOriginal => "图片",
        BoardCategory.Reference => "文本1",
        BoardCategory.Prompt => "启动",
        BoardCategory.Inbox => "待分类",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    };

    /// <summary>分类的功能角色（#30）：穷尽 switch、无兜底臂——新增分类漏映射在
    /// Core -warnaserror 质量门下即 CS8509 编译失败，角色枚举因此免手改清单。
    /// CS8524（未命名枚举值，仅手改持久化 JSON 写入越界整数才会出现）显式豁免：
    /// 存储边界（LocalStore 读取）对分类做 <see cref="IsDefined"/> 守卫、越界值按
    /// 损坏条目丢弃，本表达式对未命名值抛 SwitchExpressionException 属不可达防御。</summary>
#pragma warning disable CS8524 // switch 表达式不处理未命名枚举值（见上：装载边界已清洗；漏映射 CS8509 仍为编译错误）
    public static CategoryRole RoleOf(BoardCategory category) => category switch
    {
        BoardCategory.CustomerOriginal => CategoryRole.ContentBucket,
        BoardCategory.Reference => CategoryRole.Review,
        BoardCategory.Prompt => CategoryRole.Launcher,
        BoardCategory.Inbox => CategoryRole.ContentBucket
    };
#pragma warning restore CS8524

    /// <summary>是否内容桶（接收剪贴板采集、内容拖入、搜索与自动清扫）。
    /// 复盘与启动标签共用非内容桶排除语义，消费位点按本谓词收敛（#18 修订）。</summary>
    public static bool IsContentBucket(BoardCategory category) =>
        RoleOf(category) == CategoryRole.ContentBucket;

    public static bool IsDefined(BoardCategory category) =>
        category is >= BoardCategory.CustomerOriginal and <= BoardCategory.Inbox;

    public static bool IsValidDisplayName(string? name) =>
        name is not null &&
        StringInfo.ParseCombiningCharacters(name).Length <= MaxDisplayNameLength;

    public static string LimitDisplayName(string? name)
    {
        var value = name ?? string.Empty;
        var elementIndexes = StringInfo.ParseCombiningCharacters(value);
        return elementIndexes.Length <= MaxDisplayNameLength
            ? value
            : value[..elementIndexes[MaxDisplayNameLength]];
    }
}
