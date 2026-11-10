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

        Assert.AreEqual(1920 - WindowSettings.TabWidth - 360, placement.Left);
        Assert.AreEqual(360 + WindowSettings.TabWidth, placement.Width);
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
    public void EdgePeekPlacement_KeepsHandleWidthIntersectionWithWorkArea()
    {
        // R12 peek（契约 #17）：整窗同尺寸纯平移收缩，屏内仅留把手。
        var window = new PhysicalRectangle(1502, 80, 1928, 720);
        var workArea = new PhysicalRectangle(0, 0, 1920, 1080);

        var peek = WindowController.EdgePeekPlacement(window, workArea, handleWidthPx: 16);

        Assert.AreEqual(window.Width, peek.Width, "peek 收缩是同尺寸纯移动。");
        Assert.AreEqual(window.Top, peek.Top, "垂直位置不变。");
        Assert.AreEqual(window.Bottom, peek.Bottom);
        Assert.AreEqual(workArea.Right - 16, peek.Left, "把手左缘锚定工作区右缘内缩把手宽。");
        var intersectionWidth =
            Math.Min(peek.Right, workArea.Right) - Math.Max(peek.Left, workArea.Left);
        Assert.AreEqual(16, intersectionWidth, "与工作区交集宽恰等于把手宽。");
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void EdgePeekPlacement_AnchorsToGivenWorkAreaNotGlobalRightEdge()
    {
        // 多显示器：锚定收缩时刻所在工作区右缘（传入参数），而非全部显示器最右缘——
        // 右邻屏存在时滑出像素被邻居接住是允许的（把手在本屏右缘，空间记忆锚点）。
        // 虚拟屏原点为负（左侧主屏外接副屏）同理。
        var window = new PhysicalRectangle(-418, 80, 0, 720);
        var leftWorkArea = new PhysicalRectangle(-1920, 0, 0, 1080);

        var peek = WindowController.EdgePeekPlacement(window, leftWorkArea, handleWidthPx: 16);

        Assert.AreEqual(-16, peek.Left);
        Assert.AreEqual(window.Width, peek.Width);
        var intersectionWidth =
            Math.Min(peek.Right, leftWorkArea.Right) - Math.Max(peek.Left, leftWorkArea.Left);
        Assert.AreEqual(16, intersectionWidth);
    }

    [TestMethod]
    public void EdgePeekPlacement_ClampsDegenerateWindowNarrowerThanHandle()
    {
        // 退化输入：窗口比把手还窄 → 全窗留在屏内，交集宽＝窗口宽（钳制为更小者）。
        var window = new PhysicalRectangle(1904, 80, 1908, 720);
        var workArea = new PhysicalRectangle(0, 0, 1920, 1080);

        var peek = WindowController.EdgePeekPlacement(window, workArea, handleWidthPx: 16);

        Assert.AreEqual(workArea.Right - window.Width, peek.Left);
        Assert.AreEqual(
            window.Width,
            Math.Min(peek.Right, workArea.Right) - Math.Max(peek.Left, workArea.Left),
            "把手宽大于窗口宽时交集宽钳制为窗口宽。");
    }
}
