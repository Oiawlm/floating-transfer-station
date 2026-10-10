using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 卡片操作按钮状态矩阵与单卡删除按钮（1.24.0，契约 #19）的锁定测试。
/// 矩阵信号 = (悬停 IsMouseOver, 选中 IsSelected, 置顶 IsPinned)：
/// - 未选中+悬停：选择/置顶/删除三钮显示可点（删除只删本卡）；
/// - 未选中+未悬停：仅已置顶的置顶钮常显可点（=取消置顶），其余隐藏；
/// - 选中（无论悬停）：选择钮常显可点；置顶钮未置顶隐藏、已置顶常显禁用
///   （纯状态徽章，点击不改置顶、不改选中）；删除钮一律隐藏。
/// 单卡删除走既有删除管线（撤销栈、原子持久化、失败恢复），不改选择集合、
/// 不滚动列表；指针路径一律 Win32 消息直驱（契约 #13），按钮可见性断言读
/// FadeAnimation.IsActive 与 IsHitTestVisible（淡入只改 Opacity 不改命中）。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private static ButtonBase CardOperationButton(ListBoxItem container, string commandParameter) =>
        FindDescendants<ButtonBase>(container).Single(button =>
            Equals(button.CommandParameter, commandParameter));

    private static Point ButtonCenterIn(ListBoxItem container, string commandParameter)
    {
        var button = CardOperationButton(container, commandParameter);
        return button.TranslatePoint(
            new Point(button.ActualWidth / 2, button.ActualHeight / 2),
            container);
    }

    /// <summary>真实光标悬停到容器坐标系指定点并等待淡入完成（不点击）。
    /// settle 取 300ms：淡入 120ms 完成后留余量，又不进入 ~400ms 的工具提示
    /// 初显窗口（实测工具提示开启会让悬停态闪断、按钮命中瞬间失效）。
    /// 窗口创建后的贴边重排是异步落定的，PointToScreen 与 SetCursorPos 之间
    /// 几何可能漂移：悬停未进入窗口时重取坐标重试（最多三次），仍失败才暴露。</summary>
    private static void HoverRealAt(
        MainWindow window,
        FrameworkElement container,
        Point positionInContainer,
        int settleMilliseconds = 300)
    {
        for (var attempt = 0; ; attempt++)
        {
            var screen = container.PointToScreen(positionInContainer);
            // 与 ClickRealAt 同源：强制一次位移保证 WM_MOUSEMOVE 必然生成。
            Assert.IsTrue(
                SetCursorPos((int)Math.Round(screen.X) + 7, (int)Math.Round(screen.Y) + 5),
                "真实光标必须可移动（强制位移预热）。");
            Assert.IsTrue(SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y)), "真实光标必须可移入窗口。");
            if (window.IsMouseOver)
            {
                break;
            }

            try
            {
                PumpUntilMouseOver(window);
                break;
            }
            catch (TimeoutException)
            {
                if (attempt >= 2)
                {
                    throw;
                }
            }
        }

        if (settleMilliseconds > 0)
        {
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(settleMilliseconds));
        }
    }

    /// <summary>把真实光标移到窗口左侧的确定性空域（窗口右缘贴齐，左侧必为
    /// 窗口外），等待悬停态消退。不用 savedCursor 当「远离」目标——它可能恰好
    /// 落在本窗口的卡片上，且跨运行自我延续。</summary>
    private static void MoveRealCursorAwayFromWindow(MainWindow window)
    {
        var away = window.PointToScreen(new Point(-160, 160));
        var x = Math.Max((int)Math.Round(away.X), 8);
        Assert.IsTrue(SetCursorPos(x + 6, (int)Math.Round(away.Y) + 4), "真实光标必须可移动（强制位移预热）。");
        Assert.IsTrue(SetCursorPos(x, (int)Math.Round(away.Y)), "真实光标必须可移出窗口。");
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
    }

    /// <summary>悬停直到条件成立（吸收窗口贴边重排残余漂移），重试后仍不成立即失败。</summary>
    private static void HoverRealUntil(
        MainWindow window,
        FrameworkElement container,
        Point positionInContainer,
        Func<bool> ready)
    {
        for (var attempt = 0; attempt < 3 && !ready(); attempt++)
        {
            HoverRealAt(window, container, positionInContainer);
        }

        Assert.IsTrue(ready(), "重试真实悬停后条件仍未成立（窗口几何或悬停路由漂移）。");
    }

    [STATestMethod]
    public void CardOperationMatrix_UnselectedHoverRevealsDeleteAndPinnedStaysVisible()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var plain = board.AddText("未选中卡片", BoardCategory.Inbox);
        var pinned = board.AddText("已置顶卡片", BoardCategory.Inbox);
        board.SetPinnedMany([pinned.Id], true);
        Assert.IsTrue(pinned.IsPinned, "前置条件：置顶成功。");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var plainContainer = ContainerOf(window, plain);
            var pinnedContainer = ContainerOf(window, pinned);
            // 真实光标路径前置条件（见 ClickRealAt）：Topmost 与布局定稿先行，
            // 否则窗口贴边重排会让同一物理点在悬停间漂移出目标卡片。
            window.Topmost = true;
            CompleteLayout(window);
            var plainDelete = CardOperationButton(plainContainer, CardGestureZones.DeleteCardCommand);
            var plainPin = CardOperationButton(plainContainer, CardGestureZones.TogglePinCommand);
            var pinnedDelete = CardOperationButton(pinnedContainer, CardGestureZones.DeleteCardCommand);
            var pinnedPin = CardOperationButton(pinnedContainer, CardGestureZones.TogglePinCommand);
            var savedCursor = SaveCursorPosition();

            try
            {
                // 基线（光标先确定性移出窗口）：未选中+未悬停 → 删除钮隐藏、
                // 未置顶置顶钮隐藏；已置顶+未悬停 → 置顶钮常显（=取消置顶）。
                MoveRealCursorAwayFromWindow(window);
                Assert.IsFalse(FadeAnimation.GetIsActive(plainDelete), "未悬停时删除钮必须隐藏。");
                Assert.IsFalse(plainDelete.IsHitTestVisible, "隐藏的删除钮必须不可命中。");
                Assert.IsFalse(FadeAnimation.GetIsActive(plainPin), "未置顶且未悬停时置顶钮必须隐藏。");
                Assert.IsTrue(FadeAnimation.GetIsActive(pinnedPin), "已置顶卡片的置顶钮未悬停也必须常显。");
                Assert.AreEqual("取消置顶", pinnedPin.ToolTip);
                Assert.IsFalse(FadeAnimation.GetIsActive(pinnedDelete), "未悬停时已置顶卡片的删除钮必须隐藏。");

                // 未选中+悬停：删除/置顶钮淡入且可命中（选择钮同规则既有锁定）。
                HoverRealUntil(
                    window,
                    plainContainer,
                    new Point(24, plainContainer.ActualHeight / 2),
                    () => FadeAnimation.GetIsActive(plainDelete));
                Assert.IsTrue(plainDelete.IsHitTestVisible, "悬停淡入后的删除钮必须可命中。");
                Assert.AreEqual(1d, plainDelete.Opacity, 0.01, "淡入完成后删除钮必须完全可见。");
                Assert.IsTrue(FadeAnimation.GetIsActive(plainPin), "未选中卡片悬停时置顶钮必须淡入。");
                Assert.AreEqual("删除", plainDelete.ToolTip, "删除钮 ToolTip 必须为「删除」。");

                // 移开光标：未选中卡片全部按钮回落隐藏。
                MoveRealCursorAwayFromWindow(window);
                Assert.IsFalse(FadeAnimation.GetIsActive(plainDelete), "移开后删除钮必须隐藏。");
                Assert.IsFalse(plainDelete.IsHitTestVisible, "隐藏的删除钮必须不可命中。");

                // 已置顶+悬停（仍未选中）：删除钮淡入（悬停 ∧ 未选中成立）。
                HoverRealUntil(
                    window,
                    pinnedContainer,
                    new Point(24, pinnedContainer.ActualHeight / 2),
                    () => FadeAnimation.GetIsActive(pinnedDelete));
                Assert.IsTrue(pinnedDelete.IsHitTestVisible, "已置顶未选中卡片悬停时删除钮必须可命中。");
                SaveVisualEvidence(
                    (Border)window.FindName("WindowShell"),
                    "card-delete-hover-unselected.png",
                    "FTS_CARD_DELETE_EVIDENCE_DIR");
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardOperationMatrix_SelectedCardHidesDeleteAndPinBadgeIsInert()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var plain = board.AddText("选中的未置顶卡", BoardCategory.Inbox);
        var pinned = board.AddText("选中的已置顶卡", BoardCategory.Inbox);
        board.SetPinnedMany([pinned.Id], true);
        Assert.IsTrue(pinned.IsPinned, "前置条件：置顶成功。");
        var window = CreateWindow(directory, board);
        DataObject? delivered = null;
        window.ClipboardWriterOverride = data =>
        {
            delivered = data;
            return true;
        };

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(plain);
            list.SelectedItems.Add(pinned);
            var plainContainer = ContainerOf(window, plain);
            var pinnedContainer = ContainerOf(window, pinned);
            window.Topmost = true;
            CompleteLayout(window);
            var savedCursor = SaveCursorPosition();

            try
            {
                // 选中+未置顶+悬停：置顶钮与删除钮一律隐藏、不可命中。
                HoverRealUntil(
                    window,
                    plainContainer,
                    new Point(24, plainContainer.ActualHeight / 2),
                    () => plainContainer.IsMouseOver);
                var plainDelete = CardOperationButton(plainContainer, CardGestureZones.DeleteCardCommand);
                var plainPin = CardOperationButton(plainContainer, CardGestureZones.TogglePinCommand);
                Assert.IsFalse(FadeAnimation.GetIsActive(plainDelete), "选中卡片的删除钮悬停也必须隐藏。");
                Assert.IsFalse(plainDelete.IsHitTestVisible, "选中卡片的删除钮必须不可命中。");
                Assert.IsFalse(FadeAnimation.GetIsActive(plainPin), "选中未置顶卡片的置顶钮必须隐藏。");
                Assert.IsFalse(plainPin.IsHitTestVisible, "选中未置顶卡片的置顶钮必须不可命中。");

                // 选中+已置顶+悬停：置顶徽章常显、可命中（吞掉点击防穿透）、
                // 换纯展示徽章模板（无交互反馈）并以次文字色呈现。
                HoverRealUntil(
                    window,
                    pinnedContainer,
                    new Point(24, pinnedContainer.ActualHeight / 2),
                    () => pinnedContainer.IsMouseOver);
                var pinnedDelete = CardOperationButton(pinnedContainer, CardGestureZones.DeleteCardCommand);
                var pinnedPin = CardOperationButton(pinnedContainer, CardGestureZones.TogglePinCommand);
                Assert.IsFalse(FadeAnimation.GetIsActive(pinnedDelete), "选中卡片的删除钮悬停也必须隐藏。");
                Assert.IsTrue(FadeAnimation.GetIsActive(pinnedPin), "选中已置顶卡片的置顶徽章必须常显。");
                Assert.IsTrue(pinnedPin.IsHitTestVisible, "徽章必须可命中以吞掉点击（防止落回右半选择手势）。");
                Assert.IsTrue(pinnedPin.IsEnabled, "徽章必须保持启用（禁用元素不可命中，点击会穿透）。");
                Assert.AreEqual(
                    window.FindResource("CardStatusBadgeTemplate"),
                    pinnedPin.Template,
                    "选中卡片的置顶钮必须换用纯展示徽章模板。");
                Assert.AreSame(
                    window.FindResource("SecondaryTextBrush"),
                    pinnedPin.Foreground,
                    "徽章必须以次文字色呈现（状态显示，不是操作按钮）。");
                Assert.AreEqual("多选中，用顶部按钮操作", pinnedPin.ToolTip);

                // 真实输入点击徽章：置顶状态与选中集合都不变（点击无任何效果）。
                ClickRealAt(
                    window,
                    pinnedContainer,
                    ButtonCenterIn(pinnedContainer, CardGestureZones.TogglePinCommand),
                    requireGestureHandled: false);
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
                Assert.IsTrue(pinned.IsPinned, "徽章点击不得改变置顶状态。");
                CollectionAssert.AreEquivalent(
                    new[] { plain, pinned },
                    list.SelectedItems.Cast<BoardItem>().ToArray(),
                    "徽章点击不得改变选中集合。");
                SaveVisualEvidence(
                    (Border)window.FindName("WindowShell"),
                    "card-delete-selected-badge.png",
                    "FTS_CARD_DELETE_EVIDENCE_DIR");

                // 合成/UIA 路径同矩阵：选中卡片的删除/置顶按钮点击无效。
                pinnedDelete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, pinnedDelete));
                plainDelete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, plainDelete));
                pinnedPin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, pinnedPin));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
                Assert.IsTrue(board.Items(BoardCategory.Inbox).Any(item => item.Id == plain.Id), "选中卡片的删除按钮合成点击不得删除。");
                Assert.IsTrue(board.Items(BoardCategory.Inbox).Any(item => item.Id == pinned.Id), "选中卡片的删除按钮合成点击不得删除。");
                Assert.IsTrue(pinned.IsPinned, "选中卡片的置顶徽章合成点击不得改置顶。");
                Assert.AreEqual(
                    "已置顶状态徽章（多选中，用顶部按钮操作）",
                    System.Windows.Automation.AutomationProperties.GetName(pinnedPin),
                    "徽章的自动化名称必须是状态语义而非可操作按钮。");
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public async Task CardDeleteButton_RealInputDeletesOnlyClickedCardPreservesSelectionScrollAndUndo()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        AddScrollableItems(board, BoardCategory.Inbox);
        var target = board.Items(BoardCategory.Inbox)[3];
        var keeper = board.Items(BoardCategory.Inbox)[4];
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var viewer = FindDescendant<ScrollViewer>(list);
            Assert.IsNotNull(viewer);
            list.SelectedItems.Add(keeper);
            window.Topmost = true;
            CompleteLayout(window);
            ScrollTo(window, viewer, 120);
            var offset = viewer.VerticalOffset;
            var container = ContainerOf(window, target);
            var savedCursor = SaveCursorPosition();

            try
            {
                // 守护（契约 #13）：真实输入管线，悬停淡入后的删除按钮命中路径。
                ClickRealAt(
                    window,
                    container,
                    ButtonCenterIn(container, CardGestureZones.DeleteCardCommand),
                    requireGestureHandled: false,
                    hoverSettleMilliseconds: 300);
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }

            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            // 保存后的选择恢复在更高优先级的调度队列上，补一次低优先级泵
            // 保证恢复已执行再断言（真实输入路径的点击分发晚于泵触发）。
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(150));
            CompleteLayout(window);

            Assert.IsFalse(
                board.Items(BoardCategory.Inbox).Any(item => item.Id == target.Id),
                "真实点击删除按钮必须删除该卡。");
            CollectionAssert.AreEquivalent(
                new[] { keeper },
                list.SelectedItems.Cast<BoardItem>().ToArray(),
                "删除未选中卡片不得改变当前选中集合。");
            Assert.AreEqual(offset, viewer.VerticalOffset, 0.5, "单卡删除不得滚动列表。");

            // Ctrl+Z 经窗口撤销入口整卡恢复（锚点插回原位）。
            Assert.IsTrue(await window.UndoLastDeleteFromPanelAsync());
            CompleteLayout(window);
            Assert.AreEqual(target.Id, board.Items(BoardCategory.Inbox)[3].Id, "撤销必须把被删卡片插回原位。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardDeleteButton_SaveFailureRestoresCardAndOriginalSelection()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var target = board.AddText("删除目标", BoardCategory.Inbox);
        var keeper = board.AddText("保留选中", BoardCategory.Inbox);
        var store = new RecordingBoardStore(directory.Root)
        {
            SaveFailure = new IOException("Injected failure.")
        };
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(keeper);
            var container = ContainerOf(window, target);
            var delete = CardOperationButton(container, CardGestureZones.DeleteCardCommand);

            delete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, delete));
            CompleteLayout(window);

            // 保存失败：整卡恢复进板面、不进撤销栈，选中集合保持删除前原样
            // （失败分支不得把选择改成被删卡本身）。
            CollectionAssert.AreEquivalent(
                new[] { target, keeper },
                board.Items(BoardCategory.Inbox).ToArray(),
                "保存失败必须整卡恢复。");
            CollectionAssert.AreEquivalent(
                new[] { keeper },
                list.SelectedItems.Cast<BoardItem>().ToArray(),
                "保存失败不得改变删除前的选中集合。");
            Assert.AreEqual(
                "删除未保存，内容已恢复。",
                ((MainWindowViewModel)window.DataContext).StatusText);

            // 恢复成功后重试（解除注入失败）：删除照常生效。重试前先泵完
            // 失败路径的收尾队列（_isDeletePending 复位在 Send 优先级上）；
            // 失败恢复的 Reset 会重建容器，按钮引用须重新解析。
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(150));
            store.SaveFailure = null;
            delete = CardOperationButton(ContainerOf(window, target), CardGestureZones.DeleteCardCommand);
            delete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, delete));
            PumpDispatcherUntil(window.Dispatcher, store.SaveCompleted.Task);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(150));
            Assert.IsFalse(
                board.Items(BoardCategory.Inbox).Any(item => item.Id == target.Id),
                "解除注入失败后重试删除必须生效。");
            CollectionAssert.AreEquivalent(
                new[] { keeper },
                list.SelectedItems.Cast<BoardItem>().ToArray(),
                "重试删除仍不得改变选中集合。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardDeleteButton_PressSlideOffCancelsAndRightClickInStripCopies()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("删除滑离取消", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);
        DataObject? delivered = null;
        window.ClipboardWriterOverride = data =>
        {
            delivered = data;
            return true;
        };

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            window.Topmost = true;
            CompleteLayout(window);
            var deletePoint = ButtonCenterIn(container, CardGestureZones.DeleteCardCommand);
            var savedCursor = SaveCursorPosition();

            try
            {
                // 真实输入：按住删除钮后滑离卡片抬键——按钮常规语义取消意图。
                HoverRealUntil(
                    window,
                    container,
                    deletePoint,
                    () => container.IsMouseOver);
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                var downScreen = container.PointToScreen(deletePoint);
                var downClient = new NativePoint((int)Math.Round(downScreen.X), (int)Math.Round(downScreen.Y));
                Assert.IsTrue(ScreenToClient(hwnd, ref downClient), "屏幕点到客户区换算必须成功。");
                var awayScreen = container.PointToScreen(new Point(24, container.ActualHeight / 2));
                Assert.IsTrue(
                    PostMessage(hwnd, WmLeftButtonDown, MkLbutton, MakeLParam(downClient.X, downClient.Y)),
                    "投递删除按下消息失败。");
                Assert.IsTrue(
                    SetCursorPos((int)Math.Round(awayScreen.X), (int)Math.Round(awayScreen.Y)),
                    "真实光标必须可滑离。");
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(80));
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
                    var awayClient = new NativePoint((int)Math.Round(awayScreen.X), (int)Math.Round(awayScreen.Y));
                    Assert.IsTrue(ScreenToClient(hwnd, ref awayClient), "滑离点客户区换算必须成功。");
                    Assert.IsTrue(
                        PostMessage(hwnd, WmLeftButtonUp, 0, MakeLParam(awayClient.X, awayClient.Y)),
                        "投递滑离抬起消息失败。");
                    PumpDispatcherUntil(window.Dispatcher, upSeen.Task);
                }
                finally
                {
                    window.RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, onUp);
                }

                Assert.IsNotNull(upArgs);
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
                Assert.IsTrue(
                    board.Items(BoardCategory.Inbox).Any(candidate => candidate.Id == item.Id),
                    "按住删除钮滑离释放必须取消删除意图。");

                // 操作条内（删除钮正上方）右键复制不受三列扩展影响：仍复制本卡、
                // 不改选择、不删除。
                ClickRealRightAt(window, container, deletePoint);
                Assert.IsNotNull(delivered, "操作条内右键必须复制该卡。");
                Assert.IsTrue(delivered!.GetDataPresent(DataFormats.UnicodeText), "复制的负载必须携带文字内容。");
                Assert.IsTrue(
                    board.Items(BoardCategory.Inbox).Any(candidate => candidate.Id == item.Id),
                    "右键复制不得删除卡片。");
                var list = (ListBox)window.FindName("BoardList");
                Assert.IsFalse(
                    list.SelectedItems.Cast<BoardItem>().Any(),
                    "右键复制不得改变选择。");
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }
}
