using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.ViewModels;

/// <summary>内容搜索的类型筛选(2026-09-25 设计草案切片 1):两个 chip 的组合语义。</summary>
public enum SearchTypeFilter
{
    All = 0,
    Images = 1,
    TextOnly = 2
}

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly DefaultCaptureCategoryState _defaultCaptureCategory;
    private CategoryViewModel? _activePanel;
    private CategoryViewModel _defaultCapturePanel;
    private bool _isExternalDropRailVisible;
    private bool _isPanelExpanded;
    private bool _isSearchActive;
    private string _searchText = string.Empty;
    private SearchTypeFilter _searchType;
    private bool _shortCardNotesEnabled;
    private string _statusText = string.Empty;

    public MainWindowViewModel(
        BoardService board,
        DefaultCaptureCategoryState? defaultCaptureCategory = null)
        : this(board, WindowSettings.Default, defaultCaptureCategory)
    {
    }

    public MainWindowViewModel(
        BoardService board,
        WindowSettings settings,
        DefaultCaptureCategoryState? defaultCaptureCategory = null)
    {
        _defaultCaptureCategory = defaultCaptureCategory ?? new DefaultCaptureCategoryState();
        // 装载期修复（#30）：默认接收只能落在内容桶——历史会话态指向非内容桶
        // （如旧版本设到 Prompt 槽位）时归位 Inbox，保证收起把手与 ◎ 标记不落在
        // 复盘/启动标签上。默认接收无持久化（装载期必为 Inbox），此处是纵深。
        if (!BoardCategoryCatalog.IsContentBucket(_defaultCaptureCategory.Current))
        {
            _defaultCaptureCategory.Set(BoardCategory.Inbox);
        }

        _categories = BoardCategoryCatalog.Ordered
            .Select(category => new CategoryViewModel(
                category,
                board.Items(category),
                settings.CategoryName(category)))
            .ToArray();
        ApplyCategoryOrder(settings.DisplayOrder);
        _defaultCapturePanel = Categories.Single(
            category => category.Category == _defaultCaptureCategory.Current);
        _defaultCapturePanel.IsDefaultCapture = true;
    }

    private CategoryViewModel[] _categories;

    public IReadOnlyList<CategoryViewModel> Categories => _categories;

    /// <summary>
    /// 按显示顺序重排标签集合（1.25.0 设置页标签顺序）：复用既有
    /// <see cref="CategoryViewModel"/> 实例（实例身份被 ActivePanel/默认接收/复盘
    /// 表面切换依赖，绝不新建）；数组实例随重排替换并通知标签轨刷新。非法顺序
    /// （数量/成员/重复不符）拒绝重排、保持现状。
    /// </summary>
    public void ApplyCategoryOrder(IReadOnlyList<BoardCategory> displayOrder)
    {
        ArgumentNullException.ThrowIfNull(displayOrder);
        if (displayOrder.Count != _categories.Length ||
            displayOrder.Distinct().Count() != displayOrder.Count ||
            displayOrder.Any(category => !BoardCategoryCatalog.IsDefined(category)))
        {
            return;
        }

        _categories = displayOrder
            .Select(category => _categories.Single(panel => panel.Category == category))
            .ToArray();
        OnPropertyChanged(nameof(Categories));
    }

    public CategoryViewModel DefaultCapturePanel
    {
        get => _defaultCapturePanel;
        private set => SetProperty(ref _defaultCapturePanel, value);
    }

    public CategoryViewModel? ActivePanel
    {
        get => _activePanel;
        private set => SetProperty(ref _activePanel, value);
    }

    public bool IsPanelExpanded
    {
        get => _isPanelExpanded;
        private set => SetProperty(ref _isPanelExpanded, value);
    }

    /// <summary>搜索态：头部操作区切换为搜索条，过滤当前分类的只读视图；退出即还原。</summary>
    public bool IsSearchActive
    {
        get => _isSearchActive;
        private set => SetProperty(ref _isSearchActive, value);
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public SearchTypeFilter SearchType
    {
        get => _searchType;
        set => SetProperty(ref _searchType, value);
    }

    /// <summary>短卡片备注开关（#22 2026-10-22 修订，Issue #211）：开启后备注添加
    /// 入口对未截断的文字卡也出现。由 ApplyPreferences 唯一管线喂值（契约 #5），
    /// 卡片模板触发器经窗口祖先 DataContext 绑定消费（照 IsSearchActive 先例）。</summary>
    public bool ShortCardNotesEnabled
    {
        get => _shortCardNotesEnabled;
        set => SetProperty(ref _shortCardNotesEnabled, value);
    }

    /// <summary>进入搜索态(窗口层负责应用过滤与焦点);复盘标签不参与(冻结)。</summary>
    public void EnterSearch()
    {
        IsSearchActive = true;
    }

    /// <summary>退出搜索态并清空条件;窗口层负责还原列表。</summary>
    public void ExitSearch()
    {
        if (!IsSearchActive)
        {
            return;
        }

        IsSearchActive = false;
        SearchText = string.Empty;
        SearchType = SearchTypeFilter.All;
    }

    public bool IsExternalDropRailVisible => _isExternalDropRailVisible;

    public bool IsCategoryRailVisible => IsPanelExpanded || IsExternalDropRailVisible;

    public bool IsCollapsedCategoryHandleVisible =>
        !IsPanelExpanded && !IsExternalDropRailVisible;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public void Activate(BoardCategory category)
    {
        var activePanel = Categories.Single(panel => panel.Category == category);
        foreach (var panel in Categories)
        {
            panel.IsActive = IsPanelExpanded && ReferenceEquals(panel, activePanel);
        }

        ActivePanel = activePanel;
    }

    public void SetPanelExpanded(bool isExpanded)
    {
        var stateChanged = IsPanelExpanded != isExpanded;
        IsPanelExpanded = isExpanded;
        if (stateChanged)
        {
            OnPropertyChanged(nameof(IsCategoryRailVisible));
            OnPropertyChanged(nameof(IsCollapsedCategoryHandleVisible));
        }

        foreach (var panel in Categories)
        {
            panel.IsActive = isExpanded && ReferenceEquals(panel, ActivePanel);
        }
    }

    public void SetExternalDropRailVisible(bool isVisible)
    {
        if (!SetProperty(ref _isExternalDropRailVisible, isVisible, nameof(IsExternalDropRailVisible)))
        {
            return;
        }

        OnPropertyChanged(nameof(IsCategoryRailVisible));
        OnPropertyChanged(nameof(IsCollapsedCategoryHandleVisible));
    }

    public bool SetDefaultCaptureCategory(BoardCategory category)
    {
        if (!_defaultCaptureCategory.Set(category))
        {
            return false;
        }

        var selected = Categories.Single(panel => panel.Category == category);
        foreach (var panel in Categories)
        {
            panel.IsDefaultCapture = ReferenceEquals(panel, selected);
        }

        DefaultCapturePanel = selected;
        return true;
    }

    public void ShowStatus(string message) => StatusText = message;

    public void ClearStatus() => StatusText = string.Empty;

    public void BeginCategoryNameEdit(CategoryViewModel category) =>
        category.BeginNameEdit();

    public string EndCategoryNameEdit(CategoryViewModel category, string draftName) =>
        category.EndNameEdit(draftName);

    public void CancelCategoryNameEdit(CategoryViewModel category) =>
        category.CancelNameEdit();

    public void ApplyCategoryName(CategoryViewModel category, string name) =>
        category.ApplyDisplayName(name);
}
