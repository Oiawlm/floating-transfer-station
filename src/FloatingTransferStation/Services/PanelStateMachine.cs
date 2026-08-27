using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public sealed class PanelStateMachine
{
    private bool _pointerInside;
    private bool _dragInProgress;

    public BoardCategory? ActiveCategory { get; private set; }
    public BoardCategory? PendingCategory { get; private set; }
    public bool IsDragInProgress => _dragInProgress;
    public bool IsExpanded { get; private set; }

    public void BeginHover(BoardCategory category)
    {
        Validate(category);
        _pointerInside = true;
        PendingCategory = category;
    }

    public bool TryCancelHover(BoardCategory category)
    {
        Validate(category);
        if (PendingCategory != category)
        {
            return false;
        }

        PendingCategory = null;
        return true;
    }

    public bool TryCommitHover(out BoardCategory category)
    {
        if (!_pointerInside || PendingCategory is not { } pending)
        {
            category = default;
            return false;
        }

        category = pending;
        Switch(pending);
        return true;
    }

    public void Switch(BoardCategory category)
    {
        Validate(category);
        ActiveCategory = category;
        PendingCategory = null;
        IsExpanded = true;
    }

    public void EnterSurface() => _pointerInside = true;

    public void LeaveSurface()
    {
        _pointerInside = false;
        PendingCategory = null;
    }

    public void BeginDrag() => _dragInProgress = true;

    public void EndDrag() => _dragInProgress = false;

    public bool TryCollapse()
    {
        if (_pointerInside || _dragInProgress || !IsExpanded)
        {
            return false;
        }

        IsExpanded = false;
        return true;
    }

    private static void Validate(BoardCategory category)
    {
        if (!BoardCategoryCatalog.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }
    }
}
