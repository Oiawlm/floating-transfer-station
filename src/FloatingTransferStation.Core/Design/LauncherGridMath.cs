using FloatingTransferStation.Models;

namespace FloatingTransferStation.Design;

/// <summary>
/// 启动瓦片网格布局数学（#30 修订 2026-10-18，Issue #186）：列数与瓦片宽的唯一
/// 计算来源。拉伸填充——列数随视口宽增长，余宽全部摊进瓦片宽（域内构造保证
/// ≥ 下限 <see cref="DesignTokens.LauncherTileWidthPx"/>），行满排总宽精确等于
/// 视口（无死区）。数值全部取自 DesignTokens（节距=下限宽+沟道=92 是最小单元格，
/// 因此 n 列的最小可行视口恰为 n×84+(n−1)×8，边界处瓦片宽恰为下限）。
/// 列数策略（默认自适应 = 本类原始公式；Fixed3–Fixed6 为用户显式固定档）：固定
/// 档与视口无关恒等于档位值，瓦片宽仍由 <see cref="TileWidth"/> 摊分——行满排
/// 无死区不变，窄视口下大列数可把瓦片压到下限以下（显式选择的语义）。
/// 承载面板（LauncherTilePanel）在 Measure/Arrange 内直接消费本类，不再有
/// 「公式限宽＋面板重量化」双口径与自引用回写结构。
/// </summary>
public static class LauncherGridMath
{
    /// <summary>
    /// 列数：clamp(floor((A+沟道)/节距), 3, 6)。A=视口宽；视口 0（窄轨/收起期）与
    /// 非有限输入走下限兜底，不产生非法列数。
    /// </summary>
    public static int ColumnCount(double viewportWidth)
    {
        if (!double.IsFinite(viewportWidth))
        {
            return DesignTokens.LauncherTileMinColumns;
        }

        return Math.Clamp(
            (int)Math.Floor((viewportWidth + DesignTokens.LauncherTileGutterPx) / DesignTokens.LauncherTilePitchPx),
            DesignTokens.LauncherTileMinColumns,
            DesignTokens.LauncherTileMaxColumns);
    }

    /// <summary>
    /// 列数（策略感知唯一入口）：固定档恒等于档位列数（与视口无关，视口 0 收起期
    /// 亦然——瓦片宽经 <see cref="TileWidth"/> 自然钳 0）；自适应档走既有公式。
    /// 穷尽 switch 无兜底臂（与 BoardCategoryCatalog.RoleOf 同法）：新增策略档
    /// 漏映射在 -warnaserror 质量门下 CS8509 编译失败。CS8524（未命名枚举值）显式
    /// 豁免：策略只经偏好管线与设置窗 Tag 匹配进入、装载边界不产生越界值，本表达式
    /// 对未命名值抛 SwitchExpressionException 属不可达防御。
    /// </summary>
#pragma warning disable CS8524 // switch 表达式不处理未命名枚举值（见上：入口已收敛；漏映射 CS8509 仍为编译错误）
    public static int ColumnCount(double viewportWidth, LauncherColumnStrategy strategy) => strategy switch
    {
        LauncherColumnStrategy.Adaptive => ColumnCount(viewportWidth),
        LauncherColumnStrategy.Fixed3 => 3,
        LauncherColumnStrategy.Fixed4 => 4,
        LauncherColumnStrategy.Fixed5 => 5,
        LauncherColumnStrategy.Fixed6 => 6
    };
#pragma warning restore CS8524

    /// <summary>
    /// 瓦片宽：(A − 沟道×(n−1)) / n——余宽摊进瓦片；n 列行满排总宽精确等于 A。
    /// 有效域为「n 列最小视口」（见 <see cref="ColumnCount"/> 边界）；域外（视口
    /// 0/极窄，含固定档列数大于视口可容列数）瓦片宽可压缩，返回值钳制非负。
    /// </summary>
    public static double TileWidth(double viewportWidth, int columns) =>
        Math.Max(
            0,
            (viewportWidth - DesignTokens.LauncherTileGutterPx * (columns - 1)) / columns);
}
