using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 设置窗口「标签顺序」节（1.25.0）锁定测试：四行固定列表按显示顺序渲染（名字随
/// 分类名）；上移/下移按钮为无障碍等价操作且提交即落 settings.json（契约 #5）；
/// 拖拽提交换序；拖拽会话中的 Esc 由节优先消费（回弹原序不提交、不关窗）；捕获
/// 丢失回弹原序。
/// </summary>
[TestClass]
public sealed class SettingsWindowCategoryOrderTests
{
    [STATestMethod]
    public void OrderSection_ShowsDisplayOrderWithNamesAndBoundedMoveButtons()
    {
        using var directory = new TestDirectory();
        var settingsOrder = new[]
        {
            BoardCategory.Inbox,
            BoardCategory.Prompt,
            BoardCategory.Reference,
            BoardCategory.CustomerOriginal
        };
        var (window, store) = CreateContext(directory, s => s.WithCategoryOrder(settingsOrder));

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);

            var rows = GetOrderRows(settings);
            CollectionAssert.AreEqual(
                settingsOrder,
                rows.Cast<Border>().Select(row => (BoardCategory)row.Tag).ToArray(),
                "顺序列表必须按当前显示顺序渲染四行。");
            Assert.AreEqual(
                "待分类",
                FindDescendants<TextBlock>(rows[0])
                    .Single(t => AutomationProperties.GetName(t)!.StartsWith("标签")).Text,
                "行显示名必须经分类名读取。");

            var firstRow = rows[0];
            Assert.AreEqual(
                false,
                FindDescendants<Button>(firstRow).Single(b => Equals(b.Content, "↑")).IsEnabled,
                "首行的上移按钮必须禁用。");
            var lastRow = (Border)rows[^1];
            Assert.AreEqual(
                false,
                FindDescendants<Button>(lastRow).Single(b => Equals(b.Content, "↓")).IsEnabled,
                "末行的下移按钮必须禁用。");
            Assert.IsNotNull(
                FindDescendants<FrameworkElement>(firstRow)
                    .FirstOrDefault(element => AutomationProperties.GetName(element)!.StartsWith("拖动")),
                "拖拽把手必须有自动化名称。");
            _ = store;
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public async Task MoveUpButton_CommitsImmediatelyPersistsAndRerailsPanel()
    {
        using var directory = new TestDirectory();
        var (window, store) = CreateContext(directory, s => s);

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);

            var secondRow = GetOrderRows(settings)[1];
            FindDescendants<Button>(secondRow).Single(b => Equals(b.Content, "↑"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            // 默认序 [图片, 复盘, 文本, 待分类]：第二行「复盘」上移一位。
            var expected = new[]
            {
                BoardCategory.Reference,
                BoardCategory.CustomerOriginal,
                BoardCategory.Prompt,
                BoardCategory.Inbox
            };
            var viewModel = (MainWindowViewModel)window.DataContext;
            await PumpUntilCompleted(window.Dispatcher);
            CompleteLayout(window);

            CollectionAssert.AreEqual(
                expected,
                viewModel.Categories.Select(panel => panel.Category).ToArray(),
                "上移提交必须立即重排面板标签轨。");
            Assert.IsNotNull(store.LastSettings, "提交必须落 settings.json。");
            CollectionAssert.AreEqual(expected, store.LastSettings!.DisplayOrder.ToArray());
            var rows = GetOrderRows(settings);
            Assert.AreEqual(
                expected[0],
                (BoardCategory)rows[0].Tag,
                "提交后行列表必须按新顺序重建。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public async Task MoveDownButton_IsEquivalenceOperationOfUp()
    {
        using var directory = new TestDirectory();
        var (window, store) = CreateContext(directory, s => s);

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);

            var firstRow = (Border)GetOrderRows(settings)[0];
            FindDescendants<Button>(firstRow).Single(b => Equals(b.Content, "↓"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var expected = new[]
            {
                BoardCategory.Reference,
                BoardCategory.CustomerOriginal,
                BoardCategory.Prompt,
                BoardCategory.Inbox
            };
            await PumpUntilCompleted(window.Dispatcher);

            Assert.IsNotNull(store.LastSettings);
            CollectionAssert.AreEqual(expected, store.LastSettings!.DisplayOrder.ToArray());
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void DragCancelViaLostCapture_ReboundsWithoutCommitting()
    {
        using var directory = new TestDirectory();
        var (window, store) = CreateContext(directory, s => s);

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);
            var originalOrder = ((MainWindowViewModel)window.DataContext).Categories
                .Select(panel => panel.Category).ToArray();

            var handle = FindHandle(GetOrderRows(settings)[0]);
            handle.RaiseEvent(NewMouseButtonEventArgs(handle, UIElement.MouseLeftButtonDownEvent));
            Assert.IsTrue(handle.IsMouseCaptured, "前置条件：把手已捕获鼠标进入拖拽会话。");

            handle.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
            {
                RoutedEvent = UIElement.LostMouseCaptureEvent
            });
            CompleteLayout(window);

            Assert.IsNull(GetDragSession(settings), "捕获丢失必须结束拖拽会话。");
            Assert.AreEqual(0, store.SettingsSaveCount, "回弹不得提交任何顺序变更。");
            CollectionAssert.AreEqual(
                originalOrder,
                ((MainWindowViewModel)window.DataContext).Categories
                    .Select(panel => panel.Category).ToArray());

            // 会话已结束：再次派发抬起不得产生任何效果。
            handle.RaiseEvent(NewMouseButtonEventArgs(handle, UIElement.MouseLeftButtonUpEvent));
            Assert.AreEqual(0, store.SettingsSaveCount);
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void EscapeDuringDrag_IsConsumedBySectionAndReboundsWithoutClosing()
    {
        using var directory = new TestDirectory();
        var (window, store) = CreateContext(directory, s => s);

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);
            var originalOrder = ((MainWindowViewModel)window.DataContext).Categories
                .Select(panel => panel.Category).ToArray();

            var handle = FindHandle(GetOrderRows(settings)[0]);
            handle.RaiseEvent(NewMouseButtonEventArgs(handle, UIElement.MouseLeftButtonDownEvent));
            Assert.IsTrue(handle.IsMouseCaptured);

            var escape = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(settings)!,
                Environment.TickCount,
                Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
                Source = settings
            };
            settings.RaiseEvent(escape);
            CompleteLayout(window);

            Assert.IsTrue(escape.Handled, "拖拽会话中的 Esc 必须由顺序节优先消费。");
            Assert.IsTrue(settings.IsLoaded, "Esc 用于回弹拖拽，不得触发设置窗口关闭。");
            Assert.IsNull(GetDragSession(settings));
            Assert.AreEqual(0, store.SettingsSaveCount);
            CollectionAssert.AreEqual(
                originalOrder,
                ((MainWindowViewModel)window.DataContext).Categories
                    .Select(panel => panel.Category).ToArray());

            // 回弹后 Esc 恢复既有关窗语义。
            var escapeAfter = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(settings)!,
                Environment.TickCount,
                Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
                Source = settings
            };
            settings.RaiseEvent(escapeAfter);
            Assert.IsTrue(escapeAfter.Handled, "回弹后 Esc 应回到既有关窗路径。");
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void DragCommit_RealInputMoveDownSwapsRowsAndPersists()
    {
        using var directory = new TestDirectory();
        var (window, store) = CreateContext(directory, s => s);

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);

            var handle = FindHandle(GetOrderRows(settings)[0]);
            var hwnd = new System.Windows.Interop.WindowInteropHelper(settings).Handle;
            var screen = handle.PointToScreen(
                new Point(handle.ActualWidth / 2, handle.ActualHeight / 2));

            // 移动真实光标前保存位置，finally 恢复：不恢复会污染后续测试的
            // 光标状态（既有真实输入测试的同款约定）。
            Assert.IsTrue(GetCursorPos(out var savedCursor), "测试环境必须支持读取真实光标。");
            try
            {
                RunRealInputDrag();
            }
            finally
            {
                SetCursorPos(savedCursor.X, savedCursor.Y);
            }

            void RunRealInputDrag()
            {
                // 强制位移预热（SetCursorPos 到原位不产生 WM_MOUSEMOVE）再精确落点。
                Assert.IsTrue(SetCursorPos((int)Math.Round(screen.X) + 7, (int)Math.Round(screen.Y) + 5));
                Assert.IsTrue(SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y)));
                PumpFor(settings, TimeSpan.FromMilliseconds(120));

                // 真实消息直驱按下（契约 #13 同源管线）：MouseDevice 认为按键真按下，
                // 捕获、移动与抬起全程走生产拖拽路径，不用合成路由事件近似。
                var downClient = new NativePoint((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
                Assert.IsTrue(ScreenToClient(hwnd, ref downClient));
                Assert.IsTrue(PostMessage(
                    hwnd,
                    WmLeftButtonDown,
                    MkLbutton,
                    MakeLParam(downClient.X, downClient.Y)),
                    "投递按下消息失败。");
                PumpFor(settings, TimeSpan.FromMilliseconds(120));
                Assert.IsTrue(handle.IsMouseCaptured, "真实按下后把手必须捕获鼠标进入拖拽会话。");

                // 真实光标下移 75 DIP（行距 34：拖起行中心落到第 2 行带 → 落点索引 2）。
                // PointToScreen 差值把 DIP 位移换算为物理像素，多档 DPI 下判定一致。
                var moveDownPhysical = (int)Math.Round(
                    handle.PointToScreen(new Point(0, 75)).Y -
                    handle.PointToScreen(new Point(0, 0)).Y);
                Assert.IsTrue(SetCursorPos((int)Math.Round(screen.X) + 7, (int)Math.Round(screen.Y) + 5));
                Assert.IsTrue(SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y) + moveDownPhysical));
                PumpFor(settings, TimeSpan.FromMilliseconds(150));
                Assert.AreEqual(
                    2,
                    GetSessionTargetIndex(settings),
                    "真实移动必须驱动落点指示到第 2 行带。");

                var upClient = new NativePoint((int)Math.Round(screen.X), (int)Math.Round(screen.Y) + moveDownPhysical);
                Assert.IsTrue(ScreenToClient(hwnd, ref upClient));
                Assert.IsTrue(PostMessage(hwnd, WmLeftButtonUp, 0, MakeLParam(upClient.X, upClient.Y)));
                PumpUntilCompleted(window.Dispatcher).GetAwaiter().GetResult();
                CompleteLayout(window);
            }

            var expected = new[]
            {
                BoardCategory.Reference,
                BoardCategory.Prompt,
                BoardCategory.CustomerOriginal,
                BoardCategory.Inbox
            };
            var viewModel = (MainWindowViewModel)window.DataContext;
            CollectionAssert.AreEqual(
                expected,
                viewModel.Categories.Select(panel => panel.Category).ToArray(),
                "拖拽提交必须换序并重排面板标签轨。");
            Assert.IsNotNull(store.LastSettings);
            CollectionAssert.AreEqual(expected, store.LastSettings!.DisplayOrder.ToArray());
        }
        finally
        {
            CloseAll(window);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private const int MkLbutton = 0x0001;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint hWnd, ref NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }

    private static nint MakeLParam(int low, int high) =>
        (nint)((high << 16) | (low & 0xFFFF));

    private static MouseButtonEventArgs NewMouseButtonEventArgs(
        FrameworkElement source,
        RoutedEvent routedEvent) =>
        new(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = routedEvent,
            Source = source
        };

    private static List<Border> GetOrderRows(SettingsWindow settings)
    {
        var list = (StackPanel)settings.FindName("CategoryOrderList")!;
        Assert.IsTrue(list.Children.Count == 4, "顺序列表必须恰好四行。");
        return list.Children.Cast<Border>().ToList();
    }

    private static FrameworkElement FindHandle(Border row) =>
        FindDescendants<FrameworkElement>(row)
            .First(element => AutomationProperties.GetName(element)!.StartsWith("拖动"));

    private static object? GetDragSession(SettingsWindow settings)
    {
        var field = typeof(SettingsWindow).GetField(
            "_categoryOrderDrag",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return field.GetValue(settings);
    }

    private static int GetSessionTargetIndex(SettingsWindow settings)
    {
        var session = GetDragSession(settings);
        return session is null ? -1 : (int)session.GetType().GetProperty("TargetIndex")!.GetValue(session)!;
    }

    private static async Task PumpUntilCompleted(Dispatcher dispatcher)
    {
        // 让 fire-and-forget 的 async void 提交链路（含宿主保存与采纳）排空。
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(50);
            done.TrySetResult();
        });
        PumpUntil(dispatcher, done.Task);
    }

    [STATestMethod]
    public void DragCommit_PureClickWithoutMove_DoesNotReorder()
    {
        // 回归锁定（T7 评审批判）：落点初值必须等于起点——纯点击把手（无移动）
        // 松手不得把该行挪到首位，也不得写盘。
        using var directory = new TestDirectory();
        var (window, store) = CreateContext(directory, s => s);

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);
            var originalOrder = ((MainWindowViewModel)window.DataContext).Categories
                .Select(panel => panel.Category).ToArray();

            var handle = FindHandle(GetOrderRows(settings)[2]);
            handle.RaiseEvent(NewMouseButtonEventArgs(handle, UIElement.MouseLeftButtonDownEvent));
            Assert.IsTrue(handle.IsMouseCaptured);
            handle.RaiseEvent(NewMouseButtonEventArgs(handle, UIElement.MouseLeftButtonUpEvent));
            CompleteLayout(window);

            Assert.IsNull(GetDragSession(settings));
            Assert.AreEqual(0, store.SettingsSaveCount);
            CollectionAssert.AreEqual(
                originalOrder,
                ((MainWindowViewModel)window.DataContext).Categories
                    .Select(panel => panel.Category).ToArray());
        }
        finally
        {
            CloseAll(window);
        }
    }

    /// <summary>等待真实光标操作被 WPF 处理（WM_MOUSEMOVE 结算）。</summary>
    private static void PumpFor(SettingsWindow settings, TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, settings.Dispatcher)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(Dispatcher dispatcher, Task task)
    {
        if (task.IsCompleted)
        {
            task.GetAwaiter().GetResult();
            return;
        }

        var frame = new DispatcherFrame();
        var timedOut = false;
        var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        timeout.Tick += (_, _) =>
        {
            timedOut = true;
            timeout.Stop();
            frame.Continue = false;
        };
        _ = task.ContinueWith(
            _ => dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() => frame.Continue = false)),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        timeout.Start();
        Dispatcher.PushFrame(frame);
        timeout.Stop();
        if (timedOut && !task.IsCompleted)
        {
            throw new TimeoutException("The dispatcher operation did not complete within five seconds.");
        }

        task.GetAwaiter().GetResult();
    }

    private static (MainWindow Window, RecordingSettingsBoardStore Store) CreateContext(
        TestDirectory directory,
        Func<WindowSettings, WindowSettings> configureSettings)
    {
        var store = new RecordingSettingsBoardStore(directory.Root);
        var board = new BoardService();
        var normalizer = new ImageNormalizer(store.ImagesDirectory);
        var operationGate = new BoardOperationGate();
        MainWindow? window = null;
        void ShowStatus(string message) => window?.ShowStatus(message);
        var clipboard = new ClipboardCaptureService(
            new IdleClipboardReader(),
            normalizer,
            board,
            store,
            ShowStatus,
            operationGate: operationGate);
        var settings = configureSettings(
            WindowSettings.Default with
            {
                ReviewMigrationVersion = DailyReviewMigration.CurrentVersion
            });
        window = new MainWindow(
            board,
            store,
            settings,
            clipboard,
            new BoardMutationService(board, store, ShowStatus, operationGate),
            new DragPayloadService(),
            new ExternalDropPayloadReader(new WindowsDataImageReader()),
            new ExternalDropImportService(
                normalizer,
                board,
                store,
                ShowStatus,
                operationGate),
            dataDirectory: directory.Root);
        return (window, store);
    }

    private static SettingsWindow OpenSettingsWindow(MainWindow window)
    {
        var gear = window.FindName("SettingsButton") as Button;
        Assert.IsNotNull(gear);
        gear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var settings = GetPrivateField<SettingsWindow>(window, "_settingsWindow");
        Assert.IsNotNull(settings);
        return settings;
    }

    private static T GetPrivateField<T>(MainWindow window, string fieldName)
    {
        var field = typeof(MainWindow).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return (T)field.GetValue(window)!;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            if (VisualTreeHelper.GetChild(parent, index) is T match)
            {
                return match;
            }

            if (FindDescendant<T>(VisualTreeHelper.GetChild(parent, index)) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static void CompleteLayout(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    private static void CloseAll(MainWindow window)
    {
        (typeof(MainWindow).GetField("_settingsWindow", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) as SettingsWindow)?.Close();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Close();
        PumpUntil(window.Dispatcher, closed.Task);
    }

    private sealed class IdleClipboardReader : IClipboardReader
    {
        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipboardSnapshot(0, null, [], null));
    }

    private sealed class RecordingSettingsBoardStore(string root) : IBoardStore
    {
        public int SettingsSaveCount { get; private set; }
        public WindowSettings? LastSettings { get; private set; }
        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public Task SaveBoardAsync(BoardSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<WindowSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(WindowSettings settings, CancellationToken cancellationToken = default)
        {
            SettingsSaveCount++;
            LastSettings = settings;
            return Task.CompletedTask;
        }

        public bool TryDeleteImage(string? absolutePath) => true;
    }
}
