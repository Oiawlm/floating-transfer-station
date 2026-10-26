namespace FloatingTransferStation.Views;

/// <summary>
/// 卡片左右对半分区手势的命中标记（1.18.0 对半修正）。标记挂在卡片模板命中层
/// 两列（* /*，各占一半）的根元素 <c>Tag</c> 上，手势层沿命中元素向上的可视树
/// 路径识别命中区域，使区域判定与视觉布局解耦（无几何常量可漂移）。内容视觉层
/// 全宽在命中层之下，指针命中总落在命中层上。见 <c>MainWindow.CardGestures.cs</c>。
/// </summary>
public static class CardGestureZones
{
    /// <summary>左半：承载双击编辑与拖拽起点，无左键选择语义（右键选择手势
    /// 全卡面统一，Issue #240，右键不分区域）。</summary>
    public const string ContentZone = "CardContentZone";

    /// <summary>右半（置顶/选择/删除按钮所在半区）：承载左键复制；右键选择
    /// 手势与左半同构（全卡面统一，Issue #240）。</summary>
    public const string OperationsZone = "CardOperationsZone";

    /// <summary>
    /// 右半操作按钮的 CommandParameter 标记（XAML 与手势层共用单一来源）：
    /// 手势层按压会话据此把按钮命中分类为独立意图（区别于右半区域的
    /// 选择/复制手势）；操作按钮的可见性矩阵见 docs/design-contracts.md。
    /// </summary>
    public const string TogglePinCommand = "TogglePin";

    /// <summary>选择按钮：单击 toggle 该卡选中（矩阵：选中卡片常显可点）。</summary>
    public const string ToggleSelectionCommand = "ToggleSelection";

    /// <summary>删除按钮：单击只删这一张卡，走撤销管线（矩阵：选中卡片一律隐藏）。</summary>
    public const string DeleteCardCommand = "DeleteCard";

    /// <summary>
    /// 备注交互条（1.28.0）：单击进入备注就地编辑（仅文字卡）。独立意图会话，
    /// 模式同置顶/删除按钮——不参与双击窗、不记双击痕迹、滑离释放取消意图；
    /// 备注交互条左键绝不 toggle 选中，右键语义与既有命中层分区一致。
    /// </summary>
    public const string NoteStripCommand = "CardNoteStrip";

    /// <summary>
    /// AI 整理钮（1.41.0）：单击按内置提示词整理该卡全文并直接替换正文
    /// （Ctrl+Z 恢复原文）。独立意图会话，模式同置顶/删除/备注钮——不参与
    /// 双击窗、不记双击痕迹、滑离释放取消意图；左键绝不 toggle 选中。
    /// </summary>
    public const string AiOrganizeCommand = "AiOrganize";

    /// <summary>
    /// 插件动作钮（Issue #240）：单击打开「插件动作」菜单（菜单入口自左半
    /// 右键迁入——右键已是全卡面统一选择手势）。独立意图会话，模式同置顶/
    /// 删除/备注/AI 钮——不参与双击窗、不记双击痕迹、滑离释放取消意图；
    /// 左键绝不 toggle 选中（「命中卡不在选集先单选」惯例在菜单函数内）。
    /// </summary>
    public const string PluginActionsCommand = "PluginActions";

    /// <summary>
    /// 启动瓦片启动区（#30 修订 2026-10-18，Issue #186）：图标槽区域，单击=启动
    /// （500ms 重入门闩保持），拖拽仅从此区发起；选择非空时单击改 toggle 选择
    /// （启动挂起）。标记挂瓦片模板图标格 Tag 上，手势层沿可视树路径识别，
    /// 区域判定与视觉布局解耦（与卡片对半分区同机制族）。
    /// </summary>
    public const string LauncherLaunchZone = "LauncherLaunchZone";

    /// <summary>
    /// 启动瓦片名称带（#30 修订 2026-10-18，Issue #186）：底部整带，单击=不启动
    /// 仅聚焦，双击=就地改名（系统双击时限，与卡片左半同一家规）；选择非空时
    /// 单击改 toggle 选择；不发起拖拽。
    /// </summary>
    public const string LauncherNameZone = "LauncherNameZone";
}
