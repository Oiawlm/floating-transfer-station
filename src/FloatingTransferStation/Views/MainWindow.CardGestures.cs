using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Views;

/// <summary>
/// 卡片指针手势状态机（1.17.0 左右分区重构，1.18.0 对半修正）。卡片指针输入
/// 收敛为单一模型：Down 记录按压会话（命中条目、命中区域、修饰键、起点与
/// 按下时刻）→ Move 判拖拽 → Up 按「区域 × 双击窗 × 修饰键」分发意图
/// （编辑 / 选择 / 置顶 / 复制），意图实现（BeginCardTextEditing、
/// ToggleSelection、SelectRange、ToggleCardPinAsync、CopyBoardItemsToClipboard）
/// 保持与手势判定解耦。
/// 分区契约（对半）：卡片内容左右两半——左半（命中层左列）承载双击编辑与
/// 拖拽起点，无选择语义；右半（命中层右列，置顶/选择按钮所在半区）承载单击
/// 选择与右键复制，置顶按钮为右半内的独立置顶意图；卡片 12px 边缘环带不经
/// 命中层，按默认归左半（贴边点击承载拖拽，不误触选择/复制）。
/// 双击判定由手势层自维护（上次点击的按下时刻、按下位置与命中区域），
/// 与系统语义一致按 Down 判定，不依赖 Control.MouseDoubleClick——其系统触发
/// 链会被预览层为压制 ListBox 默认选择而设置的 Handled 阻断
/// （docs/observations.md B-007 的实机根因）。
/// 右半操作按钮同样由手势层接管：真实输入下原生按钮 Click 会先被 ListBox
/// 默认单击选择吞掉（合成/UIA 可达、实机不可达，1.16.0 起的隐性缺陷，
/// 1.17.1 修复）。按钮按压是独立会话：不参与拖拽起点，释放点滑离按钮即
/// 取消意图（按钮常规语义），痕迹不进入双击窗（连点置顶每击都生效）。
/// </summary>
public partial class MainWindow
{
    /// <summary>卡片命中区域：决定 Up 分发与右键门控的手势分区。</summary>
    private enum CardHitZone
    {
        /// <summary>左半：承载双击编辑与拖拽起点。</summary>
        Content,

        /// <summary>右半：承载单击选择与右键复制（置顶/选择按钮所在半区）。</summary>
        Operations,

        /// <summary>右半·置顶按钮：单击置顶/取消置顶，与右半选择手势不同的独立意图。</summary>
        OperationsPin,
    }

    /// <summary>
    /// 一次成功点击（未拖拽的按下-抬起）的痕迹，用于双击窗口判定。
    /// 记录按下时刻/位置/区域（系统双击语义按 Down 判定；Up 时刻会把
    /// 长按+快速单击拼成伪双击）；置顶按钮点击不记录痕迹。
    /// </summary>
    private readonly record struct CardClickTrace(
        Guid ItemId,
        int Timestamp,
        Point Position,
        CardHitZone Zone);

    private Point _pressStartPosition;
    private int _pressTimestamp;
    private BoardItem? _pressItem;
    private CardHitZone _pressZone;
    private ModifierKeys _pressModifiers;
    private bool _dragThresholdCrossed;
    private CardClickTrace? _lastCardClick;
    private Button? _pressButton;

    private void BoardList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        // 编辑器覆盖层是列表的兄弟元素：任何到达列表预览层的按下都不在
        // 编辑器内，先冲刷悬挂会话（提交已输入文字）再开始本次手势。
        // 滚动条例外——拖动滚动条与滚轮同属滚动语义，只让覆盖层跟随，
        // 不终结会话（与下方 ClearUserSelection 共用同一滚动条排除判定）。
        if (FindAncestor<ScrollBar>(source) is null)
        {
            CommitCardTextEditing();
        }

        // 右半操作按钮（置顶/选择）与卡片一样由手势层接管：真实输入下
        // 原生按钮的 Click 会先被 ListBox 默认单击选择对冒泡按下的处理与捕获
        // 吞掉（实机上 BoardList_ButtonClick 不可达，B-007 同族的「合成可达、
        // 实机不可达」缺陷，1.16.0 起即存在）；合成与 UIA 自动化路径仍走
        // BoardList_ButtonClick。列表外的按钮不经过本处理器，原生行为不变。
        if (FindAncestor<Button>(source) is { } hitButton)
        {
            if (FindAncestor<ListBoxItem>(hitButton) is not { } buttonContainer)
            {
                ResetCardPressState();
                return;
            }

            _pressStartPosition = e.GetPosition(this);
            _pressTimestamp = e.Timestamp;
            _pressItem = buttonContainer.DataContext as BoardItem;
            _pressZone = Equals(hitButton.CommandParameter, "TogglePin")
                ? CardHitZone.OperationsPin
                : CardHitZone.Operations;
            _pressButton = hitButton;
            _dragThresholdCrossed = false;
            _pressModifiers = Keyboard.Modifiers;
            e.Handled = true;
            return;
        }

        var container = FindAncestor<ListBoxItem>(source);
        if (container is null)
        {
            ResetCardPressState();
            if (FindAncestor<ScrollBar>(source) is null)
            {
                ClearUserSelection();
            }

            return;
        }

        // 命中卡片：记录按压会话并压制 ListBox 默认单击选择——选择只经由
        // 右半手势与选择按钮发生，左半点击不携带任何选择语义。
        _pressStartPosition = e.GetPosition(this);
        _pressTimestamp = e.Timestamp;
        _pressItem = container.DataContext as BoardItem;
        _pressZone = ResolveCardHitZone(source, container);
        _pressButton = null;
        _dragThresholdCrossed = false;
        _pressModifiers = Keyboard.Modifiers;
        e.Handled = true;
    }

    private void BoardList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressItem is { } item && !_dragThresholdCrossed)
        {
            // 按钮按压的意图分发要求释放点仍在按钮上（按钮常规语义：滑离
            // 取消）。判定用 Up 命中源——不依赖 IsMouseOver（按钮未悬停淡出
            // 时不可命中，IsMouseOver 会误报 false 而吞掉合法点击）。
            if (_pressButton is null ||
                FindAncestor<Button>(e.OriginalSource as DependencyObject) == _pressButton)
            {
                DispatchCardClick(item);
            }
        }

        if (_pressItem is not null)
        {
            e.Handled = true;
        }

        ResetCardPressState();
    }

    /// <summary>分发一次完整点击：先判双击，再按命中区域 × 修饰键路由到意图实现。</summary>
    private void DispatchCardClick(BoardItem item)
    {
        // 系统双击语义按两击的按下时刻与按下位置判定（长按后快速单击不得
        // 拼成伪双击）；痕迹与判定均取 Down 值，Up 不参与。
        if (IsWithinDoubleClickWindow(item))
        {
            // 双击已在 _lastCardClick 更新前判定；本击痕迹由本方法末尾统一记录。
            // 双击要求两击同区：跨半两击各自独立分发（右半第一击已 toggle，
            // 左半第二击无语义；反向亦然）。右半双击：第一击已 toggle，第二击
            // 不再分发（净效果等同一次单击，避免选中态闪烁）；左半双击：文字卡
            // 进入就地编辑（守卫沿用 BeginCardTextEditing：搜索态、关闭中；
            // 已有会话接续提交后重开；图片卡无操作）。
            if (_pressZone == CardHitZone.Content && item.Kind == BoardItemKind.Text)
            {
                if (BoardList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
                {
                    BeginCardTextEditing(item, container);
                }
            }
        }
        else if (_pressZone == CardHitZone.OperationsPin)
        {
            // 置顶按钮是右半内的独立意图：单击置顶/取消置顶，不触碰选择；
            // 语义与 BoardList_ButtonClick 的 TogglePin 分支共用同一实现。
            _ = ToggleCardPinAsync(item);
        }
        else if (_pressZone == CardHitZone.Operations)
        {
            // 选择手势只属于右半：Shift 走锚点范围选择，裸单击与 Ctrl+单击
            // 等效 toggle（保留既有 Ctrl 习惯），均不清理其他已选条目。
            if (_pressModifiers.HasFlag(ModifierKeys.Shift))
            {
                SelectRange(item, _pressModifiers.HasFlag(ModifierKeys.Control));
            }
            else
            {
                ToggleSelection(item);
            }
        }

        // 痕迹只记左半与右半点击：置顶按钮是独立意图不参与双击窗——连点
        // 置顶必须每击都 toggle（否则第二击被双击窗吞掉，按钮像坏的）。
        if (_pressZone is CardHitZone.Content or CardHitZone.Operations)
        {
            _lastCardClick = new CardClickTrace(
                item.Id,
                _pressTimestamp,
                _pressStartPosition,
                _pressZone);
        }
    }

    /// <summary>
    /// 双击窗口判定：同一卡片、两击同区、系统双击时限内、按下位置在系统
    /// 双击容差内。时限与容差取系统双击设置，与拖拽阈值一样尊重用户的
    /// 鼠标配置。
    /// </summary>
    private bool IsWithinDoubleClickWindow(BoardItem item)
    {
        if (_lastCardClick is not { } last ||
            last.ItemId != item.Id ||
            last.Zone != _pressZone)
        {
            return false;
        }

        var delta = (long)_pressTimestamp - last.Timestamp;
        // 系统双击容差是物理像素，GetPosition 是 DIP：按窗口 DPI 换算成 DIP
        // 再比较（一次查询换算 X/Y，150%/200% 缩放下容差不失真）。
        var dpi = VisualTreeHelper.GetDpi(this);
        var toleranceX = GetSystemMetrics(SmCxddoubleclk) / dpi.DpiScaleX;
        var toleranceY = GetSystemMetrics(SmCydoubleclk) / dpi.DpiScaleY;
        return delta >= 0 &&
            delta <= GetDoubleClickTime() &&
            Math.Abs(_pressStartPosition.X - last.Position.X) <= toleranceX &&
            Math.Abs(_pressStartPosition.Y - last.Position.Y) <= toleranceY;
    }

    private void ResetCardPressState()
    {
        _pressStartPosition = default;
        _pressTimestamp = 0;
        _pressItem = null;
        _pressZone = CardHitZone.Content;
        _pressButton = null;
        _dragThresholdCrossed = false;
        _pressModifiers = ModifierKeys.None;
    }

    /// <summary>
    /// 命中区域判定：沿命中元素向上的可视树路径取第一个分区标记。
    /// 未遇到标记（卡片边缘环带、容器自身）归左半：贴边点击承载拖拽，
    /// 不误触选择/复制。
    /// </summary>
    private static CardHitZone ResolveCardHitZone(DependencyObject? source, ListBoxItem container)
    {
        for (var current = source;
             current is not null && !ReferenceEquals(current, container);
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { Tag: string tag })
            {
                if (tag == CardGestureZones.ContentZone)
                {
                    return CardHitZone.Content;
                }

                if (tag == CardGestureZones.OperationsZone)
                {
                    return CardHitZone.Operations;
                }
            }
        }

        return CardHitZone.Content;
    }

    private const int SmCxddoubleclk = 36;
    private const int SmCydoubleclk = 37;

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
