using System.Collections.ObjectModel;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.ViewModels;

public sealed class CategoryViewModel : ObservableObject
{
    private bool _isActive;
    private bool _isDefaultCapture;
    private bool _isDropTarget;
    private string _displayName;
    private string _draftName;
    private bool _isEditingName;

    public CategoryViewModel(
        BoardCategory category,
        ObservableCollection<BoardItem> items,
        string? displayName = null)
    {
        Category = category;
        _displayName = displayName ?? BoardCategoryCatalog.DisplayName(category);
        _draftName = _displayName;
        Items = items;
    }

    public BoardCategory Category { get; }
    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public string DraftName
    {
        get => _draftName;
        set => SetProperty(ref _draftName, BoardCategoryCatalog.LimitDisplayName(value));
    }

    public bool IsEditingName
    {
        get => _isEditingName;
        private set => SetProperty(ref _isEditingName, value);
    }

    public ObservableCollection<BoardItem> Items { get; }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => SetProperty(ref _isDropTarget, value);
    }

    public bool IsDefaultCapture
    {
        get => _isDefaultCapture;
        set => SetProperty(ref _isDefaultCapture, value);
    }

    public void BeginNameEdit()
    {
        DraftName = DisplayName;
        IsEditingName = true;
    }

    public string EndNameEdit(string draftName)
    {
        DraftName = draftName;
        IsEditingName = false;
        return DraftName;
    }

    public void CancelNameEdit()
    {
        DraftName = DisplayName;
        IsEditingName = false;
    }

    public void ApplyDisplayName(string name)
    {
        DisplayName = name;
        DraftName = name;
    }
}
