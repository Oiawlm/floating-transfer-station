using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public sealed class PanelStateMachine
{
    private bool _pointerInside;
    private bool _dragInProgress;
    private bool _textEditingActive;

    public BoardCategory? ActiveCategory { get; private set; }
    public BoardCategory? PendingCategory { get; private set; }
    public bool IsDragInProgress => _dragInProgress;
    public bool IsTextEditingActive => _textEditingActive;
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

    /// <summary>面板内文本编辑控件获得键盘焦点期间，收起被显式抑制（IME 候选窗会补发假的指针离开）。</summary>
    public void BeginTextEditing() => _textEditingActive = true;

    public void EndTextEditing() => _textEditingActive = false;

    public void CollapseForExternalDrop()
    {
        PendingCategory = null;
        IsExpanded = false;
    }

    public bool TryCollapse()
    {
        if (_pointerInside || _dragInProgress || _textEditingActive || !IsExpanded)
        {
            return false;
        }

        IsExpanded = false;
        return true;
    }

    /// <summary>只读探针：当前条件若调用 TryCollapse 是否会提交收起，不改变状态。</summary>
    public bool WouldCollapse => !_pointerInside && !_dragInProgress && !_textEditingActive && IsExpanded;

    private static void Validate(BoardCategory category)
    {
        if (!BoardCategoryCatalog.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }
    }
}
