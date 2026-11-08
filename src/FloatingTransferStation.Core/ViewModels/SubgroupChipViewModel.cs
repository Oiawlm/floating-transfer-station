using FloatingTransferStation.Models;

namespace FloatingTransferStation.ViewModels;

/// <summary>
/// 子分组按钮轨的单个 chip 会话态（契约 #57，形态①）：显示名（名清单逐槽解析，
/// 窗口层喂值）、双击改名会话（照 <see cref="CategoryViewModel"/> 全套——草稿
/// 限长、Begin/End/Cancel/Apply）、过滤选中态、收集落点默认环与拖放目标高亮
/// （窗口层喂值）。
/// </summary>
public sealed class SubgroupChipViewModel : ObservableObject
{
    private string _displayName;
    private string _draftName;
    private bool _isEditingName;
    private bool _isFilterActive;
    private DefaultMarkerState _defaultMarkerState;
    private bool _isDropTarget;

    public SubgroupChipViewModel(int slot)
    {
        Slot = slot;
        _displayName = SubgroupCatalog.DefaultName(slot);
        _draftName = _displayName;
    }

    public int Slot { get; }

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public string DraftName
    {
        get => _draftName;
        set => SetProperty(ref _draftName, SubgroupCatalog.LimitName(value));
    }

    public bool IsEditingName
    {
        get => _isEditingName;
        private set => SetProperty(ref _isEditingName, value);
    }

    /// <summary>chip 过滤选中态（契约 #57 互斥裁决：搜索激活时冻结让位、退出搜索自动恢复）。</summary>
    public bool IsFilterActive
    {
        get => _isFilterActive;
        set => SetProperty(ref _isFilterActive, value);
    }

    /// <summary>收集落点子分组环态（契约 #57 修订＋#56 双态环语法）：chip 右上角
    /// 圆环复用 #56 分类默认标记规格——轨可见时恰一枚槽 chip 独占环（该分类默认
    /// 子分组，未设兜底显示序首槽恒灰；紫 iff 激活分类为默认接收分类且默认已设），
    /// 其余槽 chip None；chip 实例跨分类共享，本态由 VM 按激活分类差分派发
    /// （照 IsFilterActive 先例，窗口层喂值）。</summary>
    public DefaultMarkerState DefaultMarkerState
    {
        get => _defaultMarkerState;
        set => SetProperty(ref _defaultMarkerState, value);
    }

    /// <summary>拖卡归属高亮（窗口层差分应用，照分类标签 IsDropTarget 先例）。</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => SetProperty(ref _isDropTarget, value);
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
