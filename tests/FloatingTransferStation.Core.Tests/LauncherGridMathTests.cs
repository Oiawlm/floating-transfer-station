using FloatingTransferStation.Design;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Core.Tests;

/// <summary>
/// 启动瓦片网格布局数学（#30 修订 2026-10-18，Issue #186）表驱动锁定：
/// 列数 clamp(floor((A+8)/92),3,6)、瓦片宽=(A−8(n−1))/n、构造保证 w≥84（域内）、
/// 行满排总宽精确等于视口（无死区）。数值样本覆盖面板宽域 [310,640] 的三档
/// 实测视口（286/336/616）与全部列数档位边界。
/// 列数策略（2026-10-24 修订）：策略感知重载——固定档 Fixed3–Fixed6 恒等于档位
/// 列数（与视口无关，含 0/超域/非有限），瓦片宽仍按摊分公式推导（行满排无死区
/// 不变；窄视口下大列数压缩到 84 下限以下＝显式选择的语义）；自适应档与原始
/// 公式逐值等价（默认偏好＝现行为的数学面锁定）。
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

    // ---- 列数策略（2026-10-24 修订）：固定档恒=n、自适应档=原始公式 ----

    public static IEnumerable<object[]> FixedStrategyColumnTable
    {
        get
        {
            // 固定档：列数=档位值，与视口无关——覆盖视口 0（收起期）、全部列档
            // 边界视口（268/544）、实测域三档（286/336/616）、超域（900）与
            // 非有限（理论不可达防御面）。
            foreach (var (strategy, columns) in new[]
                     {
                         (LauncherColumnStrategy.Fixed3, 3),
                         (LauncherColumnStrategy.Fixed4, 4),
                         (LauncherColumnStrategy.Fixed5, 5),
                         (LauncherColumnStrategy.Fixed6, 6)
                     })
            {
                foreach (var viewport in new double[] { 0, 268, 286, 336, 456, 544, 616, 900, double.PositiveInfinity })
                {
                    yield return new[] { (object)viewport, (object)strategy, (object)columns };
                }
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(FixedStrategyColumnTable))]
    public void ColumnCount_FixedStrategy_ReturnsFixedColumnsRegardlessOfViewport(
        double viewportWidth, LauncherColumnStrategy strategy, int expected)
    {
        Assert.AreEqual(expected, LauncherGridMath.ColumnCount(viewportWidth, strategy));
    }

    [TestMethod]
    public void ColumnCount_AdaptiveStrategy_MatchesOriginalFormulaValueForValue()
    {
        // 默认偏好（自适应）＝现行为：策略重载与原始公式在全域逐值等价，
        // 含非有限兜底路径——老用户升级零感知的数学面。
        foreach (var viewport in new double[] { 0, 200, 268, 269, 286, 336, 412, 456, 544, 616, 900, double.NaN })
        {
            Assert.AreEqual(
                LauncherGridMath.ColumnCount(viewport),
                LauncherGridMath.ColumnCount(viewport, LauncherColumnStrategy.Adaptive),
                $"A={viewport}");
        }
    }

    [TestMethod]
    [DataRow(LauncherColumnStrategy.Fixed3, 286d, 90d)]      // (286−16)/3＝与自适应同档同值
    [DataRow(LauncherColumnStrategy.Fixed4, 456d, 108d)]     // (456−24)/4
    [DataRow(LauncherColumnStrategy.Fixed4, 336d, 78d)]      // (336−24)/4：窄域压缩到 84 下限以下＝显式选择
    [DataRow(LauncherColumnStrategy.Fixed5, 268d, 47.2d)]    // (268−32)/5
    [DataRow(LauncherColumnStrategy.Fixed6, 616d, 96d)]      // (616−40)/6＝与自适应同档同值
    [DataRow(LauncherColumnStrategy.Fixed6, 544d, 84d)]      // 6 列最小视口：瓦片宽恰=下限
    [DataRow(LauncherColumnStrategy.Fixed4, 0d, 0d)]         // 收起期视口 0：摊分式自然钳 0
    public void TileWidth_FixedColumns_DerivedBySpreadFormula(
        LauncherColumnStrategy strategy, double viewportWidth, double expected)
    {
        var columns = LauncherGridMath.ColumnCount(viewportWidth, strategy);
        Assert.AreEqual(expected, LauncherGridMath.TileWidth(viewportWidth, columns), 1e-9);
    }

    [TestMethod]
    [DataRow(LauncherColumnStrategy.Fixed3, 268d)]
    [DataRow(LauncherColumnStrategy.Fixed4, 286d)]
    [DataRow(LauncherColumnStrategy.Fixed5, 336d)]
    [DataRow(LauncherColumnStrategy.Fixed6, 544d)]
    public void RowLayout_FixedStrategy_FillsViewportWithNoDeadZone(
        LauncherColumnStrategy strategy, double viewportWidth)
    {
        var columns = LauncherGridMath.ColumnCount(viewportWidth, strategy);
        var tileWidth = LauncherGridMath.TileWidth(viewportWidth, columns);
        var rowWidth = columns * tileWidth + (columns - 1) * DesignTokens.LauncherTileGutterPx;
        Assert.AreEqual(viewportWidth, rowWidth, 1e-9, "固定档行满排总宽=视口（无死区不变）。");
    }
}
