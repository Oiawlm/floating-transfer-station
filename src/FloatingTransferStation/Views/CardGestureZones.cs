namespace FloatingTransferStation.Views;

/// <summary>
/// 卡片左右对半分区手势的命中标记（1.18.0 对半修正）。标记挂在卡片模板命中层
/// 两列（* /*，各占一半）的根元素 <c>Tag</c> 上，手势层沿命中元素向上的可视树
/// 路径识别命中区域，使区域判定与视觉布局解耦（无几何常量可漂移）。内容视觉层
/// 全宽在命中层之下，指针命中总落在命中层上。见 <c>MainWindow.CardGestures.cs</c>。
/// </summary>
public static class CardGestureZones
{
    /// <summary>左半：承载双击编辑与拖拽起点，无选择语义。</summary>
    public const string ContentZone = "CardContentZone";

    /// <summary>右半（置顶/选择按钮所在半区）：承载单击选择与右键复制。</summary>
    public const string OperationsZone = "CardOperationsZone";
}
