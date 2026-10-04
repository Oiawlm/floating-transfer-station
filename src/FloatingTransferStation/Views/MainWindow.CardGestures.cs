using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Views;

/// <summary>
/// 卡片指针手势状态机（1.17.0 左右分区重构）。卡片指针输入收敛为单一模型：
/// Down 记录按压会话（命中条目、命中区域、修饰键、起点）→ Move 判拖拽 →
/// Up 按「区域 × 双击窗 × 修饰键」分发意图（编辑 / 选择 / 置顶 / 复制），
/// 意图实现（BeginCardTextEditing、ToggleSelection、SelectRange、
/// ToggleCardPinAsync、CopyBoardItemsToClipboard）保持与手势判定解耦。
/// 分区契约：左区（内容列）承载双击编辑与拖拽起点，无选择语义；
/// 右区（操作列）承载单击选择与右键复制，置顶按钮为右区内的独立置顶意图；
/// 边缘环带归左区（贴边点击承载拖拽，不误触选择/复制）。
/// 双击判定由手势层自维护（上次抬键的时间戳与位置），不依赖
/// Control.MouseDoubleClick——其系统触发链会被预览层为压制 ListBox
/// 默认选择而设置的 Handled 阻断（docs/observations.md B-007 的实机根因）。
/// 操作区按钮同样由手势层接管：真实输入下原生按钮 Click 会先被 ListBox
/// 默认单击选择吞掉（合成/UIA 可达、实机不可达，1.16.0 起的隐性缺陷，
/// 1.17.1 修复）。
/// </summary>
public partial class MainWindow
{
    /// <summary>卡片命中区域：决定 Up 分发与右键门控的手势分区。</summary>
    private enum CardHitZone
    {
        /// <summary>左区：内容列，承载双击编辑与拖拽起点。</summary>
        Content,

        /// <summary>右区：操作列（置顶/选择按钮所在区域），承载单击选择与右键复制。</summary>
        Operations,

        /// <summary>右区·置顶按钮：单击置顶/取消置顶，与右区选择手势不同的独立意图。</summary>
        OperationsPin,
    }

    /// <summary>一次成功点击（未拖拽的按下-抬起）的痕迹，用于双击窗口判定。</summary>
    private readonly record struct CardClickTrace(Guid ItemId, int Timestamp, Point Position);

    private Point _pressStartPosition;
    private BoardItem? _pressItem;
    private CardHitZone _pressZone;
    private ModifierKeys _pressModifiers;
    private bool _dragThresholdCrossed;
    private CardClickTrace? _lastCardClick;

    private void BoardList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        // 卡片操作区按钮（置顶/选择）与 wrapper 一样由手势层接管：真实输入下
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
            _pressItem = buttonContainer.DataContext as BoardItem;
            _pressZone = Equals(hitButton.CommandParameter, "TogglePin")
                ? CardHitZone.OperationsPin
                : CardHitZone.Operations;
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
        // 右区手势与选择按钮发生，左区点击不携带任何选择语义。
        _pressStartPosition = e.GetPosition(this);
        _pressItem = container.DataContext as BoardItem;
        _pressZone = ResolveCardHitZone(source, container);
        _dragThresholdCrossed = false;
        _pressModifiers = Keyboard.Modifiers;
        e.Handled = true;
    }

    private void BoardList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressItem is { } item && !_dragThresholdCrossed)
        {
            DispatchCardClick(item, e);
        }

        if (_pressItem is not null)
        {
            e.Handled = true;
        }

        ResetCardPressState();
    }

    /// <summary>分发一次完整点击：先判双击，再按命中区域 × 修饰键路由到意图实现。</summary>
    private void DispatchCardClick(BoardItem item, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(this);
        if (IsWithinDoubleClickWindow(item, e.Timestamp, position))
        {
            // 双击已在 _lastCardClick 更新前判定；本击痕迹由本方法末尾统一记录。
            // 右区双击：第一击已 toggle，第二击不再分发（净效果等同一次单击，
            // 避免选中态闪烁）；左区双击：文字卡进入就地编辑（守卫沿用
            // BeginCardTextEditing：搜索态、已有编辑、关闭中；图片卡无操作）。
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
            // 置顶按钮是右区内的独立意图：单击置顶/取消置顶，不触碰选择；
            // 语义与 BoardList_ButtonClick 的 TogglePin 分支共用同一实现。
            _ = ToggleCardPinAsync(item);
        }
        else if (_pressZone == CardHitZone.Operations)
        {
            // 选择手势只属于右区：Shift 走锚点范围选择，裸单击与 Ctrl+单击
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

        _lastCardClick = new CardClickTrace(item.Id, e.Timestamp, position);
    }

    /// <summary>
    /// 双击窗口判定：同一卡片、系统双击时限内、位置在系统双击容差内。
    /// 时限与容差取系统双击设置，与拖拽阈值一样尊重用户的鼠标配置。
    /// </summary>
    private bool IsWithinDoubleClickWindow(BoardItem item, int timestamp, Point position)
    {
        if (_lastCardClick is not { } last || last.ItemId != item.Id)
        {
            return false;
        }

        var delta = (long)timestamp - last.Timestamp;
        return delta >= 0 &&
            delta <= GetDoubleClickTime() &&
            Math.Abs(position.X - last.Position.X) <= GetSystemMetrics(SmCxddoubleclk) &&
            Math.Abs(position.Y - last.Position.Y) <= GetSystemMetrics(SmCydoubleclk);
    }

    private void ResetCardPressState()
    {
        _pressItem = null;
        _dragThresholdCrossed = false;
        _pressModifiers = ModifierKeys.None;
    }

    /// <summary>
    /// 命中区域判定：沿命中元素向上的可视树路径取第一个分区标记。
    /// 未遇到标记（卡片边缘环带、容器自身）归左区：贴边点击承载拖拽，
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
