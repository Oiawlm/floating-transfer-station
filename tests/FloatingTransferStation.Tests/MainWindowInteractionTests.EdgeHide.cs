using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatingTransferStation.Design;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 一次性贴边隐藏 peek 化的窗口层契约（R12，契约 #17，Issue #353）：armed + 指针
/// 离开 → 整窗同尺寸平移收缩为右缘把手（与工作区交集宽＝16 DIP 把手宽）、面板保持
/// 展开；把手 hover 意图事务（60ms 重验指针在内，#42 同族）→ 展开回原放置并解除
/// （一次性）；展开后再次离开仅普通轨道收起；互斥（R2）沿 c1；热键与显示器变化
/// 立即展开兜底；减动效一次直达终态；收缩途中指针回入＝从当前进度反转展开。
/// 窗口矩形动画经唯一 WindowPlacementAnimator（#12 例外条款），测试注入手动时钟逐帧断言。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private const string EdgeHideDefaultLabel =
        "一次性收进屏幕边缘：鼠标离开后缩成边缘把手，移上把手自动展开";
    private const string EdgeHideArmedLabel =
        "已准备收进屏幕边缘，鼠标移开即缩成把手；再按一次取消";
    private const string EdgePeekLabel = "已收进屏幕边缘：移上把手或用唤起快捷键展开";

    private static void ToggleEdgeHide(MainWindow window) =>
        InvokePrivate(
            window,
            "EdgeHideButton_Click",
            window.FindName("EdgeHideButton"),
            new RoutedEventArgs(Button.ClickEvent, window.FindName("EdgeHideButton")));

    private static EdgeHideStateMachine EdgeHideState(MainWindow window) =>
        GetPrivateField<EdgeHideStateMachine>(window, "_edgeHide");

    private static WindowPlacementAnimator? PeekAnimator(MainWindow window) =>
        GetPrivateField<WindowPlacementAnimator?>(window, "_edgePeekAnimator");

    private static System.Windows.Controls.Grid PeekHandleLayer(MainWindow window) =>
        (System.Windows.Controls.Grid)window.FindName("EdgePeekHandleLayer")!;

    private static bool PanelHoldActive(MainWindow window) =>
        GetPrivateField<PanelStateMachine>(window, "_panelState").IsPanelHoldActive;

    /// <summary>当前窗口 DIP→物理换算系数（X 轴），用于把手几何与账本收口断言。</summary>
    private static double PeekDpiScale(MainWindow window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var source = System.Windows.Interop.HwndSource.FromHwnd(handle);
        Assert.IsNotNull(source?.CompositionTarget);
        return source.CompositionTarget.TransformToDevice.M11;
    }

    /// <summary>peek 几何锚点（物理像素）：工作区右缘与把手宽（按窗口当前 DPI 换算）。</summary>
    private static (int WorkRightPx, int HandleWidthPx) PeekAnchor(MainWindow window)
    {
        var workArea = SystemParameters.WorkArea;
        var scale = PeekDpiScale(window);
        return (
            (int)Math.Round((workArea.Left + workArea.Width) * scale),
            Math.Max(1, (int)Math.Round(DesignTokens.EdgePeekHandleWidthPx * scale)));
    }

    /// <summary>
    /// armed + 模拟指针离开 + 收起节奏 tick → 收缩为 peek 把手（减动效路径：一次直达
    /// 终态，确定性无帧）；带动画路径由注入手动时钟的用例单独锁定。
    /// </summary>
    private static void PeekWindowAtEdge(MainWindow window)
    {
        ToggleEdgeHide(window);
        InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
        InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
        Assert.IsTrue(EdgeHideState(window).IsPeek, "前置条件：窗口已收缩为 peek 把手。");
    }

    private static void AssertRestoredToOriginal(
        MainWindow window,
        in NativeRect before,
        string because)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        Assert.IsTrue(GetWindowRect(handle, out var restored), because);
        Assert.AreEqual(before.Left, restored.Left, 1, because);
        Assert.AreEqual(before.Top, restored.Top, 1, because);
        Assert.AreEqual(before.Width, restored.Width, 1, because);
        Assert.AreEqual(before.Height, restored.Height, 1, because);
    }

    [STATestMethod]
    public void EdgePeek_ArmedLeaveCollapsesToHandleWithAnimation()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        var clock = new ManualPlacementAnimationClock();
        window.PlacementAnimationClockOverride = clock;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            Assert.IsTrue(GetWindowRect(handle, out var before));
            var (workRightPx, handleWidthPx) = PeekAnchor(window);

            ToggleEdgeHide(window);
            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);

            // 提交即进入 Peek；动画起点＝当前矩形（首帧不跳变）。
            Assert.IsTrue(EdgeHideState(window).IsPeek, "armed＋离开即提交 peek。");
            var animator = PeekAnimator(window);
            Assert.IsNotNull(animator);
            Assert.IsTrue(animator.IsRunning, "收缩动画在途（167ms NormalDurationMs）。");
            Assert.IsTrue(GetWindowRect(handle, out var first));
            Assert.AreEqual(before.Left, first.Left, 1, "动画首帧仍在原矩形。");

            // 中段帧：介于初终态之间、Top/宽高不变（纯平移）。
            clock.AdvanceTo(80);
            Assert.IsTrue(GetWindowRect(handle, out var mid));
            Assert.IsTrue(
                mid.Left > before.Left && mid.Left < workRightPx - handleWidthPx,
                $"中段帧 Left={mid.Left} 必须介于原左缘 {before.Left} 与把手左缘 {workRightPx - handleWidthPx} 之间。");
            Assert.AreEqual(before.Top, mid.Top, 1, "收缩是纯平移，Top 不变。");
            Assert.AreEqual(before.Width, mid.Width, 1, "收缩是同尺寸，宽不变。");
            Assert.AreEqual(before.Height, mid.Height, 1);

            // 越过 167ms → 精确落终态：与工作区交集宽恰＝把手宽；面板保持展开。
            clock.AdvanceTo(DesignTokens.NormalDurationMs + 40);
            Assert.IsFalse(animator.IsRunning, "到达时长后动画完成。");
            Assert.IsTrue(GetWindowRect(handle, out var peeked));
            Assert.AreEqual(workRightPx - handleWidthPx, peeked.Left, 1, "把手左缘＝工作区右缘−把手宽。");
            Assert.AreEqual(workRightPx, peeked.Left + handleWidthPx, 1, "屏内保留宽恰＝把手宽。");
            Assert.AreEqual(before.Width, peeked.Width, 1);
            Assert.AreEqual(before.Top, peeked.Top, 1);
            Assert.IsTrue(viewModel.IsPanelExpanded, "peek 期间面板保持展开、内容冻结。");
            Assert.AreEqual(
                Visibility.Visible,
                PeekHandleLayer(window).Visibility,
                "收缩完成把手视觉登场。");

            // WPF Left/Top 账本在动画完成时一次收口。
            Assert.AreEqual(
                (workRightPx - handleWidthPx) / PeekDpiScale(window),
                window.Left,
                0.6,
                "动画完成时 WPF 账本一次收口到把手放置。");
        }
        finally
        {
            RestoreAndClose(window);
        }
    }

    [STATestMethod]
    public void EdgePeek_ReducedMotionCollapsesDirectlyToTerminalState()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            Assert.IsTrue(GetWindowRect(handle, out var before));
            var (workRightPx, handleWidthPx) = PeekAnchor(window);

            PeekWindowAtEdge(window);
            CompleteLayout(window);

            // 减动效：不经 animator，一次矩形变更直达终态；把手即时出现（零帧淡入）。
            Assert.IsNull(PeekAnimator(window), "减动效不创建放置动画。");
            Assert.IsTrue(GetWindowRect(handle, out var peeked));
            Assert.AreEqual(workRightPx - handleWidthPx, peeked.Left, 1);
            Assert.AreEqual(before.Width, peeked.Width, 1, "同尺寸纯平移。");
            Assert.AreEqual(before.Top, peeked.Top, 1);
            Assert.IsTrue(viewModel.IsPanelExpanded);
            var layer = PeekHandleLayer(window);
            Assert.AreEqual(Visibility.Visible, layer.Visibility, "把手即时出现。");
            Assert.AreEqual(1d, layer.Opacity, 0.01, "减动效把手无登场淡入。");

            // peek 态下再离开/收起 tick 不收起（把手驻留等待展开）。
            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            CompleteLayout(window);
            Assert.IsTrue(EdgeHideState(window).IsPeek, "peek 态不得普通收起。");
            Assert.IsTrue(viewModel.IsPanelExpanded);
        }
        finally
        {
            RestoreAndClose(window);
        }
    }

    [STATestMethod]
    public void EdgePeek_HandleHoverDwellExpandsToOriginalPlacementAndDisarms()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            Assert.IsTrue(GetWindowRect(handle, out var before));
            var savedCursor = SaveCursorPosition();

            PeekWindowAtEdge(window);
            Assert.IsTrue(GetWindowRect(handle, out var peeked));
            var midY = (peeked.Top + peeked.Bottom) / 2;

            // 意图事务布防后指针已离开窗口：到期重验失败，不得展开。
            InvokePrivate(window, "Root_MouseEnter", window, NewMouseEventArgs());
            Assert.IsTrue(
                SetCursorPos(peeked.Left - 60, midY),
                "真实光标必须可移动到窗口外。");
            InvokePrivate(window, "EdgePeekExpandIntentTimer_Tick", null, EventArgs.Empty);
            Assert.IsTrue(EdgeHideState(window).IsPeek, "指针不在窗口内，意图到期不得展开。");

            // 指针在把手内：到期重验通过 → 展开回原放置并解除（一次性）。
            Assert.IsTrue(
                SetCursorPos(peeked.Left + 4, midY),
                "真实光标必须可移上把手。");
            InvokePrivate(window, "Root_MouseEnter", window, NewMouseEventArgs());
            InvokePrivate(window, "EdgePeekExpandIntentTimer_Tick", null, EventArgs.Empty);
            CompleteLayout(window);

            Assert.IsFalse(EdgeHideState(window).IsPeek, "把手 hover 展开后解除（一次性）。");
            Assert.IsFalse(EdgeHideState(window).IsArmed);
            AssertRestoredToOriginal(window, before, "展开必须回到原矩形原形。");
            Assert.AreEqual(
                Visibility.Collapsed,
                PeekHandleLayer(window).Visibility,
                "展开后把手视觉退场。");
            Assert.IsTrue(((MainWindowViewModel)window.DataContext).IsPanelExpanded);
            RestoreCursorPosition(savedCursor);
        }
        finally
        {
            RestoreAndClose(window);
        }
    }

    [STATestMethod]
    public void EdgePeek_ReverseRescueDuringCollapseInterruptsAndExpandsFromCurrentOffset()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        var clock = new ManualPlacementAnimationClock();
        window.PlacementAnimationClockOverride = clock;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            Assert.IsTrue(GetWindowRect(handle, out var before));
            var (workRightPx, handleWidthPx) = PeekAnchor(window);
            var savedCursor = SaveCursorPosition();

            ToggleEdgeHide(window);
            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            Assert.IsTrue(EdgeHideState(window).IsPeek);

            // 收缩中段（ExitEasing 前慢后快，80ms 时进度约 10%）：指针追入窗口本体。
            clock.AdvanceTo(80);
            Assert.IsTrue(GetWindowRect(handle, out var midCollapse));
            Assert.IsTrue(
                midCollapse.Left > before.Left && midCollapse.Left < workRightPx - handleWidthPx,
                "前置：收缩进行中（介于初终态之间）。");
            Assert.IsTrue(
                SetCursorPos(midCollapse.Left + 4, (midCollapse.Top + midCollapse.Bottom) / 2),
                "真实光标必须可追入收缩中的窗口。");
            InvokePrivate(window, "Root_MouseEnter", window, NewMouseEventArgs());

            // 60ms 意图事务（真实 DispatcherTimer）到期重验指针在内 → 从当前进度反转。
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));

            var animator = PeekAnimator(window);
            Assert.IsNotNull(animator);
            Assert.IsTrue(animator.IsRunning, "救援触发展开动画（Redirect）。");
            Assert.IsFalse(EdgeHideState(window).IsPeek, "展开提交即解除一次性相位。");
            Assert.IsTrue(GetWindowRect(handle, out var atReverse));
            Assert.IsTrue(
                atReverse.Left >= midCollapse.Left - 1 && atReverse.Left <= workRightPx,
                $"反转起点＝当前实时进度 {midCollapse.Left}，禁原点重放。");

            // 推完重映射时长（封顶 250ms）→ 回到原放置，账本一次收口。
            clock.AdvanceTo(80 + DesignTokens.SlowDurationMs + 100);
            Assert.IsFalse(animator.IsRunning);
            AssertRestoredToOriginal(window, before, "救援展开必须回到原矩形。");
            Assert.AreEqual(
                before.Left / PeekDpiScale(window),
                window.Left,
                1.5,
                "账本收口到原放置。");
            RestoreCursorPosition(savedCursor);
        }
        finally
        {
            RestoreAndClose(window);
        }
    }

    [STATestMethod]
    public void EdgePeek_GlobalHotkeyRestoresPeekWindow()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            Assert.IsTrue(
                GetWindowRect(
                    new System.Windows.Interop.WindowInteropHelper(window).Handle,
                    out var before));

            PeekWindowAtEdge(window);
            InvokePrivate(window, "OnGlobalHotkeyPressed");
            CompleteLayout(window);

            Assert.IsFalse(EdgeHideState(window).IsPeek, "热键是找回兜底，必须立即展开。");
            Assert.IsFalse(EdgeHideState(window).IsArmed);
            AssertRestoredToOriginal(window, before, "热键展开必须回到原矩形。");
            Assert.IsTrue(((MainWindowViewModel)window.DataContext).IsPanelExpanded);
        }
        finally
        {
            RestoreAndClose(window);
        }
    }

    [STATestMethod]
    public void EdgePeek_DisplayEnvironmentChangeRestoresPeekWindow()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            Assert.IsTrue(
                GetWindowRect(
                    new System.Windows.Interop.WindowInteropHelper(window).Handle,
                    out var before));

            PeekWindowAtEdge(window);
            InvokePrivate(window, "OnDisplayEnvironmentChanged");
            CompleteLayout(window);

            // 显示器布局变化后把手可能失去找回路径，必须立即展开（防找不到窗口）。
            Assert.IsFalse(EdgeHideState(window).IsPeek);
            AssertRestoredToOriginal(window, before, "显示器变化必须立即展开并贴齐工作区。");
        }
        finally
        {
            RestoreAndClose(window);
        }
    }

    [STATestMethod]
    public void EdgePeek_HandleHoverRealInputDwellExpands()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            Assert.IsTrue(GetWindowRect(handle, out var before));
            var savedCursor = SaveCursorPosition();

            PeekWindowAtEdge(window);
            Assert.IsTrue(GetWindowRect(handle, out var peeked));

            // 契约 #13：把手 hover 展开必须 Win32 消息直驱守护——SetCursorPos 生成
            // 真实 WM_MOUSEMOVE，走完整 WPF 输入管线（MouseEnter → 意图布防 → 60ms
            // 到期重验 → 展开），全程不注入合成路由事件。
            Assert.IsTrue(
                SetCursorPos(peeked.Left + 4, (peeked.Top + peeked.Bottom) / 2),
                "真实光标必须可移上把手。");
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
            CompleteLayout(window);

            Assert.IsFalse(
                EdgeHideState(window).IsPeek,
                "真实鼠标移上把手（完整输入管线）必须触发展开。");
            AssertRestoredToOriginal(window, before, "真实输入展开回到原矩形。");
            RestoreCursorPosition(savedCursor);
        }
        finally
        {
            RestoreAndClose(window);
        }
    }

    [STATestMethod]
    public void EdgePeek_AfterExpandNextLeaveCollapsesToRailNormally()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            PeekWindowAtEdge(window);
            InvokePrivate(window, "OnGlobalHotkeyPressed");
            CompleteLayout(window);
            Assert.IsFalse(EdgeHideState(window).IsPeek);

            // 一次性：展开后再次离开只剩普通轨道收起，不再次收缩。
            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            CompleteLayout(window);

            Assert.IsFalse(viewModel.IsPanelExpanded, "展开后离开必须普通收起。");
            Assert.AreEqual(WindowSettings.TabWidth, window.Width, 0.5);
            Assert.AreEqual(EdgeHidePhase.Idle, EdgeHideState(window).Phase);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void EdgeHide_SecondPressCancelsArmedThenLeaveCollapsesNormally()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            ToggleEdgeHide(window);
            Assert.IsTrue(EdgeHideState(window).IsArmed);
            ToggleEdgeHide(window);
            Assert.AreEqual(EdgeHidePhase.Idle, EdgeHideState(window).Phase);

            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            CompleteLayout(window);

            Assert.IsFalse(viewModel.IsPanelExpanded, "取消待命后离开必须普通收起。");
            Assert.AreEqual(WindowSettings.TabWidth, window.Width, 0.5);
            Assert.AreEqual(EdgeHidePhase.Idle, EdgeHideState(window).Phase);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void EdgeHide_PanelHoldActivationDisarmsArmedAndLaterLeaveCollapsesNormally()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var expandedWidth = window.Width;
            var viewModel = (MainWindowViewModel)window.DataContext;

            // 互斥（R2，后激活者胜）：armed 与保持展开不得并存——激活保持展开即解除
            // 贴边待命（armed 即灭），此后保持期间离开仍不得收缩或收起。
            ToggleEdgeHide(window);
            Assert.IsTrue(EdgeHideState(window).IsArmed);
            TogglePanelHold(window);

            Assert.AreEqual(
                EdgeHidePhase.Idle,
                EdgeHideState(window).Phase,
                "激活保持展开必须解除贴边待命。");
            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            CompleteLayout(window);

            Assert.IsTrue(viewModel.IsPanelExpanded, "保持展开期间不得收缩。");
            Assert.IsTrue(PanelHoldActive(window));
            Assert.AreEqual(expandedWidth, window.Width, 0.5);

            // 解除保持后的离开走常规收起（贴边待命已被互斥解除，不再恢复收缩）。
            TogglePanelHold(window);
            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            CompleteLayout(window);

            Assert.IsFalse(viewModel.IsPanelExpanded, "解除保持后的离开必须普通收起。");
            Assert.AreEqual(WindowSettings.TabWidth, window.Width, 0.5);
            Assert.AreEqual(EdgeHidePhase.Idle, EdgeHideState(window).Phase);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void EdgeHide_MutexActivatingHoldDisarmsArmedEdgeHide()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var edgeButton = (Button)window.FindName("EdgeHideButton");
            var holdButton = (Button)window.FindName("PanelHoldButton");
            Assert.IsNotNull(edgeButton);
            Assert.IsNotNull(holdButton);
            var accent = window.FindResource("AccentBrush");
            var secondary = window.FindResource("SecondaryTextBrush");

            ToggleEdgeHide(window);
            Assert.IsTrue(EdgeHideState(window).IsArmed);
            Assert.AreEqual(accent, edgeButton.Foreground, "待命态贴边按钮点亮。");

            // 互斥（后激活者胜）：激活保持展开即解除贴边待命，两按钮任何时刻不同时点亮。
            TogglePanelHold(window);

            Assert.IsTrue(PanelHoldActive(window), "保持展开接管互斥组。");
            Assert.AreEqual(
                EdgeHidePhase.Idle,
                EdgeHideState(window).Phase,
                "保持展开激活必须解除贴边待命。");
            Assert.AreEqual(secondary, edgeButton.Foreground, "贴边按钮前景即灭。");
            Assert.AreEqual(EdgeHideDefaultLabel, edgeButton.ToolTip, "解除后按钮文案回到默认。");
            Assert.AreEqual(accent, holdButton.Foreground, "保持展开按钮点亮。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void EdgeHide_MutexActivatingEdgeHideEndsPanelHold()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var edgeButton = (Button)window.FindName("EdgeHideButton");
            var holdButton = (Button)window.FindName("PanelHoldButton");
            Assert.IsNotNull(edgeButton);
            Assert.IsNotNull(holdButton);
            var accent = window.FindResource("AccentBrush");
            var secondary = window.FindResource("SecondaryTextBrush");

            TogglePanelHold(window);
            Assert.IsTrue(PanelHoldActive(window));
            Assert.AreEqual(accent, holdButton.Foreground, "保持展开态按钮点亮。");

            // 互斥（后激活者胜）：激活贴边待命即解除保持展开，两按钮任何时刻不同时点亮。
            ToggleEdgeHide(window);

            Assert.IsTrue(EdgeHideState(window).IsArmed, "贴边待命接管互斥组。");
            Assert.IsFalse(PanelHoldActive(window), "贴边待命激活必须解除保持展开。");
            Assert.AreEqual(secondary, holdButton.Foreground, "保持展开按钮前景即灭。");
            Assert.AreEqual(
                "保持展开，暂停自动收起",
                holdButton.ToolTip,
                "解除后按钮文案回到默认。");
            Assert.AreEqual(accent, edgeButton.Foreground, "贴边按钮点亮。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void EdgeHide_ExternalDropRailDisarmsPendingEdgeHide()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var button = (Button)window.FindName("EdgeHideButton");
            Assert.IsNotNull(button);

            ToggleEdgeHide(window);
            Assert.IsTrue(EdgeHideState(window).IsArmed);

            // 面板经外部拖放离开展开态且未进入 Peek → 自动解除，不留悬置 armed。
            InvokePrivate(window, "RevealExternalDropRail");
            CompleteLayout(window);

            Assert.AreEqual(EdgeHidePhase.Idle, EdgeHideState(window).Phase);
            Assert.AreEqual(EdgeHideDefaultLabel, button.ToolTip, "解除后按钮文案回到默认。");

            InvokePrivate(window, "HideExternalDropRail");
            CompleteLayout(window);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void EdgeHide_ButtonAccessibilityStateFollowsToggleAndArmedKeepsHeaderVisible()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var button = (Button)window.FindName("EdgeHideButton");
            Assert.IsNotNull(button);
            var actions = (StackPanel)window.FindName("HeaderActions");
            Assert.IsNotNull(actions);
            var accent = window.FindResource("AccentBrush");
            var secondary = window.FindResource("SecondaryTextBrush");

            Assert.AreEqual(EdgeHideDefaultLabel, button.ToolTip);
            Assert.AreEqual(
                EdgeHideDefaultLabel,
                System.Windows.Automation.AutomationProperties.GetName(button));
            Assert.AreEqual(secondary, button.Foreground, "默认前景与相邻头部按钮一致。");

            ToggleEdgeHide(window);
            Assert.AreEqual(EdgeHideArmedLabel, button.ToolTip);
            Assert.AreEqual(accent, button.Foreground, "待命态前景切强调色。");

            // 待命期间头部常显：指针离开（MouseLeave → SetHeaderActionsVisible(false)）
            // 也不得淡出，否则待命生效的瞬间就是指示消失的瞬间。
            InvokePrivate(window, "SetHeaderActionsVisible", false);
            Assert.IsTrue(FloatingTransferStation.Views.FadeAnimation.GetIsActive(actions));

            ToggleEdgeHide(window);
            InvokePrivate(window, "SetHeaderActionsVisible", false);
            Assert.IsFalse(
                FloatingTransferStation.Views.FadeAnimation.GetIsActive(actions),
                "解除待命后头部恢复默认悬停显隐。");
            Assert.AreEqual(EdgeHideDefaultLabel, button.ToolTip);
            Assert.AreEqual(secondary, button.Foreground);

            // peek 态：按钮保持强调色与 peek 文案，头部常显（收缩动画帧里的指示）。
            PeekWindowAtEdge(window);
            Assert.AreEqual(EdgePeekLabel, button.ToolTip, "peek 态按钮文案切换。");
            Assert.AreEqual(accent, button.Foreground, "peek 态前景保持强调色。");
            InvokePrivate(window, "SetHeaderActionsVisible", false);
            Assert.IsTrue(
                FloatingTransferStation.Views.FadeAnimation.GetIsActive(actions),
                "peek 期间头部常显。");
        }
        finally
        {
            RestoreAndClose(window);
        }
    }

    /// <summary>收尾：仍在 Peek 则先展开再关闭，让窗口回屏内。</summary>
    private static void RestoreAndClose(MainWindow window)
    {
        if (EdgeHideState(window).IsPeek)
        {
            InvokePrivate(window, "RestoreFromEdgeHide");
            CompleteLayout(window);
        }

        CloseWindow(window);
    }
}
