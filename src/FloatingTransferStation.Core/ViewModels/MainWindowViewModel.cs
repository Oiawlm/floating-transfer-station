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
    private SubgroupDisplayMode _subgroupDisplay;
    private bool _shortCardNotesEnabled;
    private string _statusText = string.Empty;
    private bool _aiOrganizeEnabled = true;
    private bool _hasEnabledPluginActions;

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
        // （如旧版本设到 Prompt 槽位）时归位 Inbox，保证收起把手与默认接收标记
        // （靶心 Path 图标，2026-11-03 前为字符「◎」）不落在复盘/启动标签上。
        // 默认接收无持久化（装载期必为 Inbox），此处是纵深。
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
        SubgroupChips = Enumerable.Range(0, SubgroupCatalog.SlotCount)
            .Select(slot => new SubgroupChipViewModel(slot))
            .ToArray();
    }

    /// <summary>
    /// 子分组按钮轨 chip 集（契约 #57，形态①）：固定 4 槽实例（槽号即身份），
    /// 显示名由窗口层按名清单逐槽解析喂值（<see cref="RefreshSubgroupChips"/>）。
    /// </summary>
    public IReadOnlyList<SubgroupChipViewModel> SubgroupChips { get; }

    /// <summary>
    /// 子分组呈现模式（契约 #57 三态，默认 Off）：由 ApplyPreferences 唯一管线
    /// 喂值（照 ShortCardNotesEnabled 先例）；变化时连带通知轨可见性。
    /// </summary>
    public SubgroupDisplayMode SubgroupDisplay
    {
        get => _subgroupDisplay;
        set
        {
            if (SetProperty(ref _subgroupDisplay, value))
            {
                OnPropertyChanged(nameof(IsSubgroupRailVisible));
            }
        }
    }

    /// <summary>按钮轨可见性（契约 #57 作用域）：ChipRail 模式且激活面板为内容桶
    /// （复盘/启动表面零呈现——轨不出现）；Off/GroupedView 恒隐藏。</summary>
    public bool IsSubgroupRailVisible =>
        SubgroupDisplay == SubgroupDisplayMode.ChipRail &&
        ActivePanel is { } panel &&
        BoardCategoryCatalog.IsContentBucket(panel.Category);

    /// <summary>按名清单逐槽刷新 chip 显示名（激活面板的名；窗口层在面板激活与
    /// 改名提交后调用，settings 仍是名单持久化单一事实源）。</summary>
    public void RefreshSubgroupChips(WindowSettings settings, BoardCategory category)
    {
        foreach (var chip in SubgroupChips)
        {
            chip.ApplyDisplayName(settings.SubgroupName(category, chip.Slot));
        }
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

    /// <summary>AI 整理总开关（默认关 = 用户 2026-11-02 决策：AI 功能默认零打扰，
    /// 需显式开启）：关闭＝卡片 AI 整理钮不再出现、整理入口零动作（含无 Key 提示
    /// 也不弹）。由 ApplyPreferences 唯一管线喂值（契约 #5），卡片模板触发器与
    /// 入口守卫经窗口祖先 DataContext 绑定消费（照 ShortCardNotesEnabled 先例）。</summary>
    public bool AiOrganizeEnabled
    {
        get => _aiOrganizeEnabled;
        set => SetProperty(ref _aiOrganizeEnabled, value);
    }

    /// <summary>插件目录存在已启用动作（Issue #240 插件动作钮可见性的目录态，
    /// 单一来源＝PluginCatalog.EnabledActions 非空，禁止在别处复制目录状态）：
    /// 由窗口层在构造期与每次目录变化（启停/设置/重扫）后喂值，卡片模板的
    /// 插件钮与追加行触发器经窗口祖先 DataContext 绑定消费。</summary>
    public bool HasEnabledPluginActions
    {
        get => _hasEnabledPluginActions;
        set => SetProperty(ref _hasEnabledPluginActions, value);
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
        // 子分组轨可见性随激活面板的内容桶角色翻转（契约 #57 作用域）。
        OnPropertyChanged(nameof(IsSubgroupRailVisible));
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
