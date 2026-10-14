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
/// 丢失回弹原序。乐观提交契约（2026-10-14 修订，Issue #153）：保存挂起期间面板
/// 已是目标序（松手同一 UI 消息内上屏、无旧序帧），失败回弹是唯一二次重建。
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

            // 真实消息直驱拖拽（契约 #13 同源管线，与 A/B 共用助手）。
            Assert.IsTrue(GetCursorPos(out var savedCursor), "测试环境必须支持读取真实光标。");
            try
            {
                RealInputDragFirstRowToIndex2(settings, handle);
                PumpUntilCompleted(window.Dispatcher).GetAwaiter().GetResult();
                CompleteLayout(window);
            }
            finally
            {
                SetCursorPos(savedCursor.X, savedCursor.Y);
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

    [STATestMethod]
    public void DragCommit_WhileSavePending_PanelAlreadyShowsTargetOrder()
    {
        // 测试 A（乐观上屏锁定，Issue #153）：真实输入拖拽松手后保存挂起未完成时，
        // 面板行序必须已是目标序（松手同一 UI 消息内重建，无旧序中间帧），指示线
        // 隐藏、无半透明残留；放行保存后行序稳定、持久化序==目标序。红绿证据：
        // 未修复代码在挂起断言处失败（面板为旧序）。
        using var directory = new TestDirectory();
        var (window, store) = CreateContext(directory, s => s, new GatedSettingsBoardStore(directory.Root));

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);
            var handle = FindHandle(GetOrderRows(settings)[0]);
            var targetOrder = new[]
            {
                BoardCategory.Reference,
                BoardCategory.Prompt,
                BoardCategory.CustomerOriginal,
                BoardCategory.Inbox
            };

            store.HoldNextSave();
            try
            {
                Assert.IsTrue(GetCursorPos(out var savedCursor), "测试环境必须支持读取真实光标。");
                try
                {
                    RealInputDragFirstRowToIndex2(settings, handle);
                    PumpUntilCondition(settings.Dispatcher, () => store.SettingsSaveCount == 1);
                }
                finally
                {
                    SetCursorPos(savedCursor.X, savedCursor.Y);
                }

                AssertRowsInOrder(settings, targetOrder, "保存挂起期间面板必须已显示目标序（乐观上屏）。");
                Assert.AreEqual(
                    Visibility.Collapsed,
                    ((FrameworkElement)settings.FindName("CategoryOrderInsertionIndicator")!).Visibility,
                    "保存挂起期间插入指示线必须已隐藏。");
                Assert.IsTrue(
                    GetOrderRows(settings).All(row => row.Opacity == 1),
                    "保存挂起期间不得残留半透明拖起行。");

                store.ReleaseSave();
                PumpUntilCompleted(window.Dispatcher).GetAwaiter().GetResult();
                CompleteLayout(window);

                AssertRowsInOrder(settings, targetOrder, "保存完成后行序必须稳定在目标序（不得二次跳动）。");
                Assert.IsNotNull(store.LastSettings);
                CollectionAssert.AreEqual(
                    targetOrder,
                    store.LastSettings!.DisplayOrder.ToArray(),
                    "放行后持久化序必须等于目标序。");
            }
            finally
            {
                // 断言失败（红路径）时放行残留挂起：关闭冲刷会等待 pending 提交与
                // 保存信号量，不放行会以清理超时掩盖断言失败。绿路径此调用无副作用。
                store.ReleaseSave();
            }
        }
        finally
        {
            CloseAll(window);
        }
    }

    [STATestMethod]
    public void DragCommit_SaveFailureAfterPending_ReboundsPanelAndHostOrder()
    {
        // 测试 B（失败回弹锁定，Issue #153）：保存挂起期间面板为乐观目标序；放行并
        // 令保存抛 IOException 后——面板回弹原序（唯一允许的第二次重建）、宿主
        // CurrentDisplayOrder 回原序、状态条提示出现。
        using var directory = new TestDirectory();
        var (window, store) = CreateContext(directory, s => s, new GatedSettingsBoardStore(directory.Root));

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);
            var handle = FindHandle(GetOrderRows(settings)[0]);
            var originalOrder = window.CurrentDisplayOrder.ToArray();
            var targetOrder = new[]
            {
                BoardCategory.Reference,
                BoardCategory.Prompt,
                BoardCategory.CustomerOriginal,
                BoardCategory.Inbox
            };

            store.HoldNextSave();
            try
            {
                Assert.IsTrue(GetCursorPos(out var savedCursor), "测试环境必须支持读取真实光标。");
                try
                {
                    RealInputDragFirstRowToIndex2(settings, handle);
                    PumpUntilCondition(settings.Dispatcher, () => store.SettingsSaveCount == 1);
                }
                finally
                {
                    SetCursorPos(savedCursor.X, savedCursor.Y);
                }

                AssertRowsInOrder(settings, targetOrder, "前置：挂起期间面板必须是乐观目标序。");

                store.ReleaseSave(new IOException("模拟 settings.json 写盘失败。"));
                PumpUntilCompleted(window.Dispatcher).GetAwaiter().GetResult();
                CompleteLayout(window);

                AssertRowsInOrder(settings, originalOrder, "保存失败后面板必须回弹原序。");
                CollectionAssert.AreEqual(
                    originalOrder,
                    window.CurrentDisplayOrder.ToArray(),
                    "保存失败后宿主显示顺序必须回滚原序。");
                Assert.IsTrue(
                    ((MainWindowViewModel)window.DataContext).StatusText.Contains("标签顺序暂未保存。"),
                    "保存失败必须经状态条提示。");
            }
            finally
            {
                // 断言失败（红路径）时兜底放行并清除失败注入，理由同测试 A。
                store.ReleaseSave();
            }
        }
        finally
        {
            CloseAll(window);
        }
    }

    /// <summary>真实消息直驱把首行拖到落点索引 2 后投递抬起消息（与既有
    /// DragCommit_RealInputMoveDownSwapsRowsAndPersists 同源管线）；不等待提交链路
    /// 排空，由调用方按需泵——乐观上屏与挂起断言须在排空前观察。</summary>
    private static void RealInputDragFirstRowToIndex2(SettingsWindow settings, FrameworkElement handle)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(settings).Handle;
        var screen = handle.PointToScreen(
            new Point(handle.ActualWidth / 2, handle.ActualHeight / 2));

        Assert.IsTrue(SetCursorPos((int)Math.Round(screen.X) + 7, (int)Math.Round(screen.Y) + 5));
        Assert.IsTrue(SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y)));
        PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(120));

        var downClient = new NativePoint((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
        Assert.IsTrue(ScreenToClient(hwnd, ref downClient));
        Assert.IsTrue(PostMessage(
            hwnd,
            WmLeftButtonDown,
            MkLbutton,
            MakeLParam(downClient.X, downClient.Y)),
            "投递按下消息失败。");
        PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(120));
        Assert.IsTrue(handle.IsMouseCaptured, "真实按下后把手必须捕获鼠标进入拖拽会话。");

        var moveDownPhysical = (int)Math.Round(
            handle.PointToScreen(new Point(0, 75)).Y -
            handle.PointToScreen(new Point(0, 0)).Y);
        Assert.IsTrue(SetCursorPos((int)Math.Round(screen.X) + 7, (int)Math.Round(screen.Y) + 5));
        Assert.IsTrue(SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y) + moveDownPhysical));
        PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(150));
        Assert.AreEqual(2, GetSessionTargetIndex(settings), "真实移动必须驱动落点指示到第 2 行带。");

        var upClient = new NativePoint((int)Math.Round(screen.X), (int)Math.Round(screen.Y) + moveDownPhysical);
        Assert.IsTrue(ScreenToClient(hwnd, ref upClient));
        Assert.IsTrue(PostMessage(hwnd, WmLeftButtonUp, 0, MakeLParam(upClient.X, upClient.Y)));
    }

    private static void AssertRowsInOrder(
        SettingsWindow settings,
        IReadOnlyList<BoardCategory> expected,
        string message) =>
        CollectionAssert.AreEqual(
            expected.ToArray(),
            GetOrderRows(settings).Select(row => (BoardCategory)row.Tag).ToArray(),
            message);

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

    /// <summary>1.25.0 发布证据（FTS_RELEASE_125_EVIDENCE_DIR 设置时产出真实 WPF
    /// 渲染截图）：展开态主窗默认标签名（F4 默认名「文本」）+ 设置窗口（F2 TTL
    /// 说明行与 F3 标签顺序节）+ 上移后的顺序变化（F3）。</summary>
    [STATestMethod]
    public void Evidence_CaptureRelease125Screens()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("FTS_RELEASE_125_EVIDENCE_DIR")))
        {
            Assert.Inconclusive("未设置 FTS_RELEASE_125_EVIDENCE_DIR，跳过发布证据产出。");
        }

        using var directory = new TestDirectory();
        var (window, store) = CreateContext(directory, s => s);

        try
        {
            window.Show();
            ExpandDefaultCapture(window);
            CompleteLayout(window);
            SaveShell(window, "release125-main-window-default-names.png");

            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);
            SaveSettingsShell(settings, "release125-settings-window.png");

            var secondRow = GetOrderRows(settings)[1];
            FindDescendants<Button>(secondRow).Single(b => Equals(b.Content, "↑"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntilCompleted(window.Dispatcher).GetAwaiter().GetResult();
            CompleteLayout(window);
            SaveSettingsShell(settings, "release125-settings-after-move-up.png");
            _ = store;
        }
        finally
        {
            CloseAll(window);
        }
    }

    /// <summary>松手帧序列取证（FTS_CATEGORYORDER_DROP_FRAMES_DIR 设置时产出，
    /// 2026-10-14 Issue #153）：真实输入拖拽松手，保存被门挂起期间逐帧渲染捕获
    /// 设置窗口壳层——未修复代码该时段为旧序帧（用户看到的闪帧本体），修复后为
    /// 稳定目标序；放行保存后再捕对照帧。帧序列落指定目录供慢放检视，不提交。</summary>
    [STATestMethod]
    public void Evidence_CaptureCategoryOrderDropFrames()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("FTS_CATEGORYORDER_DROP_FRAMES_DIR")))
        {
            Assert.Inconclusive("未设置 FTS_CATEGORYORDER_DROP_FRAMES_DIR，跳过松手帧序列取证。");
        }

        using var directory = new TestDirectory();
        var (window, store) = CreateContext(directory, s => s, new GatedSettingsBoardStore(directory.Root));

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);
            var handle = FindHandle(GetOrderRows(settings)[0]);
            var shell = (Border)settings.FindName("WindowShell")!;

            store.HoldNextSave();
            Assert.IsTrue(GetCursorPos(out var savedCursor), "测试环境必须支持读取真实光标。");
            try
            {
                RealInputDragFirstRowToIndex2(settings, handle);
                // 让松手消息结算进入保存挂起（提交链已在同消息内完成上屏）。
                PumpFor(settings.Dispatcher, TimeSpan.FromMilliseconds(60));
                CaptureShellFrames(settings, shell, "drop-pending", 12);
            }
            finally
            {
                store.ReleaseSave();
                SetCursorPos(savedCursor.X, savedCursor.Y);
            }

            PumpUntilCompleted(window.Dispatcher).GetAwaiter().GetResult();
            CompleteLayout(window);
            CaptureShellFrames(settings, shell, "after-save", 4);
        }
        finally
        {
            CloseAll(window);
        }
    }

    /// <summary>逐帧渲染捕获：每帧以 Render 优先级渲染一拍后抓取壳层位图，帧间以
    /// Background 优先级让渡；帧文件名带序号便于慢放逐帧检视。</summary>
    private static void CaptureShellFrames(SettingsWindow settings, Border shell, string phase, int frameCount)
    {
        for (var index = 1; index <= frameCount; index++)
        {
            var frame = new DispatcherFrame();
            _ = settings.Dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                new Action(() =>
                {
                    SaveShellFrame(shell, $"{phase}-frame-{index:D2}.png");
                    _ = settings.Dispatcher.BeginInvoke(
                        DispatcherPriority.Background,
                        new Action(() => frame.Continue = false));
                }));
            Dispatcher.PushFrame(frame);
        }
    }

    private static void SaveShellFrame(Border shell, string fileName)
    {
        var directory = Environment.GetEnvironmentVariable("FTS_CATEGORYORDER_DROP_FRAMES_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(shell.ActualWidth),
            (int)Math.Ceiling(shell.ActualHeight),
            96,
            96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(shell);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, fileName));
        encoder.Save(stream);
    }

    private static void ExpandDefaultCapture(MainWindow window)
    {
        // 直接驱动视图模型展开默认接收分类（布局定稿后截壳层）。
        var viewModel = (MainWindowViewModel)window.DataContext;
        viewModel.Activate(viewModel.DefaultCapturePanel.Category);
        viewModel.SetPanelExpanded(true);
        window.UpdateLayout();
    }

    private static void SaveShell(MainWindow window, string fileName) =>
        SaveWindowVisual((Border)window.FindName("WindowShell")!, fileName);

    private static void SaveSettingsShell(SettingsWindow settings, string fileName) =>
        SaveWindowVisual((Border)settings.FindName("WindowShell")!, fileName);

    private static void SaveWindowVisual(Border shell, string fileName)
    {
        var directory = Environment.GetEnvironmentVariable("FTS_RELEASE_125_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(shell.ActualWidth),
            (int)Math.Ceiling(shell.ActualHeight),
            96,
            96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(shell);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, fileName));
        encoder.Save(stream);
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

    [STATestMethod]
    public void OrderRows_FollowLiveThemeSwitchWithoutRebuild()
    {
        // 契约 #21 活切换锁定：行是代码构建的，画刷必须经动态资源引用解析——
        // 构建后活切主题，行背景/描边/文字与按钮画刷跟随新字典，无需重开窗口或重建行。
        using var directory = new TestDirectory();
        var (window, store) = CreateContext(directory, s => s);

        try
        {
            window.Show();
            var settings = OpenSettingsWindow(window);
            CompleteLayout(window);
            AssertOrderRowBrushesFollowLiveThemeSwitch(settings);

            // 提交重建行后同样成立（上移触发 PopulateCategoryOrderSection 重建行）。
            var secondRow = GetOrderRows(settings)[1];
            FindDescendants<Button>(secondRow).Single(b => Equals(b.Content, "↑"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntilCompleted(window.Dispatcher).GetAwaiter().GetResult();
            CompleteLayout(window);
            AssertOrderRowBrushesFollowLiveThemeSwitch(settings);
            _ = store;
        }
        finally
        {
            CloseAll(window);
        }
    }

    /// <summary>对首行断言两组主题的画刷跟随：与 FindResource 结果同例，且两主题实例互异。</summary>
    private static void AssertOrderRowBrushesFollowLiveThemeSwitch(SettingsWindow settings)
    {
        var row = GetOrderRows(settings)[0];
        var label = FindDescendants<TextBlock>(row)
            .Single(t => AutomationProperties.GetName(t)!.StartsWith("标签"));
        var handleGlyph = FindDescendants<TextBlock>(row).Single(t => t.Text == "⋮⋮");
        var moveUp = FindDescendants<Button>(row).Single(b => Equals(b.Content, "↑"));

        var initial = CurrentThemeIsDark(settings) ? DesignTheme.Dark : DesignTheme.Light;
        var other = initial == DesignTheme.Dark ? DesignTheme.Light : DesignTheme.Dark;

        DesignThemeManager.Apply(settings, other);
        CompleteLayout(settings);
        var otherCard = (Brush)settings.FindResource("CardBrush");
        var otherBorder = (Brush)settings.FindResource("BorderBrush");
        var otherPrimary = (Brush)settings.FindResource("PrimaryTextBrush");
        var otherSecondary = (Brush)settings.FindResource("SecondaryTextBrush");
        Assert.AreSame(otherCard, row.Background, $"切到 {other} 后行背景必须解析到新主题 CardBrush。");
        Assert.AreSame(otherBorder, row.BorderBrush, $"切到 {other} 后行描边必须解析到新主题 BorderBrush。");
        Assert.AreSame(otherPrimary, label.Foreground, $"切到 {other} 后标签名必须解析到新主题 PrimaryTextBrush。");
        Assert.AreSame(otherSecondary, handleGlyph.Foreground, $"切到 {other} 后拖拽把手必须解析到新主题 SecondaryTextBrush。");
        Assert.AreSame(
            otherPrimary,
            moveUp.Foreground,
            $"切到 {other} 后上移按钮前景必须经 ThemedActionButtonStyle 的 DynamicResource 跟随。");

        DesignThemeManager.Apply(settings, initial);
        CompleteLayout(settings);
        var initialCard = (Brush)settings.FindResource("CardBrush");
        var initialBorder = (Brush)settings.FindResource("BorderBrush");
        var initialPrimary = (Brush)settings.FindResource("PrimaryTextBrush");
        var initialSecondary = (Brush)settings.FindResource("SecondaryTextBrush");
        Assert.AreNotSame(otherCard, initialCard, "两主题字典的画刷必须是不同实例（前置 sanity）。");
        Assert.AreSame(initialCard, row.Background, $"切回 {initial} 后行背景必须跟随。");
        Assert.AreSame(initialBorder, row.BorderBrush);
        Assert.AreSame(initialPrimary, label.Foreground);
        Assert.AreSame(initialSecondary, handleGlyph.Foreground);
        Assert.AreSame(initialPrimary, moveUp.Foreground);
    }

    private static bool CurrentThemeIsDark(Window window) =>
        (window.Resources.MergedDictionaries[1].Source?.OriginalString ?? string.Empty)
            .Replace('\\', '/')
            .EndsWith("Resources/DesignTheme.Dark.xaml", StringComparison.Ordinal);

    /// <summary>等待真实光标操作被 WPF 处理（WM_MOUSEMOVE 结算）。</summary>
    private static void PumpFor(Dispatcher dispatcher, TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
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

    /// <summary>泵 Dispatcher 直至条件成立或超时：用于等待真实输入消息链推进到
    /// 确定性状态（如保存链路已进入挂起），避免固定时长的竞态。</summary>
    private static void PumpUntilCondition(Dispatcher dispatcher, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            PumpFor(dispatcher, TimeSpan.FromMilliseconds(20));
        }

        Assert.IsTrue(condition(), "等待条件未在超时内满足。");
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

    private static (MainWindow Window, GatedSettingsBoardStore Store) CreateContext(
        TestDirectory directory,
        Func<WindowSettings, WindowSettings> configureSettings,
        GatedSettingsBoardStore? store = null)
    {
        store ??= new GatedSettingsBoardStore(directory.Root);
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
        // 双栏布局（契约 #24）：标签顺序节位于「面板」页，拖拽与可见性断言须先导航。
        var panelNav = settings.FindName("SettingsNavPanel") as RadioButton;
        Assert.IsNotNull(panelNav, "设置窗必须有「面板」导航项。");
        panelNav.IsChecked = true;
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
}
