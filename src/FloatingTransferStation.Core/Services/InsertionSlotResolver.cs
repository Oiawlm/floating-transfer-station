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
    /// <summary>
    /// 槽位翻转迟滞半径（DIP）：光标越过翻转中线不足此值时维持既有槽位。
    /// 取 3 覆盖指针在卡片中线附近 ±1–2 DIP 的常见抖动（指示线稳定不跳变），
    /// 又远小于卡片高度——果断移动仍即时跟随，迟滞不构成可感知延迟。
    /// </summary>
    public const double SlotFlipHysteresisDip = 3;

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
    /// 对 <see cref="Resolve"/> 的结果应用翻转迟滞（纯几何，无隐藏状态）：
    /// 光标在相邻槽位的翻转中线（较小索引条目的中线）两侧
    /// ±<see cref="SlotFlipHysteresisDip"/> 带内抖动时维持上一有效槽位，
    /// 越过迟滞带、跨多槽跳变（快速移动/滚动后的大步迁移）直接跟随本次解析。
    /// heldIndex 为空或相等、spans 为空、边界条目未实现时原样返回 resolvedIndex
    /// ——迟滞绝不制造 Resolve 之外的索引，落点语义对带外位置零变化。
    /// </summary>
    public static int ResolveHeldSlot(
        IReadOnlyList<InsertionItemSpan> spans,
        double cursorY,
        int resolvedIndex,
        int? heldIndex)
    {
        ArgumentNullException.ThrowIfNull(spans);
        if (heldIndex is not { } held || held == resolvedIndex || spans.Count == 0)
        {
            return resolvedIndex;
        }

        if (Math.Abs(resolvedIndex - held) != 1)
        {
            // 相邻槽位之外的翻转来自果断移动或跨滚动区跳变，不经迟滞。
            return resolvedIndex;
        }

        var boundaryIndex = Math.Min(held, resolvedIndex);
        double? boundaryY = null;
        foreach (var span in spans)
        {
            if (span.ItemIndex == boundaryIndex)
            {
                boundaryY = span.CenterY;
                break;
            }
        }

        if (boundaryY is not { } boundary)
        {
            return resolvedIndex;
        }

        return resolvedIndex > held
            ? (cursorY >= boundary + SlotFlipHysteresisDip ? resolvedIndex : held)
            : (cursorY <= boundary - SlotFlipHysteresisDip ? resolvedIndex : held);
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
