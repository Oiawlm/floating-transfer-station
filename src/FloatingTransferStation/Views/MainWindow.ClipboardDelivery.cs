using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Views;

/// <summary>
/// 卡片复制到剪贴板（交付第二通道，解决游戏聊天窗等无法拖入的目标）：
/// 左键单击卡片右半复制那一张（手势交换，Issue #216；不依赖、不改变当前
/// 选择，搜索过滤态同样可用）；右键选择手势全卡面统一（Issue #240）——
/// 任意卡面位置的裸/Ctrl 右键 toggle、Shift/Ctrl+Shift 右键锚点范围选择；
/// 面板持有键盘焦点、存在选择且不在文字编辑态时 Ctrl+C 复制选中项。
/// 复制不删除源卡片、不进撤销栈；负载与拖出同构并携带内部条目标记，
/// 自动采集据此跳过，复制旧卡片不会凭空生成重复条目。
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 剪贴板写入缝：测试注入捕获函数以断言负载与提示，不触碰系统剪贴板；
    /// null 时走真实 Clipboard.SetDataObject。
    /// </summary>
    internal Func<DataObject, bool>? ClipboardWriterOverride { get; set; }

    /// <summary>
    /// 压制 ListBox 对卡片右键按下的默认选中：右键是选择手势（分发在 Up，
    /// 手势交换 Issue #216），不得让默认处理先行改写选择——否则 Up 的
    /// toggle 会在默认选中之上二次翻转、锚点语义也会被默认单选冲掉。
    /// 真实输入的右键 Down 若放行，ListBox 默认处理会先选中命中项——合成
    /// 测试只驱动 Up 事件，掩盖过这一实机偏差（B-007 同族）。列表空白与
    /// 滚动条不经过本压制，原生行为不变。
    /// 2026-11-03 起同时记录右键按压会话（镜像左键 Down 的按钮命中分类，
    /// 需求 1）：命中操作钮时 Up 先按「钮意图」分发（删除钮＝永久删除），
    /// 再落卡面选择手势——滑离按钮取消意图的判定依赖此会话。
    /// </summary>
    private void BoardList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 会话先清后记：每次 Down 起点重置——Down 落在列表空白/滚动条（早退路径）
        // 后紧接的卡面 Up 不得消费上一次按压的陈旧按钮会话。
        _rightPressItem = null;
        _rightPressButton = null;
        if (_isClosing ||
            e.OriginalSource is not DependencyObject source ||
            FindAncestor<ListBoxItem>(source) is not { } container)
        {
            return;
        }

        e.Handled = true;
        _rightPressItem = container.DataContext as BoardItem;
        _rightPressButton = FindAncestor<Button>(source);
    }

    /// <summary>
    /// 右键卡片分发（手势交换，Issue #216；全卡面统一，Issue #240）：凡命中卡片
    /// 的右键一律承载选择手势——Shift 走锚点范围选择（Ctrl+Shift 保留既有选择
    /// 并追加范围），裸右键与 Ctrl+右键等效 toggle，均不清理其他已选条目；修饰键
    /// 取 Up 时线程键盘态。左半/右半/12px 边缘环带同构（区域判定只服务左键路径）。
    /// 例外（2026-11-03 需求 1，契约 #19 双语义）：按压起于删除按钮的右键是
    /// 独立按钮意图——释放点仍在钮上＝永久删除该单卡（不可撤销），滑离＝取消
    /// 意图（镜像左键按钮会话，吞掉不分发选择手势）；命中分类先于卡面选择手势。
    /// 编辑中的卡让路（编辑表面属于编辑器，与左键路径同一家规）：右键不分发、
    /// 不拦截，交还编辑器原生行为。插件动作菜单入口已迁往操作网格插件动作钮
    ///（见 MainWindow.PluginActions.cs）。
    /// </summary>
    private void BoardList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isClosing ||
            e.OriginalSource is not DependencyObject source ||
            FindAncestor<ListBoxItem>(source) is not { } container ||
            container.DataContext is not BoardItem item)
        {
            _rightPressItem = null;
            _rightPressButton = null;
            return;
        }

        if (TryGetEditingCardContainer() is { } editingContainer &&
            (ReferenceEquals(source, editingContainer) ||
             editingContainer.IsAncestorOf(source)))
        {
            return;
        }

        // 删除钮命中分类（先于卡面选择手势）：按压起于删除钮的右键是独立意图——
        // 释放仍在钮上＝永久删除，滑离＝取消（按钮常规语义，与左键会话同构）。
        // 其余操作钮的右键保持全卡面统一选择手势（需求只改删除钮）。
        var rightPressedButton = _rightPressButton;
        var rightPressedItem = _rightPressItem;
        _rightPressItem = null;
        _rightPressButton = null;
        if (rightPressedButton is not null &&
            Equals(rightPressedButton.CommandParameter, CardGestureZones.DeleteCardCommand) &&
            rightPressedItem is not null)
        {
            e.Handled = true;
            if (FindAncestor<Button>(source) == rightPressedButton)
            {
                _ = DeleteSingleCardPermanentlyAsync(rightPressedItem);
            }

            return;
        }

        e.Handled = true;
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            SelectRange(item, modifiers.HasFlag(ModifierKeys.Control));
        }
        else
        {
            ToggleSelection(item);
        }
    }

    /// <summary>
    /// Ctrl+C 复制选中项：面板展开、无文字编辑焦点（TextBox 让路）且存在选择时
    /// 生效；无选择时不改剪贴板、不拦截按键。返回是否已处理该按键。
    /// </summary>
    private bool TryCopySelectionWithCtrlC(KeyEventArgs e)
    {
        if (e.Key != Key.C ||
            e.KeyboardDevice.Modifiers != ModifierKeys.Control ||
            _isClosing ||
            !_preferences.CopySelectionWithCtrlCEnabled ||
            Keyboard.FocusedElement is TextBoxBase ||
            !_viewModel.IsPanelExpanded)
        {
            return false;
        }

        var selected = OrderedSelectedBoardItems();
        if (selected.Length == 0)
        {
            return false;
        }

        e.Handled = true;
        CopyBoardItemsToClipboard(selected);
        return true;
    }

    /// <summary>选中项按板内顺序（置顶区在前、各自排序）交付，与批量置顶的取序一致。</summary>
    private BoardItem[] OrderedSelectedBoardItems()
    {
        if (_viewModel.ActivePanel is not { } panel)
        {
            return [];
        }

        var selectedIds = BoardList.SelectedItems
            .OfType<BoardItem>()
            .Select(item => item.Id)
            .ToHashSet();
        return panel.Items.Where(item => selectedIds.Contains(item.Id)).ToArray();
    }

    /// <summary>
    /// 打包并写入剪贴板，成功后经状态条提示；图片副本缺失或剪贴板暂时被占时
    /// 提示失败且不改变面板任何状态。
    /// </summary>
    private void CopyBoardItemsToClipboard(IReadOnlyList<BoardItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        DataObject data;
        try
        {
            data = _dragPayload.BuildClipboardPayload(items);
        }
        catch (FileNotFoundException)
        {
            ShowStatus("图片副本已缺失，无法复制。");
            return;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ShowStatus("无法复制这些内容：" + exception.Message);
            return;
        }

        if (!WriteClipboardData(data))
        {
            return;
        }

        ShowStatus(DescribeCopiedItems(items));
    }

    private bool WriteClipboardData(DataObject data)
    {
        try
        {
            if (ClipboardWriterOverride is { } writer)
            {
                return writer(data);
            }

            Clipboard.SetDataObject(data, true);
            return true;
        }
        catch (Exception exception) when (
            exception is COMException or ExternalException or InvalidOperationException)
        {
            ShowStatus("剪贴板暂时不可用，请重试。");
            return false;
        }
    }

    private static string DescribeCopiedItems(IReadOnlyList<BoardItem> items)
    {
        var textCount = items.Count(item => item.Kind == BoardItemKind.Text);
        var imageCount = items.Count - textCount;
        return (textCount, imageCount) switch
        {
            ( > 0, 0) => $"已复制 {textCount} 条文字到剪贴板",
            (0, _) => $"已复制 {imageCount} 张图片到剪贴板",
            _ => $"已复制 {textCount} 条文字和 {imageCount} 张图片到剪贴板"
        };
    }

    // ---- 左半裸单击送达（2026-11-03 需求 6，契约 #36 第三递送通道）----
    // 触发判定在 DispatchCardClick 的 Content 分支：裸左键（无修饰键）、文字卡、
    // 正文非空白 → 延迟到双击窗口期满才送达（窗口内第二击＝双击进编辑，不送达；
    // 按压演变成拖出不送达——Up 不分发；任何新按压意图取消挂起送达）。

    private DispatcherTimer? _textDeliveryTimer;
    private Guid? _pendingTextDeliveryItemId;
    private bool _isTextDeliveryInFlight;

    /// <summary>
    /// 挂起一次送达（复用系统双击窗口时限，与左半就地编辑的判定同源）：期满仍
    /// 未被取消（无第二击、无拖出、无新按压）时执行送达。
    /// </summary>
    private void ScheduleTextDelivery(BoardItem item)
    {
        CancelPendingTextDelivery();
        _pendingTextDeliveryItemId = item.Id;
        if (_textDeliveryTimer is null)
        {
            _textDeliveryTimer = new DispatcherTimer
            {
                Interval = DoubleClickWindow.SystemDoubleClickSpan
            };
            _textDeliveryTimer.Tick += TextDeliveryTimer_Tick;
        }
        else
        {
            _textDeliveryTimer.Interval = DoubleClickWindow.SystemDoubleClickSpan;
        }

        _textDeliveryTimer.Start();
    }

    /// <summary>取消挂起的送达（双击第二击、拖出、新按压、关闭面板）。</summary>
    private void CancelPendingTextDelivery()
    {
        _pendingTextDeliveryItemId = null;
        _textDeliveryTimer?.Stop();
    }

    private void TextDeliveryTimer_Tick(object? sender, EventArgs e)
    {
        var itemId = _pendingTextDeliveryItemId;
        _pendingTextDeliveryItemId = null;
        _textDeliveryTimer?.Stop();
        if (_isClosing || itemId is not { } pendingId)
        {
            return;
        }

        // 前一次送达尚未收尾（含原剪贴板恢复）时推迟本次触发：送达序列触碰全机
        // 共享的剪贴板与前台，串行化防止两次送达的恢复/写负载交错互相覆写。
        if (_isTextDeliveryInFlight)
        {
            _pendingTextDeliveryItemId = pendingId;
            _textDeliveryTimer?.Start();
            return;
        }

        // 触发时点复检：卡片仍存在、仍是文字卡且正文非空白（挂起期间被编辑清空
        // 或删除则放弃）；目标由按压时刻快照决定（MouseEnter/按压捕获）。
        if (_board.FindItem(pendingId) is not { Kind: BoardItemKind.Text } item ||
            string.IsNullOrWhiteSpace(item.Text))
        {
            return;
        }

        _ = DeliverCardTextAsync(item);
    }

    private async Task DeliverCardTextAsync(BoardItem item)
    {
        DataObject payload;
        try
        {
            // 负载与复制通道同构（契约 #36）：单卡文字合并 + 内部条目标记（自动
            // 采集跳过，送达自身不产生重复卡）。
            payload = _dragPayload.BuildClipboardPayload([item]);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ShowStatus("无法送达这条内容：" + exception.Message);
            return;
        }

        _isTextDeliveryInFlight = true;
        try
        {
            var outcome = await _inputDelivery.DeliverAsync(payload);
            ShowStatus(outcome switch
            {
                InputDeliveryOutcome.Delivered => "已送达当前输入框。",
                InputDeliveryOutcome.DeliveredRestoreFailed => "已送达当前输入框（原剪贴板未能恢复）。",
                InputDeliveryOutcome.NoTarget => "没有可送达的输入窗口。",
                InputDeliveryOutcome.ClipboardWriteFailed => "剪贴板暂时不可用，请重试。",
                InputDeliveryOutcome.FocusFailed => "目标窗口无法置前，未送达。",
                _ => "送达未完成，请重试。"
            });
        }
        finally
        {
            _isTextDeliveryInFlight = false;
        }
    }
}
