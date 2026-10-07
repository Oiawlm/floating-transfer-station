using FloatingTransferStation.Design;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class WindowControllerTests
{
    [TestMethod]
    public void EdgeBleed_MatchesTheDwmCornerRadiusToken()
    {
        // 经运行时判定取值，锁定「可裁切量 == DWM 圆角半径 token == 8」的端到端数值。
        var allowedBleed = ScreenEdgeGeometry.RightEdgeBleedFor(
            new MonitorBounds(0, 0, 1920, 1040),
            new MonitorBounds(0, 0, 1920, 1040),
            [new MonitorBounds(0, 0, 1920, 1040)]);
        Assert.AreEqual((double)DesignTokens.DwmCornerRadius, allowedBleed);
        Assert.AreEqual(8d, allowedBleed);
    }

    [TestMethod]
    public void CollapsedPlacement_UsesOnlyTheDefaultCategoryRowAtRightWorkAreaEdge()
    {
        var workArea = new WorkArea(0, 0, 1920, 1040);
        var settings = new WindowSettings(360, 640, 80);

        foreach (var (category, expectedTop) in new[]
                 {
                     (BoardCategory.CustomerOriginal, 80d),
                     (BoardCategory.Reference, 240d),
                     (BoardCategory.Prompt, 400d),
                     (BoardCategory.Inbox, 560d)
                 })
        {
            var placement = WindowController.Collapsed(workArea, settings, category);

            Assert.AreEqual(1920 - WindowSettings.TabWidth, placement.Left);
            Assert.AreEqual(WindowSettings.TabWidth, placement.Width);
            Assert.AreEqual(expectedTop, placement.Top);
            Assert.AreEqual(160, placement.Height);
        }
    }

    [TestMethod]
    public void CollapsedPlacement_RejectsUndefinedDefaultCategory()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            WindowController.Collapsed(
                new WorkArea(0, 0, 1920, 1040),
                new WindowSettings(360, 640, 80),
                (BoardCategory)99));
    }

    [TestMethod]
    public void ExpandedPlacement_GrowsLeftAndKeepsRightEdgeFixed()
    {
        var placement = WindowController.Expanded(
            new WorkArea(0, 0, 1920, 1040),
            new WindowSettings(360, 640, 80));

        Assert.AreEqual(1502, placement.Left);
        Assert.AreEqual(418, placement.Width);
        Assert.AreEqual(1920, placement.Left + placement.Width);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void CategoryRailPlacement_PreservesEveryCollapsedDefaultCategoryRow()
    {
        var workArea = new WorkArea(120, 40, 1280, 720);
        var settings = new WindowSettings(5000, 5000, 5000);
        var normalized = settings.Normalize(workArea.Width, workArea.Height);

        var rail = WindowController.CategoryRail(workArea, settings);

        Assert.AreEqual(WindowSettings.TabWidth, rail.Width);
        Assert.AreEqual(workArea.Top + normalized.Top, rail.Top);
        Assert.AreEqual(normalized.WindowHeight, rail.Height);
        Assert.AreEqual(workArea.Right, rail.Left + rail.Width);

        var rowHeight = rail.Height / BoardCategoryCatalog.Ordered.Count;
        for (var index = 0; index < BoardCategoryCatalog.Ordered.Count; index++)
        {
            var category = BoardCategoryCatalog.Ordered[index];
            var collapsed = WindowController.Collapsed(workArea, settings, category);
            var railRow = new WindowPlacement(
                rail.Left,
                rail.Top + (index * rowHeight),
                rail.Width,
                rowHeight);

            Assert.AreEqual(collapsed, railRow, $"Default row moved for {category}.");
        }
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Placement_NormalizesOffscreenPersistedValues()
    {
        var placement = WindowController.Expanded(
            new WorkArea(0, 0, 1280, 720),
            new WindowSettings(5000, 5000, 5000));

        Assert.IsTrue(placement.Left >= 0);
        Assert.AreEqual(0, placement.Top);
        Assert.AreEqual(720, placement.Height);
        Assert.AreEqual(1280, placement.Left + placement.Width);
    }

    [TestMethod]
    public void CollapsedPlacement_BleedsTheWindowBeyondTheRightWorkAreaEdge()
    {
        var workArea = new WorkArea(0, 0, 1920, 1040);
        var settings = new WindowSettings(360, 640, 80);

        foreach (var (category, expectedTop) in new[]
                 {
                     (BoardCategory.CustomerOriginal, 80d),
                     (BoardCategory.Reference, 240d),
                     (BoardCategory.Prompt, 400d),
                     (BoardCategory.Inbox, 560d)
                 })
        {
            var placement = WindowController.Collapsed(
                workArea,
                settings,
                category,
                WindowSettings.EdgeBleed);

            Assert.AreEqual(1920 - WindowSettings.TabWidth, placement.Left, $"{category}: visible left edge moved.");
            Assert.AreEqual(WindowSettings.TabWidth + WindowSettings.EdgeBleed, placement.Width, $"{category}: width must include the bleed.");
            Assert.AreEqual(workArea.Right, placement.Left + placement.Width - WindowSettings.EdgeBleed, $"{category}: visible right edge must stay at the work area edge.");
            Assert.AreEqual(workArea.Right + WindowSettings.EdgeBleed, placement.Left + placement.Width, $"{category}: window right edge must bleed beyond the screen.");
            Assert.AreEqual(expectedTop, placement.Top);
            Assert.AreEqual(160, placement.Height);
        }
    }

    [TestMethod]
    public void ExpandedPlacement_BleedsTheWindowBeyondTheRightWorkAreaEdge()
    {
        var workArea = new WorkArea(0, 0, 1920, 1040);
        var placement = WindowController.Expanded(
            workArea,
            new WindowSettings(360, 640, 80),
            WindowSettings.EdgeBleed);

        Assert.AreEqual(1502, placement.Left);
        Assert.AreEqual(418 + WindowSettings.EdgeBleed, placement.Width);
        Assert.AreEqual(workArea.Right, placement.Left + placement.Width - WindowSettings.EdgeBleed);
        Assert.AreEqual(workArea.Right + WindowSettings.EdgeBleed, placement.Left + placement.Width);
    }

    [TestMethod]
    public void CategoryRailPlacement_BleedsTheWindowBeyondTheRightWorkAreaEdge()
    {
        var workArea = new WorkArea(120, 40, 1280, 720);
        var settings = new WindowSettings(5000, 5000, 5000);
        var rail = WindowController.CategoryRail(workArea, settings, WindowSettings.EdgeBleed);

        Assert.AreEqual(WindowSettings.TabWidth + WindowSettings.EdgeBleed, rail.Width);
        Assert.AreEqual(workArea.Right, rail.Left + rail.Width - WindowSettings.EdgeBleed);
        Assert.AreEqual(workArea.Right + WindowSettings.EdgeBleed, rail.Left + rail.Width);
    }

    [TestMethod]
    public void Placement_KeepsTheVisibleWidthWithinTheWorkAreaWhenBleeding()
    {
        var workArea = new WorkArea(0, 0, 100, 400);
        var placement = WindowController.Expanded(
            workArea,
            new WindowSettings(500, 300, 10),
            WindowSettings.EdgeBleed);

        Assert.IsTrue(placement.Left >= 0, "The visible width must never push the left edge off screen.");
        Assert.AreEqual(workArea.Right, placement.Left + placement.Width - WindowSettings.EdgeBleed);
        Assert.IsTrue(placement.Width - WindowSettings.EdgeBleed <= workArea.Width);
    }

    [TestMethod]
    public void Placement_IgnoresNegativeEdgeBleed()
    {
        var workArea = new WorkArea(0, 0, 1920, 1040);
        var placement = WindowController.Expanded(
            workArea,
            new WindowSettings(360, 640, 80),
            -12);

        Assert.AreEqual(418, placement.Width);
        Assert.AreEqual(1920, placement.Left + placement.Width);
    }

    [TestMethod]
    public void EdgeHidden_PlacesWindowBeyondEveryMonitorWithZeroIntersection()
    {
        // 单显示器：与「贴本屏右缘外移」等价（另有阴影余量）。
        var window = new PhysicalRectangle(1502, 80, 1928, 720);
        var hidden = WindowController.EdgeHidden(window, [new MonitorBounds(0, 0, 1920, 1080)]);

        Assert.AreEqual(window.Width, hidden.Width, "贴边隐藏是同尺寸纯移动。");
        Assert.AreEqual(window.Top, hidden.Top, "垂直位置不变。");
        Assert.AreEqual(window.Bottom, hidden.Bottom);
        Assert.AreEqual(1920 + WindowController.EdgeHideOffscreenClearancePx, hidden.Left);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void EdgeHidden_ClearsRightNeighborMonitorAndNegativeOriginScreens()
    {
        // 右邻显示器存在时，仅贴本屏右缘的滑出像素会被邻居接住；目标必须越过
        // 所有显示器的最右缘。虚拟屏原点为负（左侧主屏外接副屏）同理。
        var window = new PhysicalRectangle(-418, 80, 0, 720);
        var monitors = new[]
        {
            new MonitorBounds(-1920, 0, 0, 1080),
            new MonitorBounds(0, 0, 1920, 1080),
            new MonitorBounds(1920, -300, 3840, 780)
        };

        var hidden = WindowController.EdgeHidden(window, monitors);

        Assert.AreEqual(3840 + WindowController.EdgeHideOffscreenClearancePx, hidden.Left);
        Assert.AreEqual(window.Width, hidden.Width);
        foreach (var monitor in monitors)
        {
            Assert.IsTrue(
                hidden.Left >= monitor.Right || hidden.Right <= monitor.Left,
                $"隐藏矩形 {hidden} 与显示器 {monitor} 不得相交。");
        }
    }

    [TestMethod]
    public void EdgeHidden_RejectsEmptyMonitorList()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            WindowController.EdgeHidden(
                new PhysicalRectangle(0, 0, 100, 100),
                Array.Empty<MonitorBounds>()));
    }

    [TestMethod]
    public void EdgeRecallZone_InflatesVisibleRectangleByToleranceInPhysicalPixels()
    {
        var visibleAtHide = new PhysicalRectangle(100, 200, 400, 600);

        var zone = WindowController.EdgeRecallZone(visibleAtHide, tolerance: 8);

        Assert.AreEqual(new PhysicalRectangle(92, 192, 408, 608), zone);
        // 光标比对物理像素直比：边界含左上、不含右下（与 Win32 矩形语义一致）。
        Assert.IsTrue(zone.Contains(92, 192));
        Assert.IsTrue(zone.Contains(407, 607));
        Assert.IsFalse(zone.Contains(408, 607));
        Assert.IsFalse(zone.Contains(407, 608));
        Assert.IsFalse(zone.Contains(91, 192), "回位区外的点不得命中。");
    }
}
