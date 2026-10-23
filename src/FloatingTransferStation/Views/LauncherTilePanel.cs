using System.Windows;
using System.Windows.Controls;
using FloatingTransferStation.Design;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Views;

/// <summary>
/// 启动瓦片网格承载面板（#30 修订 2026-10-18，Issue #186）：拉伸填充布局。
/// 列数与瓦片宽在 Measure/Arrange 内由 <see cref="LauncherGridMath"/> 从可用宽
/// （ScrollViewer 视口）现算——单一几何源，无「公式限宽＋面板再量化」双口径，
/// 无 ScrollChanged 回写自身 Width 的自引用（Issue #162 回归结构就此消灭）；
/// 窄轨/收起期视口 0 由布局系统自然承载（量出 0 宽、展开后自动重测），无需跳帧
/// 补算特例。行内左对齐、末行不满时靠左（WrapPanel 语义），200 上限的有界性
/// 不变（不虚拟化）。沟道由排版间距承载（瓦片不再自带 Margin）。
/// 列数策略（默认自适应 = 既有公式）经 <see cref="ColumnStrategy"/> 依赖属性
/// 下发：MainWindow 偏好管线赋值即失效重测（无重启）；面板未实现（启动器表面
/// 未激活）期间由表面激活路径回放当前偏好——权威源=CurrentPreferences，见
/// MainWindow.ReplayLauncherColumnStrategy。
/// </summary>
internal sealed class LauncherTilePanel : Panel
{
    public static readonly DependencyProperty ColumnStrategyProperty =
        DependencyProperty.Register(
            nameof(ColumnStrategy),
            typeof(LauncherColumnStrategy),
            typeof(LauncherTilePanel),
            new PropertyMetadata(LauncherColumnStrategy.Adaptive, OnColumnStrategyChanged));

    public LauncherColumnStrategy ColumnStrategy
    {
        get => (LauncherColumnStrategy)GetValue(ColumnStrategyProperty);
        set => SetValue(ColumnStrategyProperty, value);
    }

    private static void OnColumnStrategyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // 固定↔自适应切换改变列数与行数：整体重测重排，下一次布局即新策略；
        // 首布局前的赋值此处失效重测是幂等 no-op（布局系统本就要测一次）。
        if (d is LauncherTilePanel panel)
        {
            panel.InvalidateMeasure();
        }
    }

    /// <summary>焦点导航共用：当前生效列数（面板布局宽非正时走策略兜底口径）。</summary>
    internal int EffectiveColumnCount =>
        LauncherGridMath.ColumnCount(ActualWidth > 0 ? ActualWidth : 0, ColumnStrategy);

    protected override Size MeasureOverride(Size availableSize)
    {
        var viewportWidth = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        var columns = LauncherGridMath.ColumnCount(viewportWidth, ColumnStrategy);
        var tileWidth = LauncherGridMath.TileWidth(viewportWidth, columns);
        var tileHeight = DesignTokens.LauncherTileHeightPx;
        var gutter = DesignTokens.LauncherTileGutterPx;
        var childConstraint = new Size(tileWidth, tileHeight);
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(childConstraint);
        }

        var rows = (InternalChildren.Count + columns - 1) / columns;
        var height = rows > 0 ? rows * tileHeight + (rows - 1) * gutter : 0;
        return new Size(columns * tileWidth + (columns - 1) * gutter, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = LauncherGridMath.ColumnCount(finalSize.Width, ColumnStrategy);
        var tileWidth = LauncherGridMath.TileWidth(finalSize.Width, columns);
        var tileHeight = DesignTokens.LauncherTileHeightPx;
        var gutter = DesignTokens.LauncherTileGutterPx;
        for (var index = 0; index < InternalChildren.Count; index++)
        {
            var row = index / columns;
            var column = index % columns;
            InternalChildren[index].Arrange(new Rect(
                column * (tileWidth + gutter),
                row * (tileHeight + gutter),
                tileWidth,
                tileHeight));
        }

        return finalSize;
    }
}
