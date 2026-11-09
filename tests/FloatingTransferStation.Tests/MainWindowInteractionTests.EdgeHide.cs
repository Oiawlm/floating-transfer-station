using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 一次性贴边隐藏的窗口层契约（1.20.0）：armed + 指针离开 → 整窗同尺寸纯移出所有
/// 显示器（零交集）且面板保持展开；回位区驻留 ≥200ms → 恢复原矩形并解除；恢复后
/// 再次离开仅普通轨道收起；抑制复用（PanelHold 同样抑制）；热键与显示器变化兜底；
/// 其他收起路径解除 armed。迁移原子性：隐藏与恢复各只有一次矩形变更。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private static void ToggleEdgeHide(MainWindow window) =>
        InvokePrivate(
            window,
            "EdgeHideButton_Click",
            window.FindName("EdgeHideButton"),
            new RoutedEventArgs(Button.ClickEvent, window.FindName("EdgeHideButton")));

    private static EdgeHideStateMachine EdgeHideState(MainWindow window) =>
        GetPrivateField<EdgeHideStateMachine>(window, "_edgeHide");

    private static DispatcherTimer RecallTimer(MainWindow window) =>
        GetPrivateField<DispatcherTimer>(window, "_edgeHideRecallTimer");

    private static PhysicalRectangle RecallZone(MainWindow window) =>
        GetPrivateField<PhysicalRectangle>(window, "_edgeHideRecallZone");

    private static bool PanelHoldActive(MainWindow window) =>
        GetPrivateField<PanelStateMachine>(window, "_panelState").IsPanelHoldActive;

    /// <summary>
    /// armed + 模拟指针离开 + 收起节奏 tick → 整窗贴边隐藏。回位轮询真实定时器
    /// 立即停用（回位驻留由模拟轮询单独锁定），避免测试机真实光标位置影响几何断言。
    /// </summary>
    private static void HideWindowAtEdge(MainWindow window)
    {
        ToggleEdgeHide(window);
        InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
        InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
        RecallTimer(window).Stop();
        Assert.IsTrue(EdgeHideState(window).IsDocked, "前置条件：窗口已贴边隐藏。");
    }

    private static void AssertIntersectsNoMonitor(in NativeRect rectangle)
    {
        foreach (var monitor in MonitorBounds.AllMonitors())
        {
            Assert.IsTrue(
                rectangle.Right <= monitor.Left || rectangle.Left >= monitor.Right,
                $"隐藏矩形 {rectangle} 与显示器 [{monitor.Left},{monitor.Top},{monitor.Right},{monitor.Bottom}] 不得相交。");
        }
    }

    [STATestMethod]
    public void EdgeHide_ArmedLeaveMovesWindowOffscreenKeepsPanelExpanded()
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
            var expandedHeight = window.Height;
            var viewModel = (MainWindowViewModel)window.DataContext;
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            Assert.IsTrue(GetWindowRect(handle, out var before));

            HideWindowAtEdge(window);
            CompleteLayout(window);

            Assert.IsTrue(GetWindowRect(handle, out var after));
            Assert.AreEqual(
                before.Width,
                after.Width,
                1,
                "贴边隐藏是同尺寸纯移动，不得收起或缩放。");
            Assert.AreEqual(before.Height, after.Height, 1);
            AssertIntersectsNoMonitor(after);
            Assert.AreEqual(before.Top, after.Top, 1, "垂直位置不变。");

            Assert.IsTrue(viewModel.IsPanelExpanded, "隐藏期间面板保持展开、内容冻结。");
            Assert.AreEqual(expandedWidth, window.Width, 0.5);
            Assert.AreEqual(expandedHeight, window.Height, 0.5);
        }
        finally
        {
            RestoreAndClose(window);
        }
    }

    [STATestMethod]
    public void EdgeHide_RecallDwellRestoresOriginalPlacementAndDisarms()
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

            HideWindowAtEdge(window);
            var zone = RecallZone(window);
            var cursorX = (zone.Left + zone.Right) / 2;
            var cursorY = (zone.Top + zone.Bottom) / 2;

            // 首次命中只记起点；驻留不足 200ms 不得恢复。
            InvokePrivate(window, "HandleEdgeHideRecallPoll", cursorX, cursorY, 1000L);
            Assert.IsTrue(EdgeHideState(window).IsDocked);
            InvokePrivate(window, "HandleEdgeHideRecallPoll", cursorX, cursorY, 1149L);
            Assert.IsTrue(EdgeHideState(window).IsDocked);

            // 驻留达阈值 → 恢复原矩形原形并解除（一次性）。
            InvokePrivate(window, "HandleEdgeHideRecallPoll", cursorX, cursorY, 1200L);
            CompleteLayout(window);

            Assert.IsFalse(EdgeHideState(window).IsDocked);
            Assert.IsFalse(EdgeHideState(window).IsArmed);
            Assert.IsFalse(RecallTimer(window).IsEnabled, "恢复后必须停轮询。");
            Assert.IsTrue(GetWindowRect(handle, out var restored));
            Assert.AreEqual(before.Left, restored.Left, 1, "回位必须回到原矩形。");
            Assert.AreEqual(before.Top, restored.Top, 1);
            Assert.AreEqual(before.Width, restored.Width, 1);
            Assert.AreEqual(before.Height, restored.Height, 1);
            Assert.IsTrue(((MainWindowViewModel)window.DataContext).IsPanelExpanded);
        }
        finally
        {
            RestoreAndClose(window);
        }
    }

    [STATestMethod]
    public void EdgeHide_RecallLeaveResetsDwellProgress()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            HideWindowAtEdge(window);
            var zone = RecallZone(window);
            var insideX = (zone.Left + zone.Right) / 2;
            var insideY = (zone.Top + zone.Bottom) / 2;
            var outsideX = zone.Left - 100;
            var outsideY = zone.Top - 100;

            InvokePrivate(window, "HandleEdgeHideRecallPoll", insideX, insideY, 1000L);
            // 离开回位区即重置驻留进度，重新进入重新计时。
            InvokePrivate(window, "HandleEdgeHideRecallPoll", outsideX, outsideY, 1150L);
            InvokePrivate(window, "HandleEdgeHideRecallPoll", insideX, insideY, 1160L);
            InvokePrivate(window, "HandleEdgeHideRecallPoll", insideX, insideY, 1300L);
            Assert.IsTrue(
                EdgeHideState(window).IsDocked,
                "重进后 140ms 不得恢复（扫边/短停不得唤回）。");

            InvokePrivate(window, "HandleEdgeHideRecallPoll", insideX, insideY, 1361L);
            Assert.IsFalse(EdgeHideState(window).IsDocked, "重进后驻留 201ms 必须恢复。");
        }
        finally
        {
            RestoreAndClose(window);
        }
    }

    [STATestMethod]
    public void EdgeHide_AfterRecallNextLeaveCollapsesToRailNormally()
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

            HideWindowAtEdge(window);
            var zone = RecallZone(window);
            InvokePrivate(
                window,
                "HandleEdgeHideRecallPoll",
                (zone.Left + zone.Right) / 2,
                (zone.Top + zone.Bottom) / 2,
                1000L);
            InvokePrivate(
                window,
                "HandleEdgeHideRecallPoll",
                (zone.Left + zone.Right) / 2,
                (zone.Top + zone.Bottom) / 2,
                1300L);
            Assert.IsFalse(EdgeHideState(window).IsDocked);

            // 一次性：恢复后再次离开只剩普通轨道收起，不再次隐藏。
            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            CompleteLayout(window);

            Assert.IsFalse(viewModel.IsPanelExpanded, "恢复后离开必须普通收起。");
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
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            Assert.IsTrue(GetWindowRect(handle, out var before));

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
            // 贴边待命（armed 即灭），此后保持期间离开仍不得隐藏或收起。
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

            Assert.IsTrue(viewModel.IsPanelExpanded, "保持展开期间不得隐藏。");
            Assert.IsTrue(PanelHoldActive(window));
            Assert.AreEqual(expandedWidth, window.Width, 0.5);

            // 解除保持后的离开走常规收起（贴边待命已被互斥解除，不再恢复隐藏）。
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
            Assert.AreEqual(
                "一次性移出屏幕：鼠标离开后收进屏幕边缘，移回原位自动展开",
                edgeButton.ToolTip,
                "解除后按钮文案回到默认。");
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
    public void EdgeHide_GlobalHotkeyRestoresDockedWindow()
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

            HideWindowAtEdge(window);
            InvokePrivate(window, "OnGlobalHotkeyPressed");
            CompleteLayout(window);

            Assert.IsFalse(EdgeHideState(window).IsDocked, "热键是找回兜底，必须立即恢复。");
            Assert.IsTrue(GetWindowRect(handle, out var restored));
            Assert.AreEqual(before.Left, restored.Left, 1);
            Assert.AreEqual(before.Top, restored.Top, 1);
            Assert.AreEqual(before.Width, restored.Width, 1);
            Assert.IsTrue(((MainWindowViewModel)window.DataContext).IsPanelExpanded);
        }
        finally
        {
            RestoreAndClose(window);
        }
    }

    [STATestMethod]
    public void EdgeHide_DisplayEnvironmentChangeRestoresDockedWindow()
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

            HideWindowAtEdge(window);
            InvokePrivate(window, "OnDisplayEnvironmentChanged");
            CompleteLayout(window);

            // 显示器布局变化后离屏窗口可能失去回位路径，必须立即恢复（防找不到窗口）。
            Assert.IsFalse(EdgeHideState(window).IsDocked);
            Assert.IsTrue(GetWindowRect(handle, out var restored));
            Assert.AreEqual(before.Left, restored.Left, 1);
            Assert.AreEqual(before.Width, restored.Width, 1);
        }
        finally
        {
            RestoreAndClose(window);
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

            // 面板经外部拖放离开展开态且未进入 Docked → 自动解除，不留悬置 armed。
            InvokePrivate(window, "RevealExternalDropRail");
            CompleteLayout(window);

            Assert.AreEqual(EdgeHidePhase.Idle, EdgeHideState(window).Phase);
            Assert.AreEqual(
                "一次性移出屏幕：鼠标离开后收进屏幕边缘，移回原位自动展开",
                button.ToolTip,
                "解除后按钮文案回到默认。");

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
            var defaultLabel = "一次性移出屏幕：鼠标离开后收进屏幕边缘，移回原位自动展开";

            Assert.AreEqual(defaultLabel, button.ToolTip);
            Assert.AreEqual(
                defaultLabel,
                System.Windows.Automation.AutomationProperties.GetName(button));
            Assert.AreEqual(secondary, button.Foreground, "默认前景与相邻头部按钮一致。");

            ToggleEdgeHide(window);
            Assert.AreEqual(
                "已准备贴边隐藏，鼠标移开即移出屏幕；再按一次取消",
                button.ToolTip);
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
            Assert.AreEqual(defaultLabel, button.ToolTip);
            Assert.AreEqual(secondary, button.Foreground);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    /// <summary>收尾：仍在 Docked 则先恢复再关闭，让窗口离屏前回到屏幕内。</summary>
    private static void RestoreAndClose(MainWindow window)
    {
        if (EdgeHideState(window).IsDocked)
        {
            InvokePrivate(window, "RestoreFromEdgeHide");
            CompleteLayout(window);
        }

        CloseWindow(window);
    }
}
