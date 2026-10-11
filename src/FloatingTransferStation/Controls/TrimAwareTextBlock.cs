using System.Windows;
using System.Windows.Controls;

namespace FloatingTransferStation.Controls;

/// <summary>
/// 暴露「文本是否被限高截断」的承载容器（1.28.0，长卡片备注的添加门槛判据）。
/// 判据是渲染布局事实而非字符数：直接比较 DesiredSize/ActualHeight 不可靠——
/// 两者恒被 MaxHeight 钳制，CharacterEllipsis 也只在受高度约束的那次测量中
/// 产生。因此 MeasureOverride 双次测量：先不限高取自然高度，再以
/// min(约束高, MaxHeight) 限高收尾（最后一次测量必须是受限那次，保证渲染=
/// 省略号态），自然高度超出限高 + 0.5 DIP 容差即截断。
/// 形态说明：WPF 的 TextBlock.MeasureOverride 是密封的（CS0239），无法按
/// TextBlock 子类实现，故为 Decorator 子类——子元素必须是 TextBlock（模板
/// 提供），限高 MaxHeight 只声明在本元素上（限高唯一来源，子元素不得重复
/// 声明，否则双来源漂移）；measure 驱动天然覆盖文本/宽度/字号/主题变化，
/// 虚拟化回收后随重测自校正。IsTruncated 是普通只读依赖属性：不得注册
/// AffectsMeasure——消费方（备注入口可见性）不参与本控件布局，值变化仅写
/// 不触发测量重入环。
/// </summary>
public class TrimAwareTextBlock : Decorator
{
    /// <summary>高度比较容差：吸收亚像素排版误差，避免恰好整行数误判截断。</summary>
    private const double TruncationEpsilon = 0.5;

    private static readonly DependencyPropertyKey IsTruncatedPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsTruncated),
        typeof(bool),
        typeof(TrimAwareTextBlock),
        new PropertyMetadata(false));

    public static readonly DependencyProperty IsTruncatedProperty = IsTruncatedPropertyKey.DependencyProperty;

    /// <summary>本次测量中受限高度是否截掉了自然文本（省略号已出现在渲染态）。</summary>
    public bool IsTruncated => (bool)GetValue(IsTruncatedProperty);

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is not { } child)
        {
            return default;
        }

        var limitHeight = Math.Min(constraint.Height, MaxHeight);
        child.Measure(new Size(constraint.Width, double.PositiveInfinity));
        var naturalHeight = child.DesiredSize.Height;
        var isTruncated = limitHeight < double.PositiveInfinity &&
            naturalHeight > limitHeight + TruncationEpsilon;
        if (IsTruncated != isTruncated)
        {
            SetValue(IsTruncatedPropertyKey, isTruncated);
        }

        // 以受限测量收尾：CharacterEllipsis 在这次测量中落到最后一条放得下的行。
        child.Measure(new Size(constraint.Width, limitHeight));
        return child.DesiredSize;
    }
}
