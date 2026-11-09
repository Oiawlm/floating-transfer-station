using FloatingTransferStation.Models;

namespace FloatingTransferStation.ViewModels;

/// <summary>
/// 子分组按钮轨的「全部」伪 chip 会话态（契约 #57 修订，实验开关默认关）：无槽
/// 身份——不参与槽序拖排/改名/拖放归属/默认落点（不设 Slot、编辑会话与环态），
/// 仅承载全量过滤激活态；显示名恒为目录常量 <see cref="SubgroupCatalog.AllChipName"/>。
/// 样式层 <c>SubgroupChipActiveLayerStyle</c>/<c>SubgroupChipLabelTextStyle</c> 按
/// <see cref="IsFilterActive"/> 同名命中（照 <see cref="SubgroupChipViewModel"/> 的
/// 鸭子类型绑定先例，两 VM 共享轨 chip 样式族）。
/// </summary>
public sealed class SubgroupAllChipViewModel : ObservableObject
{
    private bool _isFilterActive;

    /// <summary>显示名恒为「全部」（伪 chip 无改名会话，无 DraftName）。</summary>
    public string DisplayName => SubgroupCatalog.AllChipName;

    /// <summary>全量过滤选中态：悬停进入（Filter=null＋全 chip 激活视觉）、沿既有
    /// 三路径（Esc/切分类/模式切换）＋悬停其他 chip 退出（契约 #60 2026-11-08 句级
    /// 同步）；搜索激活期间冻结让位（#37）。</summary>
    public bool IsFilterActive
    {
        get => _isFilterActive;
        set => SetProperty(ref _isFilterActive, value);
    }
}
