using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class InsertionSlotResolverTests
{
    // 相邻容器在 VirtualizingStackPanel 中连续（卡片 Margin 在 ListBoxItem 内），
    // 但解析器按传入边缘矩形工作，这里用带间隙的样例同时覆盖间隙命中的行为。
    private static readonly InsertionItemSpan[] ThreeItemsWithGaps =
    [
        new InsertionItemSpan(0, 4, 104),
        new InsertionItemSpan(1, 106, 206),
        new InsertionItemSpan(2, 208, 308)
    ];

    [TestMethod]
    public void Resolve_EmptySpansReturnsLeadingSlot()
    {
        var slot = InsertionSlotResolver.Resolve([], 123d);

        Assert.AreEqual(0, slot.InsertionIndex);
        Assert.AreEqual(0d, slot.IndicatorEdgeY);
    }

    [TestMethod]
    public void Resolve_InsertionIndexNeverDecreasesAsCursorMovesDown()
    {
        double previousIndex = -1;
        for (var cursorY = -20d; cursorY <= 330d; cursorY += 0.5)
        {
            var slot = InsertionSlotResolver.Resolve(ThreeItemsWithGaps, cursorY);
            Assert.IsGreaterThanOrEqualTo(previousIndex, (double)slot.InsertionIndex);
            previousIndex = slot.InsertionIndex;
        }

        Assert.AreEqual(3d, previousIndex, "越过末项中线后必须到达末尾槽。");
    }

    [TestMethod]
    public void Resolve_FlipsAtEachItemCenter()
    {
        Assert.AreEqual(
            0,
            InsertionSlotResolver.Resolve(ThreeItemsWithGaps, 53.9).InsertionIndex);
        Assert.AreEqual(
            1,
            InsertionSlotResolver.Resolve(ThreeItemsWithGaps, 54.1).InsertionIndex);
        Assert.AreEqual(
            1,
            InsertionSlotResolver.Resolve(ThreeItemsWithGaps, 155.9).InsertionIndex);
        Assert.AreEqual(
            2,
            InsertionSlotResolver.Resolve(ThreeItemsWithGaps, 156.1).InsertionIndex);
        // 中线本身归下半区，与 DropInsertionCalculator.ForTarget 的既有语义一致。
        Assert.AreEqual(
            1,
            InsertionSlotResolver.Resolve(ThreeItemsWithGaps, 54d).InsertionIndex);
    }

    [TestMethod]
    public void Resolve_AboveFirstAndBelowLastUseTheOuterSlots()
    {
        var above = InsertionSlotResolver.Resolve(ThreeItemsWithGaps, -50d);
        Assert.AreEqual(0, above.InsertionIndex);
        Assert.AreEqual(4d, above.IndicatorEdgeY);

        var below = InsertionSlotResolver.Resolve(ThreeItemsWithGaps, 400d);
        Assert.AreEqual(3, below.InsertionIndex);
        Assert.AreEqual(308d, below.IndicatorEdgeY);
    }

    [TestMethod]
    public void Resolve_CursorInsideACardGapStaysOnTheNeighboringSlot()
    {
        var slot = InsertionSlotResolver.Resolve(ThreeItemsWithGaps, 105d);

        Assert.AreEqual(1, slot.InsertionIndex);
        Assert.AreEqual(106d, slot.IndicatorEdgeY);
    }

    [TestMethod]
    public void Resolve_IndicatorEdgeIsTheOwningItemTopEdge()
    {
        foreach (var (cursorY, expectedIndex, expectedEdge) in new[]
                 {
                     (10d, 0, 4d),
                     (60d, 1, 106d),
                     (300d, 3, 308d)
                 })
        {
            var slot = InsertionSlotResolver.Resolve(ThreeItemsWithGaps, cursorY);
            Assert.AreEqual(expectedIndex, slot.InsertionIndex);
            Assert.AreEqual(expectedEdge, slot.IndicatorEdgeY);
        }
    }

    [TestMethod]
    public void Resolve_SortsSpansDefensivelyBeforeResolving()
    {
        var shuffled = new[]
        {
            ThreeItemsWithGaps[2],
            ThreeItemsWithGaps[0],
            ThreeItemsWithGaps[1]
        };

        var slot = InsertionSlotResolver.Resolve(shuffled, 60d);

        Assert.AreEqual(1, slot.InsertionIndex);
        Assert.AreEqual(106d, slot.IndicatorEdgeY);
    }

    [TestMethod]
    [DataRow(0, 0, false, 0)]
    [DataRow(0, 2, true, 0)]
    [DataRow(1, 2, true, 1)]
    [DataRow(5, 2, true, 2)]
    [DataRow(4, 2, false, 4)]
    [DataRow(0, 2, false, 2)]
    [DataRow(-3, 2, true, 0)]
    [DataRow(9, 9, true, 9)]
    [DataRow(12, 9, false, 12)]
    public void ClampInsertionIndex_UsesThePinnedBoundaryAsTheLimit(
        int insertionIndex,
        int pinnedCount,
        bool dragBatchIsPinned,
        int expected)
    {
        Assert.AreEqual(
            expected,
            InsertionSlotResolver.ClampInsertionIndex(
                insertionIndex,
                pinnedCount,
                dragBatchIsPinned));
    }

    [TestMethod]
    public void ClampInsertionIndex_RejectsNegativePinnedCount()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => InsertionSlotResolver.ClampInsertionIndex(0, -1, false));
    }

    [TestMethod]
    public void IndicatorEdgeY_MapsAnySlotBackToItsEdge()
    {
        Assert.AreEqual(4d, InsertionSlotResolver.IndicatorEdgeY(ThreeItemsWithGaps, -1));
        Assert.AreEqual(4d, InsertionSlotResolver.IndicatorEdgeY(ThreeItemsWithGaps, 0));
        Assert.AreEqual(106d, InsertionSlotResolver.IndicatorEdgeY(ThreeItemsWithGaps, 1));
        Assert.AreEqual(208d, InsertionSlotResolver.IndicatorEdgeY(ThreeItemsWithGaps, 2));
        Assert.AreEqual(308d, InsertionSlotResolver.IndicatorEdgeY(ThreeItemsWithGaps, 3));
        Assert.AreEqual(308d, InsertionSlotResolver.IndicatorEdgeY(ThreeItemsWithGaps, 99));
        Assert.AreEqual(0d, InsertionSlotResolver.IndicatorEdgeY([], 0));
    }

    // 中线（54/156）与迟滞半径（3）共同决定翻转带：中线 ±3 DIP 内维持既有槽位。
    // 条目 0/1/2 的翻转中线分别是 54、156、258。

    [TestMethod]
    public void ResolveHeldSlot_KeepsHeldSlotWhileCursorJittersAroundTheFlipLine()
    {
        // held=1，光标越过条目 1 中线（156）不足迟滞半径：维持 1（下行方向）。
        Assert.AreEqual(1, InsertionSlotResolver.ResolveHeldSlot(ThreeItemsWithGaps, 157d, 2, 1));
        Assert.AreEqual(1, InsertionSlotResolver.ResolveHeldSlot(ThreeItemsWithGaps, 158d, 2, 1));
        // 越过中线 + 迟滞半径（≥159）才翻转。
        Assert.AreEqual(2, InsertionSlotResolver.ResolveHeldSlot(ThreeItemsWithGaps, 159d, 2, 1));

        // held=2，光标退回中线之上不足半径：维持 2（上行方向，迟滞对称）。
        Assert.AreEqual(2, InsertionSlotResolver.ResolveHeldSlot(ThreeItemsWithGaps, 155d, 1, 2));
        Assert.AreEqual(2, InsertionSlotResolver.ResolveHeldSlot(ThreeItemsWithGaps, 154d, 1, 2));
        Assert.AreEqual(1, InsertionSlotResolver.ResolveHeldSlot(ThreeItemsWithGaps, 153d, 1, 2));
    }

    [TestMethod]
    public void ResolveHeldSlot_NullOrEqualHeldFollowsResolution()
    {
        Assert.AreEqual(2, InsertionSlotResolver.ResolveHeldSlot(ThreeItemsWithGaps, 156.5d, 2, null));
        Assert.AreEqual(2, InsertionSlotResolver.ResolveHeldSlot(ThreeItemsWithGaps, 156.5d, 2, 2));
    }

    [TestMethod]
    public void ResolveHeldSlot_MultiSlotJumpFollowsResolutionWithoutHysteresis()
    {
        // 跨多槽跳变（快速移动/滚动后迁移）不经迟滞：既不可能由中线抖动产生，
        // 也不应被迟滞带拦下。
        Assert.AreEqual(3, InsertionSlotResolver.ResolveHeldSlot(ThreeItemsWithGaps, 300d, 3, 1));
        Assert.AreEqual(0, InsertionSlotResolver.ResolveHeldSlot(ThreeItemsWithGaps, -10d, 0, 2));
    }

    [TestMethod]
    public void ResolveHeldSlot_MissingBoundaryItemOrEmptySpansFollowResolution()
    {
        // 边界条目未实现（回收容器）或 spans 为空：迟滞绝不制造 Resolve 之外的索引。
        var sparse = new[] { ThreeItemsWithGaps[0], ThreeItemsWithGaps[2] };
        Assert.AreEqual(2, InsertionSlotResolver.ResolveHeldSlot(sparse, 156.5d, 2, 1));
        Assert.AreEqual(1, InsertionSlotResolver.ResolveHeldSlot([], 156.5d, 1, 2));
    }
}
