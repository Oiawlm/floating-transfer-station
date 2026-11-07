using System.Globalization;

namespace FloatingTransferStation.Models;

/// <summary>
/// 分类内子分组目录（实验性功能，契约 #57）：固定 4 槽/分类——不加、不减、不排序；
/// 槽号索引即稳定身份（槽数固定下与 <see cref="WindowSettings.CategoryOrder"/> 的
/// 枚举序列持久化同构，JSON 足迹最小，改名只改 settings 名清单、归属不丢）。
/// 镜像 <see cref="BoardCategoryCatalog"/> 的目录风格。
/// </summary>
public static class SubgroupCatalog
{
    public const int SlotCount = 4;

    /// <summary>「全部」伪 chip 显示名（契约 #57 修订，实验开关默认关）：开关开启
    /// 时恒居轨首的聚合视图入口，无槽身份——不参与槽序拖排/改名/拖放归属/默认
    /// 落点。两字恒容于开启后的五列等分（最小面板每 chip ≈57 DIP）。</summary>
    public const string AllChipName = "全部";

    /// <summary>
    /// 子分组名上限（裁决＝3 字符）：最小面板 310 → 轨可用宽约 260 DIP、四槽
    /// UniformGrid 各约 65 DIP，3 字×12px＝36 加内边距充裕、4 字溢出；默认集
    /// （3/2/3/2）恰全部 ≤3。上限由 chip 编辑器 <c>MaxLength</c> 单一来源承载
    /// （照备注 120 先例），设置侧 <see cref="LimitName"/> 归一。
    /// </summary>
    public const int MaxNameLength = 3;

    /// <summary>默认槽名（用户举例即默认集）：待分类/重要/不重要/待办。</summary>
    public static string DefaultName(int slot) => slot switch
    {
        0 => "待分类",
        1 => "重要",
        2 => "不重要",
        3 => "待办",
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, null)
    };

    public static bool IsValidSlot(int? slot) =>
        slot is >= 0 and < SlotCount;

    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        StringInfo.ParseCombiningCharacters(name).Length <= MaxNameLength;

    public static string LimitName(string? name)
    {
        var value = name ?? string.Empty;
        var elementIndexes = StringInfo.ParseCombiningCharacters(value);
        return elementIndexes.Length <= MaxNameLength
            ? value
            : value[..elementIndexes[MaxNameLength]];
    }
}
