namespace FloatingTransferStation.Services;

/// <summary>
/// 已实现（realized）列表条目在列表坐标系中的纵向边缘矩形。
/// </summary>
public readonly record struct InsertionItemSpan(int ItemIndex, double TopEdge, double BottomEdge)
{
    public double CenterY => (TopEdge + BottomEdge) / 2;
}

/// <summary>
/// 插入槽位：索引为「移除被拖项之前」的语义（与 <see cref="BoardService.MoveMany"/>
/// 的 preRemovalIndex 对齐），指示条贴靠该槽拥有条目的上缘（末尾槽为末项下缘）。
/// </summary>
public readonly record struct InsertionSlot(int InsertionIndex, double IndicatorEdgeY);

/// <summary>
/// 把光标 Y 解析为插入槽位的纯几何函数：槽位在每个条目的竖直中线处翻转，
/// 因此插入索引随光标下移单调不减；相邻条目的间隙整体归属其后的槽。
/// 分区（置顶/普通）合法性不由本类判定，由调用方按置顶边界钳制表达。
/// </summary>
public static class InsertionSlotResolver
{
    /// <summary>按条目中线判定光标所属槽位；空输入返回 (0, 0)。</summary>
    public static InsertionSlot Resolve(IReadOnlyList<InsertionItemSpan> spans, double cursorY)
    {
        ArgumentNullException.ThrowIfNull(spans);
        if (spans.Count == 0)
        {
            return new InsertionSlot(0, 0d);
        }

        foreach (var span in SortByTopEdge(spans))
        {
            // 中线本身归下半区，与 DropInsertionCalculator.ForTarget 的既有语义一致。
            if (cursorY < span.CenterY)
            {
                return new InsertionSlot(span.ItemIndex, span.TopEdge);
            }
        }

        var last = spans[spans.Count - 1];
        return new InsertionSlot(checked(last.ItemIndex + 1), last.BottomEdge);
    }

    /// <summary>
    /// 把置顶分区表达为槽位边界：置顶批量向下越界时收回到 pinnedCount，
    /// 普通批量向上越界时顶到 pinnedCount；混合置顶批量不属于本方法职责。
    /// </summary>
    public static int ClampInsertionIndex(
        int insertionIndex,
        int pinnedCount,
        bool dragBatchIsPinned)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pinnedCount);
        var clamped = dragBatchIsPinned
            ? Math.Min(insertionIndex, pinnedCount)
            : Math.Max(insertionIndex, pinnedCount);
        return Math.Max(0, clamped);
    }

    /// <summary>把槽位索引映射回指示条边缘：找首个索引不小于它的条目取其上缘，越界取末项下缘。</summary>
    public static double IndicatorEdgeY(IReadOnlyList<InsertionItemSpan> spans, int insertionIndex)
    {
        ArgumentNullException.ThrowIfNull(spans);
        if (spans.Count == 0)
        {
            return 0d;
        }

        foreach (var span in SortByTopEdge(spans))
        {
            if (insertionIndex <= span.ItemIndex)
            {
                return span.TopEdge;
            }
        }

        return spans[spans.Count - 1].BottomEdge;
    }

    private static InsertionItemSpan[] SortByTopEdge(IReadOnlyList<InsertionItemSpan> spans)
    {
        // 已实现容器通常已按顺序排列；排序是防御（每次 DragOver 重新枚举，开销按已实现数计）。
        var sorted = spans.ToArray();
        Array.Sort(sorted, (left, right) => left.TopEdge.CompareTo(right.TopEdge));
        return sorted;
    }
}
