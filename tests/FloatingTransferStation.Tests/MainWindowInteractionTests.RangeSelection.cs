using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    [DataRow(0, 3)]
    [DataRow(3, 0)]
    public void RangeSelection_ShiftRightClickSelectsDisplayedIntervalWithoutSaving(int anchorIndex, int targetIndex)
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        for (var index = 4; index >= 0; index--)
        {
            board.AddText($"卡片 {index + 1}");
        }

        var items = board.Items(BoardCategory.Inbox).ToArray();
        board.SetPinnedMany([items[0].Id], true);
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            ClickSelectionButton(window, items[4]);
            ClickSelectionButton(window, items[anchorIndex]);
            if (anchorIndex == 0)
            {
                InvokePrivate(window, "SetHeaderActionsVisible", true);
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
                CompleteLayout(window);
                SaveVisualEvidence((Border)window.FindName("WindowShell"),
                    "before-shift-click.png", "FTS_RANGE_SELECTION_EVIDENCE_DIR");
            }

            RightClickCard(window, items[targetIndex], ModifierKeys.Shift);
            CompleteLayout(window);
            if (anchorIndex == 0)
            {
                // 等选中徽章淡入定稿再取证（与 before 捕获同一节奏），否则
                // RenderTargetBitmap 会把淡入中程渲染成未选中。
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
                CompleteLayout(window);
                SaveVisualEvidence((Border)window.FindName("WindowShell"),
                    "after-shift-click.png", "FTS_RANGE_SELECTION_EVIDENCE_DIR");
            }

            CollectionAssert.AreEquivalent(items[..4], list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual("4", ((TextBlock)window.FindName("SelectedCountText")).Text);
            Assert.AreEqual("删除已选 4 项", ((Button)window.FindName("DeleteContentButton")).ToolTip);
            CollectionAssert.AreEqual(items, board.Items(BoardCategory.Inbox).ToArray());
            Assert.IsTrue(items[0].IsPinned);
            Assert.AreEqual(0, store.SaveCount);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void RangeSelection_ControlAnchorSupportsAddingAndShrinkingRanges()
    {
        using var directory = new TestDirectory();
        var board = CreateRangeSelectionBoard();
        var items = board.Items(BoardCategory.Inbox).ToArray();
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            var list = (ListBox)window.FindName("BoardList");
            RightClickCard(window, items[4], ModifierKeys.Control);
            RightClickCard(window, items[1], ModifierKeys.Control);
            RightClickCard(window, items[2], ModifierKeys.Control | ModifierKeys.Shift);
            CollectionAssert.AreEquivalent(new[] { items[1], items[2], items[4] },
                list.SelectedItems.Cast<BoardItem>().ToArray());

            RightClickCard(window, items[3], ModifierKeys.Shift);
            CollectionAssert.AreEquivalent(items[1..4], list.SelectedItems.Cast<BoardItem>().ToArray());
            RightClickCard(window, items[2], ModifierKeys.Shift);
            CollectionAssert.AreEquivalent(items[1..3], list.SelectedItems.Cast<BoardItem>().ToArray());
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    [DataRow("missing")]
    [DataRow("deselected")]
    [DataRow("removed")]
    [DataRow("category-switch")]
    [DataRow("escape")]
    [DataRow("background")]
    public void RangeSelection_InvalidatedAnchorStartsAtTarget(string invalidation)
    {
        using var directory = new TestDirectory();
        var board = CreateRangeSelectionBoard();
        var items = board.Items(BoardCategory.Inbox).ToArray();
        board.AddText("其他分类", BoardCategory.Reference);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            if (invalidation != "missing")
            {
                ClickSelectionButton(window, items[0]);
            }

            switch (invalidation)
            {
                case "deselected":
                    ClickSelectionButton(window, items[0]);
                    break;
                case "removed":
                    board.MoveMany([items[0].Id], BoardCategory.Reference, 0);
                    break;
                case "category-switch":
                    EnterCategory(window, BoardCategory.Reference);
                    CompleteLayout(window);
                    EnterCategory(window, BoardCategory.Inbox);
                    CompleteLayout(window);
                    ApplicationCommands.SelectAll.Execute(null, window);
                    break;
                case "escape":
                    window.RaiseEvent(NewKeyEventArgs(window, Key.Escape));
                    ApplicationCommands.SelectAll.Execute(null, window);
                    break;
                case "background":
                    list.RaiseEvent(NewMouseButtonEventArgs(Mouse.PreviewMouseDownEvent, list));
                    ApplicationCommands.SelectAll.Execute(null, window);
                    break;
            }

            RightClickCard(window, items[3], ModifierKeys.Shift);
            CollectionAssert.AreEqual(new[] { items[3] }, list.SelectedItems.Cast<BoardItem>().ToArray());
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void RangeSelection_CollapsePreservesAnchorButHiddenClickDoesNotSelect()
    {
        using var directory = new TestDirectory();
        var board = CreateRangeSelectionBoard();
        var items = board.Items(BoardCategory.Inbox).ToArray();
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            ClickSelectionButton(window, items[1]);
            var target = RealizeCard(window, items[3]);
            var hitSource = ZoneSourceOf(target, CardGestureZones.OperationsZone);
            CollapseForSetup(window);
            var list = (ListBox)window.FindName("BoardList");
            Assert.IsFalse(((MainWindowViewModel)window.DataContext).IsPanelExpanded);
            // 面板收起后的隐藏卡不得响应右键范围选择（手势交换后选择走右键）。
            WithThreadKeyboardModifiers(ModifierKeys.Shift, () => RaiseRightCardClick(window, hitSource));
            CollectionAssert.AreEqual(new[] { items[1] }, list.SelectedItems.Cast<BoardItem>().ToArray());

            ExpandCategory(window, BoardCategory.Inbox);
            RightClickCard(window, items[3], ModifierKeys.Shift);
            CollectionAssert.AreEquivalent(items[1..4], list.SelectedItems.Cast<BoardItem>().ToArray());
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void RangeSelection_RightClickUsesUpModifiersAndDragPreservesOriginalPayload()
    {
        using var directory = new TestDirectory();
        var board = CreateRangeSelectionBoard();
        var items = board.Items(BoardCategory.Inbox).ToArray();
        var window = CreateWindow(directory, board);
        DataObject? captured = null;
        window.ClipboardWriterOverride = data =>
        {
            captured = data;
            return true;
        };

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            ClickSelectionButton(window, items[0]);
            ClickSelectionButton(window, items[2]);
            var list = (ListBox)window.FindName("BoardList");
            var target = RealizeCard(window, items[0]);
            var hitSource = ZoneSourceOf(target, CardGestureZones.OperationsZone);
            // 手势交换（Issue #216）：左键修饰键不再分流选择——Shift 按下的
            // 左键会话照常起拖，拖拽负载仍按既有选择构建。
            WithThreadKeyboardModifiers(ModifierKeys.Shift, () =>
                hitSource.RaiseEvent(NewMouseButtonEventArgs(
                    Mouse.PreviewMouseDownEvent,
                    hitSource,
                    NextIsolatedClickTimestamp())));
            CollectionAssert.AreEquivalent(new[] { items[0], items[2] },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            var data = GetPrivateMethod("BuildInternalDragData")!.Invoke(window, [items[0]]) as DataObject;
            Assert.IsNotNull(data);
            CollectionAssert.AreEqual(new[] { items[0].Id, items[2].Id },
                new DragPayloadService().GetInternalItemIds(data)!.ToArray());
            typeof(MainWindow).GetField("_dragThresholdCrossed",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(window, true);
            target.RaiseEvent(NewMouseButtonEventArgs(
                Mouse.PreviewMouseUpEvent,
                target,
                NextIsolatedClickTimestamp()));
            Assert.IsNull(captured, "成拖拽的右半左键 Up 不得触发复制。");
            CollectionAssert.AreEquivalent(new[] { items[0], items[2] },
                list.SelectedItems.Cast<BoardItem>().ToArray());

            // 右键分发在 Up、无按压会话：修饰键取 Up 时键盘态——裸按下后
            // Shift 抬起同样按 Shift 分发锚点范围选择。右键专用隧道事件在
            // BoardList 上 Raise（Source=分区命中元素）。
            hitSource = ZoneSourceOf(RealizeCard(window, items[4]), CardGestureZones.OperationsZone);
            var rightList = (ListBox)window.FindName("BoardList")!;
            WithThreadKeyboardModifiers(ModifierKeys.None, () =>
                rightList.RaiseEvent(NewRightButtonArgs(
                    UIElement.PreviewMouseRightButtonDownEvent,
                    hitSource,
                    NextIsolatedClickTimestamp())));
            WithThreadKeyboardModifiers(ModifierKeys.Shift, () =>
                rightList.RaiseEvent(NewRightButtonArgs(
                    UIElement.PreviewMouseRightButtonUpEvent,
                    hitSource,
                    NextIsolatedClickTimestamp())));
            CollectionAssert.AreEquivalent(items[2..5], list.SelectedItems.Cast<BoardItem>().ToArray());
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void RangeSelection_IncludesVirtualizedCardsInCurrentDisplayOrder()
    {
        using var directory = new TestDirectory();
        var board = CreateRangeSelectionBoard(40);
        var items = board.Items(BoardCategory.Inbox).ToArray();
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            ClickSelectionButton(window, items[0]);
            RealizeCard(window, items[^1]);
            var list = (ListBox)window.FindName("BoardList");
            Assert.IsNull(list.ItemContainerGenerator.ContainerFromItem(items[20]),
                "The range must include a card without a realized container.");
            RightClickCard(window, items[^1], ModifierKeys.Shift);

            CollectionAssert.AreEquivalent(items, list.SelectedItems.Cast<BoardItem>().ToArray());
            CollectionAssert.AreEqual(items, board.Items(BoardCategory.Inbox).ToArray());
            Assert.AreEqual(0, store.SaveCount);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RangeSelection_SlowPinCompletionPreservesNewRange(bool batch)
    {
        using var directory = new TestDirectory();
        var board = CreateRangeSelectionBoard();
        var items = board.Items(BoardCategory.Inbox).ToArray();
        var store = new BlockingFirstSuccessfulSaveBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            ClickSelectionButton(window, items[0]);
            // 状态矩阵（契约 #19）：单卡置顶只作用于未选中卡片——非批量路径
            // 置顶未选中的 items[4]，批量路径仍置顶已选集合。
            var pin = batch
                ? (Button)window.FindName("BatchPinButton")
                : FindDescendants<Button>(RealizeCard(window, items[4]))
                    .Single(button => Equals(button.CommandParameter, CardGestureZones.TogglePinCommand));
            pin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, pin));
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveStarted.Task);
            RightClickCard(window, items[3], ModifierKeys.Shift);
            var list = (ListBox)window.FindName("BoardList");
            CollectionAssert.AreEquivalent(items[..4], list.SelectedItems.Cast<BoardItem>().ToArray());

            store.ReleaseFirstSave();
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveCompleted.Task);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);
            CollectionAssert.AreEquivalent(items[..4], list.SelectedItems.Cast<BoardItem>().ToArray());
        }
        finally
        {
            store.ReleaseFirstSave();
            try
            {
                CloseWindow(window);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
    }

    [STATestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RangeSelection_FailedPinRestoresContentAndLatestRange(bool batch)
    {
        using var directory = new TestDirectory();
        var board = CreateRangeSelectionBoard();
        var items = board.Items(BoardCategory.Inbox).ToArray();
        var store = new UiThreadFailingFirstSaveBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            ClickSelectionButton(window, items[0]);
            // 状态矩阵（契约 #19）：单卡置顶只作用于未选中卡片——非批量路径
            // 置顶未选中的 items[4]，批量路径仍置顶已选集合。
            var pin = batch
                ? (Button)window.FindName("BatchPinButton")
                : FindDescendants<Button>(RealizeCard(window, items[4]))
                    .Single(button => Equals(button.CommandParameter, CardGestureZones.TogglePinCommand));
            pin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, pin));
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveStarted.Task);
            RightClickCard(window, items[3], ModifierKeys.Shift);

            store.FailFirstSave();
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);
            Assert.IsFalse(items[0].IsPinned);
            Assert.IsFalse(items[4].IsPinned);
            CollectionAssert.AreEqual(items, board.Items(BoardCategory.Inbox).ToArray());
            var list = (ListBox)window.FindName("BoardList");
            CollectionAssert.AreEquivalent(items[..4], list.SelectedItems.Cast<BoardItem>().ToArray());
        }
        finally
        {
            store.FailFirstSave();
            try
            {
                CloseWindow(window);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
    }

    [STATestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RangeSelection_DeleteCompletionPreservesLaterSelectionOfRemainingCards(bool failSave)
    {
        using var directory = new TestDirectory();
        var board = CreateRangeSelectionBoard();
        var items = board.Items(BoardCategory.Inbox).ToArray();
        var successStore = new BlockingFirstSuccessfulSaveBoardStore(directory.Root);
        var failureStore = new UiThreadFailingFirstSaveBoardStore(directory.Root);
        IBoardStore store = failSave ? failureStore : successStore;
        var saveStarted = failSave ? failureStore.FirstSaveStarted.Task : successStore.FirstSaveStarted.Task;
        Action completeSave = failSave ? failureStore.FailFirstSave : successStore.ReleaseFirstSave;
        var window = CreateWindow(board, store, WindowSettings.Default);
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            ClickSelectionButton(window, items[0]);
            window.RaiseEvent(NewKeyEventArgs(window, Key.Delete));
            PumpDispatcherUntil(window.Dispatcher, saveStarted);
            ClickSelectionButton(window, items[2]);
            RightClickCard(window, items[3], ModifierKeys.Shift);

            var delete = (Button)window.FindName("DeleteContentButton");
            var completed = new TaskCompletionSource();
            delete.IsEnabledChanged += (_, _) =>
            {
                if (delete.IsEnabled)
                {
                    completed.TrySetResult();
                }
            };
            completeSave();
            PumpDispatcherUntil(window.Dispatcher, completed.Task);
            var list = (ListBox)window.FindName("BoardList");
            CollectionAssert.AreEquivalent(items[2..4], list.SelectedItems.Cast<BoardItem>().ToArray());
            CollectionAssert.AreEqual(failSave ? items : items[1..], board.Items(BoardCategory.Inbox).ToArray());
            Assert.AreEqual("删除已选 2 项", delete.ToolTip);
        }
        finally
        {
            completeSave();
            try
            {
                CloseWindow(window);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
    }

    [STATestMethod]
    [DataRow("category-switch")]
    [DataRow("unselected-drag")]
    public void RangeSelection_ClearedDuringSlowSaveIsNotRevived(string clearAction)
    {
        using var directory = new TestDirectory();
        var board = CreateRangeSelectionBoard();
        var items = board.Items(BoardCategory.Inbox).ToArray();
        board.AddText("其他分类", BoardCategory.Reference);
        var store = new BlockingFirstSuccessfulSaveBoardStore(directory.Root);
        var window = CreateWindow(board, store, WindowSettings.Default);
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            ClickSelectionButton(window, items[0]);
            var pin = (Button)window.FindName("BatchPinButton");
            pin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, pin));
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveStarted.Task);
            RightClickCard(window, items[3], ModifierKeys.Shift);
            if (clearAction == "category-switch")
            {
                EnterCategory(window, BoardCategory.Reference);
                CompleteLayout(window);
                EnterCategory(window, BoardCategory.Inbox);
                CompleteLayout(window);
            }
            else
            {
                var data = GetPrivateMethod("BuildInternalDragData")!.Invoke(window, [items[4]]) as DataObject;
                Assert.IsNotNull(data);
                Assert.AreEqual(items[4].Id, new DragPayloadService().GetInternalItemId(data));
            }

            var list = (ListBox)window.FindName("BoardList");
            Assert.AreEqual(0, list.SelectedItems.Count);
            store.ReleaseFirstSave();
            PumpDispatcherUntil(window.Dispatcher, store.FirstSaveCompleted.Task);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(100));
            CompleteLayout(window);

            Assert.AreEqual(0, list.SelectedItems.Count);
            Assert.AreEqual("左键清空非置顶，右键清空全部", ((Button)window.FindName("DeleteContentButton")).ToolTip);
        }
        finally
        {
            store.ReleaseFirstSave();
            try
            {
                CloseWindow(window);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
    }

    private static BoardService CreateRangeSelectionBoard(int count = 5)
    {
        var board = new BoardService();
        for (var index = count; index > 0; index--)
        {
            board.AddText($"合成卡片 {index}");
        }

        return board;
    }

    private static void ClickSelectionButton(MainWindow window, BoardItem item)
    {
        var container = RealizeCard(window, item);
        var button = FindDescendants<Button>(container)
            .Single(candidate => Equals(candidate.CommandParameter, "ToggleSelection"));
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    }

    private static ListBoxItem RealizeCard(MainWindow window, BoardItem item)
    {
        var list = (ListBox)window.FindName("BoardList");
        list.ScrollIntoView(item);
        CompleteLayout(window);
        var container = list.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
        Assert.IsNotNull(container);
        return container;
    }

    /// <summary>
    /// 左键单击卡片指定分区（手势交换，Issue #216：右半左键＝复制；左半单击
    /// 无选择语义，由 CardGestures 契约测试单独覆盖）。时间戳与此前点击
    /// 拉开超过系统双击时限，保证各次单击互相独立。
    /// </summary>
    private static void LeftClickCard(
        MainWindow window,
        BoardItem item,
        ModifierKeys modifiers,
        string? zoneTag = null)
    {
        var hitSource = ZoneHitSource(window, item, zoneTag ?? CardGestureZones.OperationsZone);
        WithThreadKeyboardModifiers(modifiers, () => RaiseCardClick(hitSource));
    }

    /// <summary>
    /// 右键单击卡片指定分区（手势交换，Issue #216：选择手势只属于右键——
    /// 裸/Ctrl toggle、Shift 锚点范围、Ctrl+Shift 追加范围；左半右键无选择
    /// 语义）。右键分发在 Up 且无按压会话，修饰键取 Up 时键盘态。
    /// 右键专用隧道事件须在 BoardList 上 Raise（Source=分区命中元素）——在
    /// 后代元素上直接 Raise 不触达 BoardList 的处理器（与通用左键事件不同）。
    /// </summary>
    private static void RightClickCard(
        MainWindow window,
        BoardItem item,
        ModifierKeys modifiers,
        string? zoneTag = null)
    {
        var hitSource = ZoneHitSource(window, item, zoneTag ?? CardGestureZones.OperationsZone);
        WithThreadKeyboardModifiers(
            modifiers,
            () => RaiseRightCardClick(window, hitSource));
    }

    /// <summary>右键真实形态单击：预览按下 + 预览抬起（timestamp 可控递增）。</summary>
    private static void RaiseRightCardClick(MainWindow window, UIElement hitSource) =>
        RaiseRightCardPressAndRelease(window, hitSource, NextIsolatedClickTimestamp());

    private static void RaiseRightCardPressAndRelease(MainWindow window, UIElement hitSource, int timestamp)
    {
        var list = (ListBox)window.FindName("BoardList")!;
        list.RaiseEvent(NewRightButtonArgs(
            UIElement.PreviewMouseRightButtonDownEvent,
            hitSource,
            timestamp));
        list.RaiseEvent(NewRightButtonArgs(
            UIElement.PreviewMouseRightButtonUpEvent,
            hitSource,
            timestamp + 5));
    }

    private static void WithThreadKeyboardModifiers(ModifierKeys modifiers, Action action)
    {
        // SetKeyboardState affects only this STA thread, without sending system input.
        // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setkeyboardstate
        var previous = new byte[256];
        Assert.IsTrue(GetKeyboardState(previous));
        var current = (byte[])previous.Clone();
        foreach (var key in new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
        {
            current[key] = 0;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            current[0x10] = current[0xA0] = 0x80;
        }

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            current[0x11] = current[0xA2] = 0x80;
        }

        try
        {
            Assert.IsTrue(SetKeyboardState(current));
            Assert.AreEqual(modifiers, Keyboard.Modifiers);
            action();
        }
        finally
        {
            Assert.IsTrue(SetKeyboardState(previous));
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState([Out] byte[] state);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState(byte[] state);
}
