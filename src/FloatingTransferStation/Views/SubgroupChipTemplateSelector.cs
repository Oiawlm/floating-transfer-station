using System.Windows;
using System.Windows.Controls;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Views;

/// <summary>
/// 子分组轨模板选择器（契约 #57 修订）：轨 ItemsControl 的投影混合槽 chip 与
/// 「全部」伪 chip 两种数据上下文——按实例类型分派模板（槽＝交互全套、全部＝仅
/// 悬停进入），禁给 <see cref="SubgroupChipViewModel"/> 加伪槽或在两 VM 间散布
/// 类型守卫。
/// </summary>
public sealed class SubgroupChipTemplateSelector : DataTemplateSelector
{
    public DataTemplate? SlotTemplate { get; set; }

    public DataTemplate? AllTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item switch
        {
            SubgroupAllChipViewModel => AllTemplate,
            SubgroupChipViewModel => SlotTemplate,
            _ => base.SelectTemplate(item, container)
        };
}
