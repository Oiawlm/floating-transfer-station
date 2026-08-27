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
}
