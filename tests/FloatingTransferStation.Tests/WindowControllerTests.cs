using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class WindowControllerTests
{
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
    public void CollapsedPlacement_FollowsCustomCategoryDisplayOrder()
    {
        // 1.25.0 标签顺序：收起把手行号 = 分类在显示顺序（settings.DisplayOrder）中的
        // 位置，不再假设目录默认序。
        var workArea = new WorkArea(0, 0, 1920, 1040);
        var settings = new WindowSettings(360, 640, 80)
        {
            CategoryOrder = new[]
            {
                BoardCategory.CustomerOriginal,
                BoardCategory.Inbox,
                BoardCategory.Reference,
                BoardCategory.Prompt
            }
        };

        Assert.AreEqual(80d, WindowController.Collapsed(workArea, settings, BoardCategory.CustomerOriginal).Top);
        Assert.AreEqual(80d + 160, WindowController.Collapsed(workArea, settings, BoardCategory.Inbox).Top);
        Assert.AreEqual(80d + (2 * 160), WindowController.Collapsed(workArea, settings, BoardCategory.Reference).Top);
        Assert.AreEqual(80d + (3 * 160), WindowController.Collapsed(workArea, settings, BoardCategory.Prompt).Top);
    }

    [TestMethod]
    public void CollapsedPlacement_InvalidCategoryOrderFallsBackToCatalogRows()
    {
        var workArea = new WorkArea(0, 0, 1920, 1040);
        var settings = new WindowSettings(360, 640, 80)
        {
            CategoryOrder = new[]
            {
                BoardCategory.Prompt,
                BoardCategory.Prompt,
                BoardCategory.Reference,
                BoardCategory.Inbox
            }
        };

        Assert.AreEqual(240d, WindowController.Collapsed(workArea, settings, BoardCategory.Reference).Top);
        Assert.AreEqual(400d, WindowController.Collapsed(workArea, settings, BoardCategory.Prompt).Top);
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
    public void Placement_ClampsTheWindowWidthToTheWorkArea()
    {
        // 可见态窗口完整落在工作区内：极窄工作区下宽度钳制到工作区宽度，左缘不出界。
        var workArea = new WorkArea(0, 0, 100, 400);
        var placement = WindowController.Expanded(
            workArea,
            new WindowSettings(500, 300, 10));

        Assert.AreEqual(workArea.Width, placement.Width);
        Assert.AreEqual(workArea.Right, placement.Left + placement.Width);
        Assert.IsTrue(placement.Left >= workArea.Left);
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
