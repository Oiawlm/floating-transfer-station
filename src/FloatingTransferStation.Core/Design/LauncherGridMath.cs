namespace FloatingTransferStation.Design;

/// <summary>
/// 启动瓦片网格布局数学（#30 修订 2026-10-18，Issue #186）：列数与瓦片宽的唯一
/// 计算来源。拉伸填充——列数随视口宽增长，余宽全部摊进瓦片宽（域内构造保证
/// ≥ 下限 <see cref="DesignTokens.LauncherTileWidthPx"/>），行满排总宽精确等于
/// 视口（无死区）。数值全部取自 DesignTokens（节距=下限宽+沟道=92 是最小单元格，
/// 因此 n 列的最小可行视口恰为 n×84+(n−1)×8，边界处瓦片宽恰为下限）。
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
    /// 瓦片宽：(A − 沟道×(n−1)) / n——余宽摊进瓦片；n 列行满排总宽精确等于 A。
    /// 有效域为「n 列最小视口」（见 <see cref="ColumnCount"/> 边界）；域外（视口
    /// 0/极窄）列数下限兜底、瓦片宽可压缩，返回值经调用方钳制非负。
    /// </summary>
    public static double TileWidth(double viewportWidth, int columns) =>
        Math.Max(
            0,
            (viewportWidth - DesignTokens.LauncherTileGutterPx * (columns - 1)) / columns);
}
