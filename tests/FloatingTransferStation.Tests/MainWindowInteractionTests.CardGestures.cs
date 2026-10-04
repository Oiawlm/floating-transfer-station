using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 卡片指针手势左右分区的交互契约测试（1.17.0 手势分区重构）。历史背景见
/// B-007（docs/observations.md）：旧实现的双击编辑唯一入口是
/// Control.MouseDoubleClick 挂载，而命中卡片的预览鼠标处理器把事件标记
/// Handled，实机双击的系统触发链被阻断。本文件全部用真实事件序列
/// （预览按下/抬起，timestamp 可控递增）驱动手势层，不依赖系统双击链。
/// 左半：双击编辑、拖拽起点，无选择语义；
/// 右半：单击 toggle 选择、Shift 范围选择、右键复制。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    /// <summary>手势层自维护双击窗：测试时钟按系统双击时限推进，保证确定性。</summary>
    private static int _cardClickClock;

    private static int AdvanceCardClickClock(int millis) => _cardClickClock += millis;

    /// <summary>与上一次点击拉开超过系统双击时限的独立单击时间戳。</summary>
    private static int NextIsolatedClickTimestamp() =>
        AdvanceCardClickClock(checked((int)GetDoubleClickTime() + 100));

    /// <summary>落在系统双击时限内的第二击时间戳。</summary>
    private static int NextPairedClickTimestamp() => AdvanceCardClickClock(60);

    /// <summary>定位卡片某分区根元素（模板 Tag 标记）作为真实事件的命中源。</summary>
    private static FrameworkElement ZoneSourceOf(ListBoxItem container, string zoneTag) =>
        FindDescendants<FrameworkElement>(container).Single(
            // 值相等：跨程序集的字符串常量实例不保证同一（.NET Core 不共享驻留池）。
            element => Equals(element.Tag, zoneTag));

    private static FrameworkElement ZoneHitSource(MainWindow window, BoardItem item, string zoneTag)
    {
        var container = RealizeCard(window, item);
        var hitSource = ZoneSourceOf(container, zoneTag);
        Assert.IsNotNull(hitSource, $"卡片模板必须存在分区标记 {zoneTag} 的命中元素。");
        return hitSource;
    }

    /// <summary>发出一次真实形态的点击：预览按下 + 预览抬起（timestamp 可控）。</summary>
    private static void RaiseCardPressAndRelease(UIElement hitSource, int timestamp)
    {
        var down = NewMouseButtonEventArgs(Mouse.PreviewMouseDownEvent, hitSource, timestamp);
        hitSource.RaiseEvent(down);
        Assert.IsTrue(down.Handled, "命中卡片的预览按下必须触达窗口手势处理器。");
        hitSource.RaiseEvent(NewMouseButtonEventArgs(
            Mouse.PreviewMouseUpEvent,
            hitSource,
            timestamp + 5));
    }

    /// <summary>一次独立单击（时间上远离此前点击，不构成双击）。</summary>
    private static void RaiseCardClick(UIElement hitSource) =>
        RaiseCardPressAndRelease(hitSource, NextIsolatedClickTimestamp());

    /// <summary>双击：第一击独立计时，第二击落在系统双击时限内、同一命中源。</summary>
    private static void RaiseCardDoubleClick(UIElement hitSource)
    {
        RaiseCardPressAndRelease(hitSource, NextIsolatedClickTimestamp());
        RaiseCardPressAndRelease(hitSource, NextPairedClickTimestamp());
    }

    [STATestMethod]
    public void CardGestures_RealSequenceDoubleClickOpensEditorWithoutSystemChain()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("真实序列双击", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");

            // B-007 守护：真实事件序列（两对预览按下/抬起，timestamp 递增、
            // 命中左半）必须进入就地编辑——手势层自维护双击判定，不依赖
            // Control.MouseDoubleClick 的系统触发链。
            RaiseCardDoubleClick(ZoneHitSource(window, item, CardGestureZones.ContentZone));
            CompleteLayout(window);

            Assert.AreEqual(Visibility.Visible, EditorHost(window).Visibility);
            Assert.AreEqual("真实序列双击", EditorInput(window).Text);
            Assert.IsTrue(EditorInput(window).IsKeyboardFocused);

            // 系统双击链挂载已删除：合成 MouseDoubleClick 事件不再是编辑入口。
            EditorHost(window).Visibility = Visibility.Collapsed;
            var other = board.AddText("另一张卡");
            CompleteLayout(window);
            list.RaiseEvent(NewMouseButtonEventArgs(
                Control.MouseDoubleClickEvent,
                ContainerOf(window, other)));
            CompleteLayout(window);

            Assert.AreEqual(
                Visibility.Collapsed,
                EditorHost(window).Visibility,
                "MouseDoubleClick 挂载必须已删除，编辑入口只有手势层。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardGestures_ContentZoneClicksHaveNoSelectionSemantics()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var first = board.AddText("第一张");
        var second = board.AddText("第二张");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(second);

            foreach (var modifiers in new[]
            {
                ModifierKeys.None,
                ModifierKeys.Control,
                ModifierKeys.Shift
            })
            {
                ClickCard(window, first, modifiers, CardGestureZones.ContentZone);
            }

            CollectionAssert.AreEqual(
                new[] { second },
                list.SelectedItems.Cast<BoardItem>().ToArray(),
                "左半单击（裸/Ctrl/Shift）不得产生任何选择语义。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardGestures_OperationsZoneBareClickTogglesOnlyHitItem()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var first = board.AddText("第一张");
        var second = board.AddText("第二张");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(second);

            // 裸单击右半：toggle 命中条目，不影响其他已选。
            ClickCard(window, first, ModifierKeys.None);
            CollectionAssert.AreEquivalent(
                new[] { first, second },
                list.SelectedItems.Cast<BoardItem>().ToArray(),
                "右半单击 toggle 命中条目且不影响其他已选。");

            // 再单击：取消选择，其他已选保留。
            ClickCard(window, first, ModifierKeys.None);
            CollectionAssert.AreEqual(
                new[] { second },
                list.SelectedItems.Cast<BoardItem>().ToArray(),
                "再次右半单击取消命中条目。");

            // 取证：右半单击选中一张、其他已选保留的选中态视觉。
            ClickCard(window, first, ModifierKeys.None);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "gesture-operations-click-selected.png",
                "FTS_GESTURE_EVIDENCE_DIR");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardGestures_OperationsZoneCtrlClickMatchesBareClick()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("Ctrl 单击");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");

            ClickCard(window, item, ModifierKeys.Control);
            CollectionAssert.AreEqual(
                new[] { item },
                list.SelectedItems.Cast<BoardItem>().ToArray(),
                "Ctrl+右半单击与裸单击等效 toggle。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardGestures_OperationsZoneDoubleClickTogglesExactlyOnce()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("右半双击");
        var other = board.AddText("另一张");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");

            // 右半双击净效果等同一次单击：第一击 toggle，第二击识别为双击不再
            // toggle（避免选中态闪烁）。
            RaiseCardDoubleClick(ZoneHitSource(window, item, CardGestureZones.OperationsZone));
            CollectionAssert.AreEqual(
                new[] { item },
                list.SelectedItems.Cast<BoardItem>().ToArray(),
                "右半双击只 toggle 一次。");

            Assert.AreEqual(
                string.Empty,
                StatusOf(window),
                "右半双击不触发编辑或复制等其他意图。");
            _ = other;
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardGestures_SecondClickBeyondDoubleClickWindowIsTwoSingleClicks()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("超窗双击", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var hitSource = ZoneHitSource(window, item, CardGestureZones.ContentZone);

            // 第二击超出系统双击时限：两次独立单击——左半单击无任何语义，
            // 不得进入编辑。
            RaiseCardPressAndRelease(hitSource, NextIsolatedClickTimestamp());
            RaiseCardPressAndRelease(hitSource, NextIsolatedClickTimestamp());
            CompleteLayout(window);

            Assert.AreEqual(
                Visibility.Collapsed,
                EditorHost(window).Visibility,
                "超窗双击必须判定为两次独立单击，不进入编辑。");

            // 对照：时限内的第二击进入编辑（同源同卡）。
            RaiseCardDoubleClick(hitSource);
            CompleteLayout(window);
            Assert.AreEqual(Visibility.Visible, EditorHost(window).Visibility);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardGestures_RealInputOperationsClickTogglesSelection()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("真实输入右半单击", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            window.Topmost = true;
            CompleteLayout(window);
            var clickPoint = ButtonCenterInContainer(
                container,
                button => Equals(button.CommandParameter, "ToggleSelection"));
            var savedCursor = SaveCursorPosition();

            try
            {
                // 守护（1.17.1 修复的实机缺陷）：悬停淡入后的按钮命中路径。
                // 旧实现早退放行原生按钮 Click，而 ListBox 默认单击选择会先
                // 处理并捕获冒泡按下，按钮收不到自己的抬起、Click 永不触发
                // ——合成/UIA 可达、真实输入不可达。现在手势层接管按钮命中，
                // 等淡入完成（按钮可命中）后单击必须 toggle 选中该卡。
                ClickRealAt(
                    window,
                    container,
                    clickPoint,
                    requireGestureHandled: false,
                    hoverSettleMilliseconds: 450);
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }

            var list = (ListBox)window.FindName("BoardList");
            CollectionAssert.AreEquivalent(
                new[] { item },
                list.SelectedItems.Cast<BoardItem>().ToArray(),
                "真实输入管线在选择按钮上的单击必须 toggle 选中该卡。");
            Assert.IsFalse(item.IsPinned, "选择按钮单击不得误触置顶。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardGestures_RealInputPinButtonTogglesPinWithoutTouchingSelection()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("真实输入置顶按钮", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            window.Topmost = true;
            CompleteLayout(window);
            var clickPoint = ButtonCenterInContainer(
                container,
                button => Equals(button.CommandParameter, "TogglePin"));
            var savedCursor = SaveCursorPosition();

            try
            {
                ClickRealAt(
                    window,
                    container,
                    clickPoint,
                    requireGestureHandled: false,
                    hoverSettleMilliseconds: 450);
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }

            // 手势分发对置顶是异步落盘；泵到状态翻转或超时。
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!item.IsPinned && DateTime.UtcNow < deadline)
            {
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
            }

            Assert.IsTrue(item.IsPinned, "真实输入管线在置顶按钮上的单击必须置顶该卡。");
            var list = (ListBox)window.FindName("BoardList");
            Assert.IsFalse(
                list.SelectedItems.Cast<BoardItem>().Any(),
                "置顶按钮单击不得改变选择。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    /// <summary>把卡片模板内指定操作按钮的中心换算到容器坐标系（真实输入点击点）。</summary>
    private static Point ButtonCenterInContainer(
        ListBoxItem container,
        Func<System.Windows.Controls.Primitives.ButtonBase, bool> match)
    {
        var button = FindDescendants<System.Windows.Controls.Primitives.ButtonBase>(container).Single(match);
        return button.TranslatePoint(
            new Point(button.ActualWidth / 2, button.ActualHeight / 2),
            container);
    }

    [STATestMethod]
    public void CardGestures_RealInputDoubleClickOpensEditor()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("真实输入双击", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            // 先稳定窗口几何（置顶与贴边重排定稿），两击的同一物理点才不会
            // 因窗口移动而在双击位置容差内漂移。
            window.Topmost = true;
            CompleteLayout(window);
            var center = new Point(24, container.ActualHeight / 2);
            var savedCursor = SaveCursorPosition();

            try
            {
                // 预热一击：首次悬停/激活会触发贴边取整等几何重排，同一物理点
                // 在重排前后的窗口相对坐标会漂移；先让几何定稿，再拉开与预热
                // 击的时间差，正式两击在稳定几何上判定双击。
                ClickRealAt(window, container, center);
                PumpDispatcherFor(
                    window.Dispatcher,
                    TimeSpan.FromMilliseconds(GetDoubleClickTime() + 100));

                // 端到端守护（B-007）：Win32 鼠标消息直驱窗口，走完整 WPF 输入
                // 管线（真实时间戳/位置/ClickCount 链）。真实双击必须进入编辑——
                // 旧实现正是死在这条链上（预览 Handled 阻断 MouseDoubleClick）。
                ClickRealAt(window, container, center);
                ClickRealAt(window, container, center);
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }

            Assert.AreEqual(
                Visibility.Visible,
                EditorHost(window).Visibility,
                "真实输入管线的双击必须进入就地编辑。");
            Assert.AreEqual("真实输入双击", EditorInput(window).Text);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "gesture-real-double-click-editing.png",
                "FTS_GESTURE_EVIDENCE_DIR");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardGestures_SecondClickTooFarAwayIsTwoSingleClicks()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("位置过远双击", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            // 先稳定窗口几何（置顶与贴边重排定稿），两击的物理点换算才一致。
            window.Topmost = true;
            CompleteLayout(window);
            var savedCursor = SaveCursorPosition();

            try
            {
                // 预热一击稳定几何（同 RealInput 双击测试），并拉开双击窗口。
                ClickRealAt(window, container, new Point(24, container.ActualHeight / 2));
                PumpDispatcherFor(
                    window.Dispatcher,
                    TimeSpan.FromMilliseconds(GetDoubleClickTime() + 100));

                // 双击位置容差按系统 SM_CX/CYDOUBLECLK 判定：真实输入把两次点击
                // 打到同一卡片左半上下两端（相距超过容差），必须判为两次独立
                // 单击，不进入编辑。
                ClickRealAt(window, container, new Point(24, container.ActualHeight * 0.2));
                ClickRealAt(window, container, new Point(24, container.ActualHeight * 0.8));
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }

            CompleteLayout(window);
            Assert.AreEqual(
                Visibility.Collapsed,
                EditorHost(window).Visibility,
                "位置过远的第二击必须判为独立单击，不进入编辑。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardGestures_DragSuppressesClickSemanticsAndDoubleClickTrace()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("拖拽后点击");
        var other = board.AddText("另一张");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(other);
            var hitSource = ZoneHitSource(window, item, CardGestureZones.OperationsZone);

            // 模拟超过拖拽阈值的按压会话：Up 不分发任何点击语义。
            var down = NewMouseButtonEventArgs(
                Mouse.PreviewMouseDownEvent,
                hitSource,
                NextIsolatedClickTimestamp());
            hitSource.RaiseEvent(down);
            typeof(MainWindow).GetField(
                "_dragThresholdCrossed",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(window, true);
            hitSource.RaiseEvent(NewMouseButtonEventArgs(
                Mouse.PreviewMouseUpEvent,
                hitSource,
                down.Timestamp + 5));

            CollectionAssert.AreEqual(
                new[] { other },
                list.SelectedItems.Cast<BoardItem>().ToArray(),
                "拖拽发生后的 Up 不得触发点击语义。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private const int MkLbutton = 0x0001;

    /// <summary>
    /// 真实输入管线点击：把真实光标移入窗口（HwndMouseInputProvider 对光标
    /// 不在窗口上的注入消息有"Spurious mouse event"丢弃防护），确认悬停
    /// 激活后再投递 Win32 鼠标消息。事件携带真实时间戳与位置；等待用事件
    /// 驱动（Up 路由完成即返回）而非固定时长——两击间隔必须稳定落在系统
    /// 双击时限内。调用前必须完成 Topmost 与布局定稿，否则窗口贴边重排会
    /// 使同一物理点在两击间的窗口相对位置漂移、干扰双击位置容差。
    /// positionInContainer 为卡片容器坐标系内的点。
    /// </summary>
    private static void ClickRealAt(
        MainWindow window,
        FrameworkElement container,
        Point positionInContainer,
        bool requireGestureHandled = true,
        int hoverSettleMilliseconds = 0)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var screen = container.PointToScreen(positionInContainer);
        var client = new NativePoint((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
        Assert.IsTrue(ScreenToClient(hwnd, ref client), "屏幕点到客户区换算必须成功。");
        Assert.IsTrue(SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y)), "真实光标必须可移入窗口。");
        PumpUntilMouseOver(window);
        if (hoverSettleMilliseconds > 0)
        {
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(hoverSettleMilliseconds));
        }
        MouseButtonEventArgs? upArgs = null;
        var upSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MouseButtonEventHandler onUp = (_, e) =>
        {
            upArgs = e;
            upSeen.TrySetResult();
        };
        window.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, onUp, true);
        try
        {
            var lParam = MakeLParam(client.X, client.Y);
            Assert.IsTrue(PostMessage(hwnd, WmLeftButtonDown, MkLbutton, lParam), "投递按下消息失败。");
            Assert.IsTrue(PostMessage(hwnd, WmLeftButtonUp, 0, lParam), "投递抬起消息失败。");
            PumpDispatcherUntil(window.Dispatcher, upSeen.Task);
        }
        finally
        {
            window.RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, onUp);
        }

        // 路由同步完成后 Handled 已定型：手势层的 Up 处理器对命中卡片置 true，
        // 据此确认真实消息没有被输入管线丢弃。按钮命中路径（右半按钮淡入后
        // 可命中）由按钮命令承接，Up 不经手势层置 Handled。
        Assert.IsNotNull(upArgs);
        if (requireGestureHandled)
        {
            Assert.IsTrue(upArgs.Handled, "真实点击必须被手势层处理（按下命中卡片）。");
        }
    }

    /// <summary>等待 WPF 认定真实光标已悬停在窗口上（MouseEnter 处理完成）。</summary>
    private static void PumpUntilMouseOver(MainWindow window)
    {
        if (window.IsMouseOver)
        {
            return;
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MouseEventHandler onEnter = (_, _) => entered.TrySetResult();
        window.AddHandler(UIElement.MouseEnterEvent, onEnter);
        try
        {
            PumpDispatcherUntil(window.Dispatcher, entered.Task);
        }
        finally
        {
            window.RemoveHandler(UIElement.MouseEnterEvent, onEnter);
        }

        Assert.IsTrue(window.IsMouseOver, "真实光标移入后窗口必须进入悬停态。");
    }

    private static nint MakeLParam(int low, int high) =>
        (nint)((high << 16) | (low & 0xFFFF));

    private static NativePoint SaveCursorPosition()
    {
        Assert.IsTrue(GetCursorPos(out var point), "测试环境必须支持读取真实光标。");
        return point;
    }

    /// <summary>
    /// 真实光标是跨测试持续存在的全局状态：悬停在后续测试窗口上会让
    /// IsMouseOver 为真，面板收起类测试即被抑制。移动过它的测试必须
    /// 恢复原位，恢复失败须立即暴露而非静默污染后续运行。
    /// </summary>
    private static void RestoreCursorPosition(NativePoint point) =>
        Assert.IsTrue(SetCursorPos(point.X, point.Y), "真实光标恢复失败会污染后续测试。");

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint hWnd, ref NativePoint point);
}
