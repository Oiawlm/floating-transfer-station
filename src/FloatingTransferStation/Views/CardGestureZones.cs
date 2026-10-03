namespace FloatingTransferStation.Views;

/// <summary>
/// 卡片左右分区手势的命中标记（1.17.0 手势分区重构）。标记挂在卡片模板两区
/// 根元素的 <c>Tag</c> 上，手势层沿命中元素向上的可视树路径识别命中区域，
/// 使区域判定与视觉布局解耦（无几何常量可漂移）。见 <c>MainWindow.CardGestures.cs</c>。
/// </summary>
public static class CardGestureZones
{
    /// <summary>左区（内容列）：承载双击编辑与拖拽起点，无选择语义。</summary>
    public const string ContentZone = "CardContentZone";

    /// <summary>右区（置顶/选择按钮所在操作列）：承载单击选择与右键复制。</summary>
    public const string OperationsZone = "CardOperationsZone";
}
