using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public sealed record BatchDragPlan(
    IReadOnlyList<BoardItem> Items,
    bool ClearExistingSelection);

public static class BatchDragPlanner
{
    public static BatchDragPlan Create(
        IReadOnlyList<BoardItem> sourceItems,
        IReadOnlyCollection<Guid> selectedIds,
        Guid dragOriginId)
    {
        ArgumentNullException.ThrowIfNull(sourceItems);
        ArgumentNullException.ThrowIfNull(selectedIds);
        var origin = sourceItems.SingleOrDefault(item => item.Id == dragOriginId)
            ?? throw new KeyNotFoundException($"Board item {dragOriginId} was not found.");
        var selected = selectedIds.ToHashSet();
        if (!selected.Contains(dragOriginId))
        {
            return new BatchDragPlan([origin], selected.Count > 0);
        }

        return new BatchDragPlan(
            sourceItems.Where(item => selected.Contains(item.Id)).ToArray(),
            false);
    }
}
