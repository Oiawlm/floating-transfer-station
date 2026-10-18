using FloatingTransferStation.Design;

namespace FloatingTransferStation.Core.Tests;

/// <summary>
/// 启动瓦片网格布局数学（#30 修订 2026-10-18，Issue #186）表驱动锁定：
/// 列数 clamp(floor((A+8)/92),3,6)、瓦片宽=(A−8(n−1))/n、构造保证 w≥84（域内）、
/// 行满排总宽精确等于视口（无死区）。数值样本覆盖面板宽域 [310,640] 的三档
/// 实测视口（286/336/616）与全部列数档位边界。
/// </summary>
[TestClass]
public sealed class LauncherGridMathTests
{
    public static IEnumerable<object[]> ColumnCountTable
    {
        get
        {
            // 视口宽 A → 期望列数（310/360/640 面板宽对应 286/336/616 视口为实测样本）。
            yield return new[] { (object)286d, (object)3 };   // 面板 310
            yield return new[] { (object)336d, (object)3 };   // 面板 360（用户默认宽）
            yield return new[] { (object)412d, (object)4 };   // 面板 436
            yield return new[] { (object)456d, (object)5 };   // 面板 480
            yield return new[] { (object)616d, (object)6 };   // 面板 640
            yield return new[] { (object)900d, (object)6 };   // 超域钳上限 6
            yield return new[] { (object)200d, (object)3 };   // 欠域钳下限 3
            yield return new[] { (object)0d, (object)3 };     // 窄轨视口 0 兜底下限
        }
    }

    [TestMethod]
    [DynamicData(nameof(ColumnCountTable))]
    public void ColumnCount_FollowsClampedFloorFormula(double viewportWidth, int expected)
    {
        Assert.AreEqual(expected, LauncherGridMath.ColumnCount(viewportWidth));
    }

    [TestMethod]
    [DataRow(286d, 3, 90d)]      // (286−16)/3
    [DataRow(336d, 3, 320d / 3)] // (336−16)/3 ≈ 106.67
    [DataRow(456d, 5, 424d / 5)] // (456−32)/5 = 84.8
    [DataRow(616d, 6, 96d)]      // (616−40)/6
    [DataRow(268d, 3, 84d)]      // 3 列最小视口：瓦片宽恰=下限
    [DataRow(544d, 6, 84d)]      // 6 列最小视口：瓦片宽恰=下限
    public void TileWidth_DistributesFullViewportAcrossColumns(double viewportWidth, int columns, double expected)
    {
        Assert.AreEqual(expected, LauncherGridMath.TileWidth(viewportWidth, columns), 1e-9);
    }

    [TestMethod]
    [DataRow(286d)]
    [DataRow(336d)]
    [DataRow(456d)]
    [DataRow(616d)]
    [DataRow(268d)]
    [DataRow(544d)]
    public void RowLayout_FillsViewportWithNoDeadZone(double viewportWidth)
    {
        var columns = LauncherGridMath.ColumnCount(viewportWidth);
        var tileWidth = LauncherGridMath.TileWidth(viewportWidth, columns);
        var rowWidth = columns * tileWidth + (columns - 1) * DesignTokens.LauncherTileGutterPx;
        Assert.AreEqual(viewportWidth, rowWidth, 1e-9, "行满排总宽=视口（无死区）。");
    }

    [TestMethod]
    [DataRow(269d)]
    [DataRow(300d)]
    [DataRow(336d)]
    [DataRow(456d)]
    [DataRow(543d)]
    [DataRow(616d)]
    [DataRow(700d)]
    public void TileWidth_StaysAtOrAboveMinimumWithinValidDomain(double viewportWidth)
    {
        var columns = LauncherGridMath.ColumnCount(viewportWidth);
        var tileWidth = LauncherGridMath.TileWidth(viewportWidth, columns);
        Assert.IsTrue(
            tileWidth >= DesignTokens.LauncherTileWidthPx - 1e-9,
            $"域内构造保证瓦片宽 ≥ 84 下限（A={viewportWidth}, n={columns}, w={tileWidth}）。");
    }

    [TestMethod]
    public void ColumnBoundaries_PitchFormulaGuaranteesMinimumTileWidth()
    {
        // 每档列数的最小视口 = n×84+(n−1)×8（瓦片宽恰=下限）；该视口下公式恰好给出 n 列。
        for (var columns = DesignTokens.LauncherTileMinColumns;
             columns <= DesignTokens.LauncherTileMaxColumns;
             columns++)
        {
            var minViewport = columns * DesignTokens.LauncherTileWidthPx +
                (columns - 1) * DesignTokens.LauncherTileGutterPx;
            Assert.AreEqual(columns, LauncherGridMath.ColumnCount(minViewport), $"n={columns} 最小视口恰跨档。");
            Assert.AreEqual(
                DesignTokens.LauncherTileWidthPx,
                LauncherGridMath.TileWidth(minViewport, columns),
                1e-9,
                $"n={columns} 最小视口处瓦片宽=下限。");
        }
    }

    [TestMethod]
    public void ColumnCount_RejectsNonFiniteAsLowerBound()
    {
        // 防御性：非有限视口（理论不可达）走下限兜底，不得抛出或产生非法列数。
        Assert.AreEqual(DesignTokens.LauncherTileMinColumns, LauncherGridMath.ColumnCount(double.NaN));
        Assert.AreEqual(DesignTokens.LauncherTileMinColumns, LauncherGridMath.ColumnCount(double.PositiveInfinity));
    }
}
